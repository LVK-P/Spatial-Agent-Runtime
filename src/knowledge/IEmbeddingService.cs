using System.Collections.Generic;
using System.Threading.Tasks;

// IEmbeddingService.cs — Text vectorization service contract.
// Implementations:
//   TfidfEmbeddingService — local TF-IDF + BM25, zero external dependencies.
//   ApiEmbeddingService   — LLM API embedding (DeepSeek/OpenAI compatible).
//   HybridEmbeddingService — dual-path (BM25 + API) with automatic fallback.

namespace SpatialAgent.Knowledge
{
    /// <summary>
    /// Converts text to dense vectors for semantic search.
    /// Three implementations cover local-only, API-only, and hybrid modes.
    /// </summary>
    public interface IEmbeddingService
    {
        /// <summary>Human-readable service name for diagnostics.</summary>
        string Name { get; }

        /// <summary>True if the service runs entirely offline.</summary>
        bool IsLocal { get; }

        /// <summary>Output vector dimensionality (0 if unknown).</summary>
        int Dimension { get; }

        /// <summary>Whether the service is initialized and ready for queries.</summary>
        bool IsReady { get; }

        /// <summary>Encode a single text as a float vector.</summary>
        Task<float[]> EmbedAsync(string text);

        /// <summary>Encode multiple texts in one batch.</summary>
        Task<float[][]> EmbedBatchAsync(IEnumerable<string> texts);

        /// <summary>
        /// Build or refresh the vocabulary/index from a corpus.
        /// Local services rebuild token indices; API-backed services are a no-op.
        /// </summary>
        Task BuildVocabularyAsync(IEnumerable<string> corpus);
    }
}
