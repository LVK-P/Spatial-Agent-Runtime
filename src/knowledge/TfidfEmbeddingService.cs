// TfidfEmbeddingService.cs — Local TF-IDF + BM25 embedding service.
// Runs entirely offline with zero external dependencies. Designed for
// Chinese text semantic search in GIS domain contexts.
//
// Pipeline:
//   1. Tokenize: jieba dictionary segmentation (preferred), or character
//      N-gram (bigram + unigram) fallback for Chinese; word splitting for English.
//   2. Weight: TF-IDF (term frequency × inverse document frequency).
//   3. Search: Okapi BM25 ranking.
//   4. Vector: sparse (Dictionary<int,float>) → dense float[] for cosine.
//
// The jieba custom dictionary (gis_dict.txt, 427 terms) is loaded at static
// init so domain terminology like "三区三线" is segmented correctly.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using JiebaNet.Segmenter;

namespace SpatialAgent.Knowledge
{
    /// <summary>
    /// Local TF-IDF + BM25 embedding service for offline knowledge-base search.
    /// </summary>
    public class TfidfEmbeddingService : IEmbeddingService
    {
        // ── jieba tokenizer (static init with GIS custom dictionary) ──
        private static readonly JiebaSegmenter? _jieba;

        static TfidfEmbeddingService()
        {
            try
            {
                _jieba = new JiebaSegmenter();
                string? dictPath = SpatialAgent.Runtime.Contracts.ResourcePaths.Find("gis_dict.txt");
                if (dictPath != null)
                {
                    foreach (string line in File.ReadLines(dictPath))
                    {
                        string word = line.Trim();
                        if (word.Length == 0 || word.StartsWith("#")) continue;
                        int space = word.LastIndexOf(' ');
                        if (space > 0) word = word[..space].Trim();
                        if (word.Length > 0)
                            _jieba.AddWord(word);
                    }
                }
            }
            catch { _jieba = null; }
        }

        // ── BM25 hyperparameters ──
        private const double BM25_K1 = 1.5;
        private const double BM25_B = 0.75;

        // ── Vocabulary ──
        private readonly Dictionary<string, int> _vocab = new();      // token → index
        private readonly List<string> _indexToToken = new();           // index → token
        private readonly Dictionary<int, int> _docFreq = new();        // index → document frequency
        private int _docCount;
        private double _avgDocLength;

        // ── Document cache (doc string → term frequency map) ──
        private readonly Dictionary<string, Dictionary<int, int>> _docTermFreqs = new();

        // ── Stop words (Chinese + English) ──
        private static readonly HashSet<string> StopTokens = new()
        {
            "的", "了", "在", "是", "我", "有", "和", "就", "不", "人", "都", "一", "一个",
            "上", "也", "很", "到", "说", "要", "去", "你", "会", "着", "没有", "看", "好",
            "自己", "这", "他", "她", "它", "们", "那", "些", "所", "为", "所以", "因为",
            "但是", "然而", "而且", "或者", "如果", "虽然", "可以", "这个", "那个", "什么",
            "怎么", "哪", "吗", "啊", "呢", "吧", "哦", "嗯", "哈",
            "the", "a", "an", "is", "are", "was", "were", "be", "been", "being",
            "have", "has", "had", "do", "does", "did", "will", "would", "could",
            "should", "may", "might", "can", "shall", "to", "of", "in", "for",
            "on", "with", "at", "by", "from", "as", "into", "through", "during",
            "and", "but", "or", "not", "no", "if", "then", "than", "that", "this",
            "it", "its", "he", "she", "they", "them", "we", "you", "I", "me", "my"
        };

        // ── IEmbeddingService properties ──
        public string Name => "TF-IDF (Local)";
        public bool IsLocal => true;
        public int Dimension => _vocab.Count;
        public bool IsReady => _vocab.Count > 0 && _docCount > 0;

        // ── Vocabulary building ──
        public Task BuildVocabularyAsync(IEnumerable<string> corpus)
        {
            _vocab.Clear();
            _indexToToken.Clear();
            _docFreq.Clear();
            _docTermFreqs.Clear();
            _docCount = 0;

            var docs = corpus.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
            if (docs.Count == 0) return Task.CompletedTask;

            int totalLength = 0;

            foreach (var doc in docs)
            {
                var tokens = Tokenize(doc);
                if (tokens.Count == 0) continue;

                totalLength += tokens.Count;
                _docCount++;

                var termFreq = new Dictionary<int, int>();

                foreach (var token in tokens.Distinct())
                {
                    int idx = GetOrAddToken(token);
                    if (!_docFreq.ContainsKey(idx)) _docFreq[idx] = 0;
                    _docFreq[idx]++;
                }

                foreach (var token in tokens)
                {
                    int idx = _vocab[token];
                    if (!termFreq.ContainsKey(idx)) termFreq[idx] = 0;
                    termFreq[idx]++;
                }

                _docTermFreqs[doc] = termFreq;
            }

            _avgDocLength = _docCount > 0 ? (double)totalLength / _docCount : 1.0;

            return Task.CompletedTask;
        }

        // ── Embedding (text → dense float vector) ──
        public Task<float[]> EmbedAsync(string text)
        {
            if (!IsReady || string.IsNullOrWhiteSpace(text))
                return Task.FromResult(Array.Empty<float>());

            var tokens = Tokenize(text);
            var termFreq = new Dictionary<int, int>();
            foreach (var t in tokens)
            {
                if (!_vocab.TryGetValue(t, out int idx)) continue;
                if (!termFreq.ContainsKey(idx)) termFreq[idx] = 0;
                termFreq[idx]++;
            }

            int docLen = tokens.Count;

            // TF-IDF vector
            var vector = new float[Dimension];
            foreach (var (idx, tf) in termFreq)
            {
                double idf = Math.Log((_docCount - _docFreq.GetValueOrDefault(idx, 0) + 0.5) /
                                     (_docFreq.GetValueOrDefault(idx, 0) + 0.5) + 1.0);
                double tfNorm = (double)tf / docLen;
                vector[idx] = (float)(tfNorm * idf);
            }

            // L2 normalization
            float norm = 0;
            for (int i = 0; i < vector.Length; i++) norm += vector[i] * vector[i];
            norm = (float)Math.Sqrt(norm);
            if (norm > 0)
                for (int i = 0; i < vector.Length; i++) vector[i] /= norm;

            return Task.FromResult(vector);
        }

        public async Task<float[][]> EmbedBatchAsync(IEnumerable<string> texts)
        {
            var tasks = texts.Select(EmbedAsync);
            return await Task.WhenAll(tasks);
        }

        // ── BM25 search (direct term-frequency ranking, no vector needed) ──
        /// <summary>
        /// Rank candidate documents using Okapi BM25.
        /// More accurate than TF-IDF vector + cosine similarity for text search.
        /// </summary>
        public List<ScoredDocument> Search(IEnumerable<string> candidates, string query, int topK = 5)
        {
            if (!IsReady || string.IsNullOrWhiteSpace(query))
                return new List<ScoredDocument>();

            var queryTokens = Tokenize(query).Where(t => _vocab.ContainsKey(t)).ToList();
            if (queryTokens.Count == 0)
                return KeywordSearch(candidates, query, topK);

            var scored = new List<ScoredDocument>();

            foreach (var doc in candidates)
            {
                if (!_docTermFreqs.TryGetValue(doc, out var termFreq)) continue;

                double score = 0;
                int docLen = termFreq.Values.Sum();

                foreach (var qt in queryTokens)
                {
                    if (!_vocab.TryGetValue(qt, out int idx)) continue;
                    int tf = termFreq.GetValueOrDefault(idx, 0);
                    if (tf == 0) continue;

                    int df = _docFreq.GetValueOrDefault(idx, 1);
                    double idf = Math.Log((_docCount - df + 0.5) / (df + 0.5) + 1.0);
                    double numerator = tf * (BM25_K1 + 1);
                    double denominator = tf + BM25_K1 * (1 - BM25_B + BM25_B * docLen / _avgDocLength);
                    score += idf * numerator / denominator;
                }

                if (score > 0)
                    scored.Add(new ScoredDocument { Content = doc, Score = score });
            }

            return scored.OrderByDescending(s => s.Score).Take(topK).ToList();
        }

        // ── Fallback: keyword overlap search ──
        /// <summary>
        /// Coarse keyword match used when BM25 has no vocabulary coverage for the query.
        /// Longer matching tokens receive a length bonus (domain terms tend to be multi-character).
        /// </summary>
        public List<ScoredDocument> KeywordSearch(IEnumerable<string> candidates, string query, int topK = 5)
        {
            var qTokens = Tokenize(query).Where(t => !StopTokens.Contains(t)).ToHashSet();
            var results = new List<ScoredDocument>();

            foreach (var doc in candidates)
            {
                var dTokens = Tokenize(doc).ToHashSet();
                int hits = qTokens.Count(t => dTokens.Contains(t));
                if (hits > 0)
                {
                    double bonus = qTokens.Where(t => t.Length >= 2 && dTokens.Contains(t))
                                         .Sum(t => t.Length * 0.1);
                    results.Add(new ScoredDocument { Content = doc, Score = hits + bonus });
                }
            }

            return results.OrderByDescending(s => s.Score).Take(topK).ToList();
        }

        // ── Tokenization (Chinese-friendly: jieba → character N-gram fallback) ──
        /// <summary>
        /// Tokenize Chinese text. Uses jieba dictionary segmentation when available,
        /// falls back to character bigram + unigram for pure Chinese segments.
        /// English/numbers are split on whitespace.
        /// Example: "国土空间规划" → ["国土", "空间", "规划"] (jieba) or
        /// ["国土", "土空", "空间", "间规", "规划", "国", "土", "空", "间", "规", "划"] (N-gram).
        /// </summary>
        public static List<string> Tokenize(string text)
        {
            var tokens = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return tokens;

            // jieba dictionary-based segmentation (preferred for Chinese).
            if (_jieba != null)
            {
                var segments = _jieba.Cut(text, cutAll: false);
                foreach (var seg in segments)
                {
                    string word = seg.Trim().ToLowerInvariant();
                    if (word.Length == 0 || StopTokens.Contains(word))
                        continue;
                    tokens.Add(word);
                }
                return tokens;
            }

            // Fallback: character N-gram for Chinese, word splitting for English.
            var parts = Regex.Split(text, @"[^一-鿿\w]+");

            foreach (var seg in parts)
            {
                if (string.IsNullOrEmpty(seg)) continue;

                string cleaned = seg.Trim().ToLowerInvariant();

                // Pure Chinese segment → bigram + unigram
                if (Regex.IsMatch(cleaned, @"^[一-鿿]+$"))
                {
                    for (int i = 0; i < cleaned.Length - 1; i++)
                    {
                        string bigram = cleaned.Substring(i, 2);
                        if (!StopTokens.Contains(bigram))
                            tokens.Add(bigram);
                    }

                    foreach (char c in cleaned)
                    {
                        string unigram = c.ToString();
                        if (!StopTokens.Contains(unigram))
                            tokens.Add(unigram);
                    }
                }
                else
                {
                    // English/numeric → split on whitespace
                    var words = Regex.Split(cleaned, @"\s+");
                    foreach (var word in words)
                    {
                        if (word.Length == 0) continue;
                        string lower = word.ToLowerInvariant();
                        if (!StopTokens.Contains(lower))
                            tokens.Add(lower);
                    }
                }
            }

            return tokens;
        }

        // ── Helpers ──

        private int GetOrAddToken(string token)
        {
            if (!_vocab.TryGetValue(token, out int idx))
            {
                idx = _vocab.Count;
                _vocab[token] = idx;
                _indexToToken.Add(token);
            }
            return idx;
        }

        /// <summary>Vocabulary size.</summary>
        public int VocabSize => _vocab.Count;

        /// <summary>Number of indexed documents.</summary>
        public int DocumentCount => _docCount;

        /// <summary>One-line diagnostics summary.</summary>
        public string Stats => $"vocab: {VocabSize} tokens | docs: {DocumentCount} | avg len: {_avgDocLength:F1}";
    }

    /// <summary>
    /// A document with a relevance score (used by local search methods).
    /// </summary>
    public class ScoredDocument
    {
        public string Content { get; set; } = "";
        public double Score { get; set; }
    }
}
