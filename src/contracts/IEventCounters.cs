namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// ★ ADR 0056 / Phase 4A：最小事件计数接口，解耦 TranscriptWriter 与 EventBus 具体类型。
    ///
    /// 原先 TranscriptWriter.CheckConsistency(EventBus?) 直接依赖 Runtime 的 EventBus 类。
    /// 通过此接口，TranscriptWriter 只依赖抽象计数，不再绑定具体实现。
    ///
    /// EventBus : IEventBus, IEventCounters 同时实现两个接口。
    /// </summary>
    public interface IEventCounters
    {
        /// <summary>已发布 tool_started 事件总数。</summary>
        long ToolStartedCount { get; }

        /// <summary>已发布 tool_completed + tool_failed 事件总数。</summary>
        long ToolCompletedCount { get; }

        /// <summary>已发布 tool_failed 事件总数。</summary>
        long ToolFailedCount { get; }
    }
}
