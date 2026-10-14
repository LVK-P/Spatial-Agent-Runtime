using System.Collections.Generic;
using System.Threading.Tasks;

// IEmbeddingService.cs — Text vectorization service contract.
// Implementations:
//   TfidfEmbeddingService — local TF-IDF + BM25, zero external dependencies.
//   ApiEmbeddingService   — LLM API embedding (DeepSeek/OpenAI compatible).
//   HybridEmbeddingService — dual-path (BM25 + API) with automatic fallback.
// 中文：IEmbeddingService.cs — 文本矢量化服务合同。实现方式：TfidfEmbeddingService — 本地 TF-IDF + BM25，零外部依赖。ApiEmbeddingService — LLM API 嵌入（兼容 DeepSeek/OpenAI）。HybridEmbeddingService — 双路径（BM25 + API）带自动回退。

namespace SpatialAgent.Knowledge
{
    /// <summary>
    /// Converts text to dense vectors for semantic search.
    /// Three implementations cover local-only, API-only, and hybrid modes.
    /// </summary>
    /// <remarks>中文：将文本转换为密集向量以实现语义搜索。三种实现涵盖了仅本地、仅API和混合模式。</remarks>
    public interface IEmbeddingService
    {
        /// <summary>Human-readable service name for diagnostics.</summary>
        /// <remarks>中文：诊断的人类可读服务名称。</remarks>
        string Name { get; }

        /// <summary>True if the service runs entirely offline.</summary>
        /// <remarks>中文：如果服务完全离线，那是真的。</remarks>
        bool IsLocal { get; }

        /// <summary>Output vector dimensionality (0 if unknown).</summary>
        /// <remarks>中文：输出向量维度（如果未知则为0）。</remarks>
        int Dimension { get; }

        /// <summary>Whether the service is initialized and ready for queries.</summary>
        /// <remarks>中文：服务是否已初始化并准备好查询。</remarks>
        bool IsReady { get; }

        /// <summary>Encode a single text as a float vector.</summary>
        /// <remarks>中文：将单个文本编码为浮点矢量。</remarks>
        Task<float[]> EmbedAsync(string text);

        /// <summary>Encode multiple texts in one batch.</summary>
        /// <remarks>中文：一次编码多条文本。</remarks>
        Task<float[][]> EmbedBatchAsync(IEnumerable<string> texts);

        /// <summary>
        /// Build or refresh the vocabulary/index from a corpus.
        /// Local services rebuild token indices; API-backed services are a no-op.
        /// </summary>
        /// <remarks>中文：从语料库建立或刷新词汇表/索引。本地服务重建词元索引；API 服务无需执行此操作。</remarks>
        Task BuildVocabularyAsync(IEnumerable<string> corpus);
    }
}
