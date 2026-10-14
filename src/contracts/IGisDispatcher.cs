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
    /// <remarks>中文：抽象GIS执行上下文。适配器通过 QueuedTask.Run（） 实现，测试通过同步或线程池回退实现。</remarks>
    public interface IGisDispatcher
    {
        /// <summary>
        /// Run an operation on the GIS thread with cancellation support.
        /// </summary>
        /// <remarks>中文：在支持取消功能的GIS线程上运行操作。</remarks>
        Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken ct = default);

        /// <summary>
        /// Run a fire-and-forget operation (void return).
        /// </summary>
        /// <remarks>中文：执行一个“发射后遗忘”操作（虚空回归）。</remarks>
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
    /// <remarks>中文：按工具类型超时政策。纯数据——没有ArcGIS依赖。它存在于契约中，所以调度器（运行时）可以不用依赖适配器。命名为“ToolExecution”而非“Gis”，因为按风险级别超时是通用的代理模式，非GIS专属。GIS专用的覆盖（例如拓扑检查需要更长时间超时）存在于适配器配置中。</remarks>
    public class ToolExecutionTimeoutPolicy
    {
        /// <summary>ReadOnly tools: spatial queries, statistics. Default 60s.</summary>
        /// <remarks>中文：唯读工具：空间查询、统计数据。默认是60多。</remarks>
        public TimeSpan ReadOnlyTimeout { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>Modify tools: buffer, clip, add field. Default 5min.</summary>
        /// <remarks>中文：修改工具：缓冲区、裁剪、添加字段。默认5分钟。</remarks>
        public TimeSpan ModifyTimeout { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>Destructive tools: delete features, delete field. Default 30s.</summary>
        /// <remarks>中文：破坏性工具：删除特征，删除字段。默认是30多。</remarks>
        public TimeSpan DestructiveTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>Resolve timeout for a given risk level.</summary>
        /// <remarks>中文：解决给定风险等级的超时问题。</remarks>
        public TimeSpan ForRiskLevel(ToolRiskLevel level) => level switch
        {
            ToolRiskLevel.ReadOnly => ReadOnlyTimeout,
            ToolRiskLevel.Destructive => DestructiveTimeout,
            _ => ModifyTimeout
        };
    }
}
