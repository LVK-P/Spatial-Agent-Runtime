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
    /// <remarks>English：ADR 0056 / Phase 4A: Minimum event counting interface, decouples TranscriptWriter and EventBus specific types. Originally, TranscriptWriter.CheckConsistency(EventBus?) was an EventBus class that directly depended on Runtime. Through this interface, TranscriptWriter relies solely on abstract counting and does not bind to specific implementations. EventBus: IEventBus and IEventCounters implement two interfaces simultaneously.</remarks>
    public interface IEventCounters
    {
        /// <summary>已发布 tool_started 事件总数。</summary>
        /// <remarks>English：tool_started total number of published events.</remarks>
        long ToolStartedCount { get; }

        /// <summary>已发布 tool_completed + tool_failed 事件总数。</summary>
        /// <remarks>English：Total number of tool_completed + tool_failed events published.</remarks>
        long ToolCompletedCount { get; }

        /// <summary>已发布 tool_failed 事件总数。</summary>
        /// <remarks>English：A total of tool_failed events have been published.</remarks>
        long ToolFailedCount { get; }
    }
}
