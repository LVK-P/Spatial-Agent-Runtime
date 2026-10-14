using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

// HybridEmbeddingService.cs — Dual-path retrieval with RRF fusion.
// 1. Local BM25: domain term matching, works offline.
// 2. API Embedding: semantic understanding for open-ended queries.
// 3. Reciprocal Rank Fusion (RRF): merges both result lists.
// Falls back gracefully when one path is unavailable.
// 中文：HybridEmbeddingService.cs — 使用 RRF 融合的双路径检索。1. 本地 BM25：匹配领域词汇，可离线运行；2. API 嵌入：理解开放式查询的语义；3. 倒数排名融合（RRF）：合并两组结果。当某一路径不可用时平稳回退。

namespace SpatialAgent.Knowledge
{
    /// <summary>
    /// Manages both local (TF-IDF/BM25) and API embedding services,
    /// providing unified retrieval with automatic fallback.
    /// </summary>
    /// <remarks>中文：管理本地（TF-IDF/BM25）和 API 嵌入服务，提供统一检索与自动回退。</remarks>
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
        /// <remarks>中文：本地BM25指数是否已加载。</remarks>
        public bool IsLocalReady => _local.IsReady;

        /// <summary>Whether the API embedding endpoint is reachable.</summary>
        /// <remarks>中文：API嵌入端点是否可访问。</remarks>
        public bool IsApiReady => _api.IsReady;

        /// <summary>Human-readable active retrieval mode.</summary>
        /// <remarks>中文：人类可读的主动检索模式。</remarks>
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
        // 中文：IEmbeddingService

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
        // 中文：委派搜索方法（RRF 融合存在于 KnowledgeRetriever）

        /// <summary>Local BM25 ranked search.</summary>
        /// <remarks>中文：本地BM25排名搜索。</remarks>
        public List<ScoredDocument> LocalSearch(IEnumerable<string> candidates, string query, int topK = 10)
        {
            return _local.Search(candidates, query, topK);
        }

        /// <summary>Local keyword-based search (no TF-IDF weighting).</summary>
        /// <remarks>中文：基于本地关键词的搜索（无TF-IDF加权）。</remarks>
        public List<ScoredDocument> LocalKeywordSearch(IEnumerable<string> candidates, string query, int topK = 10)
        {
            return _local.KeywordSearch(candidates, query, topK);
        }

        // ── Diagnostics ──
        // 中文：诊断

        public string Stats =>
            $"local: {(_local.IsReady ? $"vocab {_local.VocabSize} | docs {_local.DocumentCount}" : "not ready")} | " +
            $"API: {(_api.IsReady ? "connected" : "not connected")} | " +
            $"mode: {ActiveMode}";
    }
}
