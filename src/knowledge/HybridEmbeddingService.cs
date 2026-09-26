using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

// HybridEmbeddingService.cs — Dual-path retrieval with RRF fusion.
// 1. Local BM25: domain term matching, works offline.
// 2. API Embedding: semantic understanding for open-ended queries.
// 3. Reciprocal Rank Fusion (RRF): merges both result lists.
// Falls back gracefully when one path is unavailable.

namespace SpatialAgent.Knowledge
{
    /// <summary>
    /// Manages both local (TF-IDF/BM25) and API embedding services,
    /// providing unified retrieval with automatic fallback.
    /// </summary>
    public class HybridEmbeddingService : IEmbeddingService
    {
        private readonly TfidfEmbeddingService _local;
        private readonly ApiEmbeddingService _api;
        private const double RRF_K = 60.0;

        public string Name => $"Hybrid (local BM25 + API {_api.Name})";
        public bool IsLocal => false;
        public int Dimension => _api.Dimension > 0 ? _api.Dimension : _local.Dimension;
        public bool IsReady => _local.IsReady || _api.IsReady;

        /// <summary>Whether the local BM25 index is loaded.</summary>
        public bool IsLocalReady => _local.IsReady;

        /// <summary>Whether the API embedding endpoint is reachable.</summary>
        public bool IsApiReady => _api.IsReady;

        /// <summary>Human-readable active retrieval mode.</summary>
        public string ActiveMode => (IsLocalReady, IsApiReady) switch
        {
            (true, true) => "dual (BM25 + embedding)",
            (true, false) => "local BM25 only",
            (false, true) => "API embedding only",
            _ => "not ready"
        };

        public HybridEmbeddingService(TfidfEmbeddingService local, ApiEmbeddingService api)
        {
            _local = local;
            _api = api;
        }

        // ── IEmbeddingService ──

        public async Task<float[]> EmbedAsync(string text)
        {
            if (_api.IsReady)
            {
                var vec = await _api.EmbedAsync(text);
                if (vec.Length > 0) return vec;
            }
            return await _local.EmbedAsync(text);
        }

        public async Task<float[][]> EmbedBatchAsync(IEnumerable<string> texts)
        {
            if (_api.IsReady)
                return await _api.EmbedBatchAsync(texts);
            return await _local.EmbedBatchAsync(texts);
        }

        public async Task BuildVocabularyAsync(IEnumerable<string> corpus)
        {
            await _local.BuildVocabularyAsync(corpus);
        }

        // ── Delegating search methods (RRF fusion lives in KnowledgeRetriever) ──

        /// <summary>Local BM25 ranked search.</summary>
        public List<ScoredDocument> LocalSearch(IEnumerable<string> candidates, string query, int topK = 10)
        {
            return _local.Search(candidates, query, topK);
        }

        /// <summary>Local keyword-based search (no TF-IDF weighting).</summary>
        public List<ScoredDocument> LocalKeywordSearch(IEnumerable<string> candidates, string query, int topK = 10)
        {
            return _local.KeywordSearch(candidates, query, topK);
        }

        // ── Diagnostics ──

        public string Stats =>
            $"local: {(_local.IsReady ? $"vocab {_local.VocabSize} | docs {_local.DocumentCount}" : "not ready")} | " +
            $"API: {(_api.IsReady ? "connected" : "not connected")} | " +
            $"mode: {ActiveMode}";
    }
}
