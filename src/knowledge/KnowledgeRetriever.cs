// KnowledgeRetriever.cs — RAG knowledge index with hybrid retrieval.
// Builds a chunk index from markdown documents, then serves queries via:
//   1. Hybrid mode (BM25 + API Embedding + RRF fusion), or
//   2. Local-only (TF-IDF/BM25), or
//   3. API-only (cosine similarity on embedding vectors).
// Chunks are deduplicated by stable Id, not Content text, to avoid silent
// overwrites when GIS standards documents contain repeated boilerplate.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using SpatialAgent.Core;
using Newtonsoft.Json;
using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Knowledge
{
    public class KnowledgeChunk
    {
        [JsonProperty("id")] public string Id { get; set; } = "";
        [JsonProperty("title")] public string Title { get; set; } = "";
        [JsonProperty("content")] public string Content { get; set; } = "";
        [JsonProperty("source")] public string Source { get; set; } = "";
        [JsonProperty("category")] public string Category { get; set; } = "";
        [JsonProperty("embedding")] public float[]? Embedding { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        [Newtonsoft.Json.JsonIgnore]
        public double Score { get; set; }
    }

    public class KnowledgeRetriever
    {
        private readonly HybridEmbeddingService? _hybrid;
        private readonly IEmbeddingService _embedding;
        private readonly string _knowledgeDir;
        private readonly string _indexPath;
        private List<KnowledgeChunk> _chunks = new();
        private string _sourceFingerprint = "";
        private const double RRF_K = 60.0;

        public bool IsIndexed { get; private set; }
        public int ChunkCount => _chunks.Count;
        public string EmbeddingMode => _hybrid != null ? _hybrid.Name : _embedding.Name;
        public bool IsHybridMode => _hybrid != null;
        public bool IsLocalReady => _hybrid?.IsLocalReady ?? (_embedding.IsLocal && _embedding.IsReady);
        public bool IsApiReady => _hybrid?.IsApiReady ?? (!_embedding.IsLocal && _embedding.IsReady);

        public KnowledgeRetriever(IEmbeddingService embedding, string knowledgeDir)
        {
            _embedding = embedding;
            _knowledgeDir = knowledgeDir;
            _indexPath = Path.Combine(knowledgeDir, "index.json");
        }

        public KnowledgeRetriever(HybridEmbeddingService hybrid, string knowledgeDir)
        {
            _hybrid = hybrid;
            _embedding = hybrid;
            _knowledgeDir = knowledgeDir;
            _indexPath = Path.Combine(knowledgeDir, "index.json");
        }

        public async Task BuildIndexAsync()
        {
            if (!Directory.Exists(_knowledgeDir)) { Directory.CreateDirectory(_knowledgeDir); return; }
            var mdFiles = Directory.GetFiles(_knowledgeDir, "*.md", SearchOption.AllDirectories);
            _sourceFingerprint = ComputeSourceFingerprint(mdFiles);
            if (File.Exists(_indexPath))
            {
                bool loaded = await LoadIndexAsync(_sourceFingerprint);
                if (loaded && _chunks.Count > 0)
                {
                    IsIndexed = true;
                    return;
                }
            }
            _chunks.Clear();
            foreach (var file in mdFiles)
            {
                string content = await File.ReadAllTextAsync(file);
                string relativePath = Path.GetRelativePath(_knowledgeDir, file);
                var chunks = SplitByHeadings(content);
                foreach (var (title, chunkContent) in chunks)
                {
                    if (string.IsNullOrWhiteSpace(chunkContent)) continue;
                    _chunks.Add(new KnowledgeChunk { Id = $"{relativePath}#{title}", Title = title, Content = chunkContent.Trim(), Source = relativePath, Category = Path.GetDirectoryName(relativePath)?.Replace("\\", "/") ?? "general" });
                }
            }
            await _embedding.BuildVocabularyAsync(_chunks.Select(c => c.Content));
            if (!_embedding.IsLocal && _chunks.Count > 0)
            {
                foreach (var chunk in _chunks) { if (string.IsNullOrWhiteSpace(chunk.Content)) continue; chunk.Embedding = await _embedding.EmbedAsync(chunk.Content); }
            }
            IsIndexed = _chunks.Count > 0;
            await SaveIndexAsync();
        }

        public async Task<List<KnowledgeChunk>> SearchAsync(string query, int topK = 5)
        {
            if (!IsIndexed || _chunks.Count == 0) return new List<KnowledgeChunk>();

            if (_hybrid != null && _hybrid.IsLocalReady && _hybrid.IsApiReady)
            {
                // Hybrid: dual-path retrieval + RRF fusion
                return await HybridSearchAsync(query, topK);
            }
            if (_embedding.IsLocal && _embedding is TfidfEmbeddingService tfidf)
            {
                var contents = _chunks.Select(c => c.Content).ToList();
                var scored = tfidf.Search(contents, query, topK);
                return MapToChunks(scored);
            }
            else
            {
                float[]? qEmb = await _embedding.EmbedAsync(query);
                if (qEmb == null || qEmb.Length == 0) return KeywordSearch(query, topK);
                foreach (var chunk in _chunks) { if (chunk.Embedding == null) continue; chunk.Score = CosineSimilarity(qEmb, chunk.Embedding); }
                return _chunks.Where(c => c.Embedding != null).OrderByDescending(c => c.Score).Take(topK).ToList();
            }
        }

        private async Task<List<KnowledgeChunk>> HybridSearchAsync(string query, int topK)
        {
            var idToChunk = _chunks.ToDictionary(c => c.Id, c => c);
            var rrfScores = new Dictionary<string, double>();

            // Build a content→Id lookup. When multiple chunks share the same content
            // (common in GIS standards documents with repeated boilerplate), we keep
            // all of them by mapping each chunk index to its Id, then using the index
            // to resolve scored results. This avoids silent overwrites that happen with
            // content-as-key dictionaries.
            var contents = _chunks.Select(c => c.Content).ToList();

            // Path 1: local BM25
            var localResults = _hybrid!.LocalSearch(contents, query, topK * 2);
            for (int i = 0; i < localResults.Count; i++)
            {
                // Resolve via chunk index (LocalSearch preserves input order)
                int idx = contents.IndexOf(localResults[i].Content);
                string key = idx >= 0 ? _chunks[idx].Id : localResults[i].Content;
                rrfScores[key] = rrfScores.GetValueOrDefault(key, 0) + 1.0 / (RRF_K + i + 1);
            }

            // Path 2: API embedding
            try
            {
                float[]? qEmb = await _embedding.EmbedAsync(query);
                if (qEmb != null && qEmb.Length > 0)
                {
                    foreach (var chunk in _chunks)
                    {
                        if (chunk.Embedding == null) continue;
                        chunk.Score = CosineSimilarity(qEmb, chunk.Embedding);
                    }
                    var apiResults = _chunks.Where(c => c.Embedding != null)
                        .OrderByDescending(c => c.Score).Take(topK * 2).ToList();
                    for (int i = 0; i < apiResults.Count; i++)
                    {
                        rrfScores[apiResults[i].Id] = rrfScores.GetValueOrDefault(apiResults[i].Id, 0) + 1.0 / (RRF_K + i + 1);
                    }
                }
            }
            catch (Exception ex) { AgentDiagnostics.Warn("KnowledgeRetriever.HybridSearch", "API search failed, falling back to local", ex); }

            if (rrfScores.Count == 0) return new List<KnowledgeChunk>();

            return rrfScores.OrderByDescending(kv => kv.Value).Take(topK)
                .Select(kv => { if (idToChunk.TryGetValue(kv.Key, out var c)) { c.Score = kv.Value; return c; } return null; })
                .Where(c => c != null).Cast<KnowledgeChunk>().ToList();
        }

        public List<KnowledgeChunk> KeywordSearch(string query, int topK = 5)
        {
            if (_hybrid != null)
            {
                var contents = _chunks.Select(c => c.Content).ToList();
                var scored = _hybrid.LocalKeywordSearch(contents, query, topK);
                return MapToChunks(scored);
            }
            if (_embedding is TfidfEmbeddingService tfidf)
            {
                var contents = _chunks.Select(c => c.Content).ToList();
                return MapToChunks(tfidf.KeywordSearch(contents, query, topK));
            }
            var keywords = TfidfEmbeddingService.Tokenize(query).Where(t => t.Length >= 2).ToHashSet();
            return _chunks.Select(c => { var tokens = TfidfEmbeddingService.Tokenize(c.Content).ToHashSet(); c.Score = keywords.Count(k => tokens.Contains(k)); return c; }).Where(c => c.Score > 0).OrderByDescending(c => c.Score).Take(topK).ToList();
        }

        public async Task AddKnowledgeAsync(string title, string content, string category)
        {
            var chunk = new KnowledgeChunk { Id = $"{category}/{title}", Title = title, Content = content, Category = category, Source = $"{category}/{title}.md" };
            if (!_embedding.IsLocal) chunk.Embedding = await _embedding.EmbedAsync(content);
            _chunks.Add(chunk);
            await _embedding.BuildVocabularyAsync(_chunks.Select(c => c.Content));
            IsIndexed = true;
            await SaveIndexAsync();
        }

        private List<KnowledgeChunk> MapToChunks(List<ScoredDocument> scored)
        {
            var contents = _chunks.Select(c => c.Content).ToList();
            var results = new List<KnowledgeChunk>();
            var consumed = new HashSet<int>();
            foreach (var sd in scored)
            {
                // Find first unconsumed chunk with matching content (handles duplicates).
                int idx = contents.FindIndex(c => c == sd.Content);
                while (idx >= 0 && consumed.Contains(idx))
                    idx = contents.FindIndex(idx + 1, c => c == sd.Content);
                if (idx >= 0)
                {
                    consumed.Add(idx);
                    _chunks[idx].Score = sd.Score;
                    results.Add(_chunks[idx]);
                }
            }
            return results;
        }

        private static List<(string, string)> SplitByHeadings(string content)
        {
            var result = new List<(string, string)>();
            var lines = content.Split('\n');
            string ct = "概述";
            var sb = new System.Text.StringBuilder();
            foreach (var line in lines)
            {
                // Split on H1, H2, H3 headings (H4+ treated as body)
                bool isH1 = line.StartsWith("# ") && !line.StartsWith("## ");
                bool isH2 = line.StartsWith("## ") && !line.StartsWith("### ");
                bool isH3 = line.StartsWith("### ") && !line.StartsWith("#### ");
                if (isH1 || isH2 || isH3) {
                    if (sb.Length > 0) { result.Add((ct, sb.ToString())); sb.Clear(); }
                    ct = line[(isH3 ? 4 : isH2 ? 3 : 2)..].Trim();
                }
                else sb.AppendLine(line);
            }
            if (sb.Length > 0) result.Add((ct, sb.ToString()));
            return result;
        }

        private static double CosineSimilarity(float[] a, float[] b)
        {
            if (a.Length != b.Length) return 0;
            double d = 0, na = 0, nb = 0;
            for (int i = 0; i < a.Length; i++) { d += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
            if (na == 0 || nb == 0) return 0;
            return d / (Math.Sqrt(na) * Math.Sqrt(nb));
        }

        private async Task SaveIndexAsync()
        {
            try { var data = new { schemaVersion = 2, sourceFingerprint = _sourceFingerprint, embeddingMode = EmbeddingMode, isHybrid = _hybrid != null, chunks = _chunks, updatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm") }; string json = JsonConvert.SerializeObject(data, Formatting.Indented); Directory.CreateDirectory(_knowledgeDir); await File.WriteAllTextAsync(_indexPath, json); } catch (Exception ex) { AgentDiagnostics.Warn("KnowledgeRetriever", "Failed to save knowledge index", ex); }
        }

        private async Task<bool> LoadIndexAsync(string expectedFingerprint)
        {
            try
            {
                string json = await File.ReadAllTextAsync(_indexPath);
                var data = JsonConvert.DeserializeObject<dynamic>(json);
                string storedFingerprint = data?.sourceFingerprint?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(storedFingerprint)
                    || !storedFingerprint.Equals(expectedFingerprint, StringComparison.Ordinal))
                    return false;
                if (data?.chunks != null)
                {
                    _chunks = JsonConvert.DeserializeObject<List<KnowledgeChunk>>(data.chunks.ToString()) ?? new List<KnowledgeChunk>();
                    await _embedding.BuildVocabularyAsync(_chunks.Select(c => c.Content));
                    IsIndexed = _chunks.Count > 0;
                    return true;
                }
            }
            catch (Exception ex) { AgentDiagnostics.Warn("KnowledgeRetriever", "Failed to load knowledge index", ex); }
            return false;
        }

        private string ComputeSourceFingerprint(IEnumerable<string> files)
        {
            var manifest = files
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path =>
                {
                    var info = new FileInfo(path);
                    return $"{Path.GetRelativePath(_knowledgeDir, path).Replace('\\', '/')}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
                });
            byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", manifest)));
            return Convert.ToHexString(digest).ToLowerInvariant();
        }
    }
}
