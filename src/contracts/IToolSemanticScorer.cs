using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Semantic tool ranking service for L2 tool retrieval fallback.
    /// </summary>
    public interface IToolSemanticScorer
    {
        bool IsReady { get; }

        Task BuildAsync(IReadOnlyList<IToolDescriptor> tools, CancellationToken cancellation = default);

        Task<IReadOnlyList<string>> RankAsync(string query, int topN = 50, CancellationToken cancellation = default);
    }
}
