using System;
using System.Threading;
using System.Threading.Tasks;

// ================================================================
// IGisDispatcher.cs — 抽象 GIS 执行上下文调度器
//
// 将 ArcGIS Pro MCT 线程模型隔离在 Adapters 层。
// Runtime 和 Tests 通过此接口获得平台无关的执行调度。
//
// 来源：跨实现架构审查后的宿主调度合同。
// ================================================================
namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Abstract GIS execution context.
    /// Adapters implement with QueuedTask.Run(),
    /// tests implement with synchronous or thread-pool fallback.
    /// </summary>
    public interface IGisDispatcher
    {
        /// <summary>
        /// Run an operation on the GIS thread with cancellation support.
        /// </summary>
        Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken ct = default);

        /// <summary>
        /// Run a fire-and-forget operation (void return).
        /// </summary>
        Task RunAsync(Func<Task> operation, CancellationToken ct = default);
    }

    /// <summary>
    /// Per-tool-kind timeout policy. Pure data — no ArcGIS dependency.
    /// Lives in Contracts so Scheduler (Runtime) can use it without
    /// depending on Adapters.
    ///
    /// Named "ToolExecution" rather than "Gis" because timeout-by-risk-level
    /// is a generic agent pattern, not GIS-specific. GIS-specific overrides
    /// (e.g. topology checks need longer timeouts) live in Adapters config.
    /// </summary>
    public class ToolExecutionTimeoutPolicy
    {
        /// <summary>ReadOnly tools: spatial queries, statistics. Default 60s.</summary>
        public TimeSpan ReadOnlyTimeout { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>Modify tools: buffer, clip, add field. Default 5min.</summary>
        public TimeSpan ModifyTimeout { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>Destructive tools: delete features, delete field. Default 30s.</summary>
        public TimeSpan DestructiveTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>Resolve timeout for a given risk level.</summary>
        public TimeSpan ForRiskLevel(ToolRiskLevel level) => level switch
        {
            ToolRiskLevel.ReadOnly => ReadOnlyTimeout,
            ToolRiskLevel.Destructive => DestructiveTimeout,
            _ => ModifyTimeout
        };
    }
}
