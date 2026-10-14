using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Semantic tool ranking service for L2 tool retrieval fallback.
    /// </summary>
    /// <remarks>中文：语义工具排名服务用于L2工具检索备份。</remarks>
    public interface IToolSemanticScorer
    {
        bool IsReady { get; }

        Task BuildAsync(IReadOnlyList<IToolDescriptor> tools, CancellationToken cancellation = default);

        Task<IReadOnlyList<string>> RankAsync(string query, int topN = 50, CancellationToken cancellation = default);
    }
}
