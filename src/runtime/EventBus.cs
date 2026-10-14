// EventBus.cs — In-process pub/sub backbone for the agent runtime.
// Publishers (Scheduler, AgentRuntime, ContextManager) emit AgentEvent
// instances. Subscribers (TranscriptWriter, ViewModel, diagnostics) react
// to specific event types or subscribe via "*" for firehose access.
// Handlers are always invoked sequentially inside a try/catch so one
// failing subscriber never takes down the runtime.
// 中文：EventBus.cs — 代理运行时的进程发布/订阅骨干网。发布者（Scheduler、AgentRuntime、ContextManager）会发布 AgentEvent 实例。订阅者（TranscriptWriter、ViewModel、diagnostics）对特定事件类型做出反应，或通过“*”订阅以获得消防水管访问。处理程序总是在尝试/捕获中顺序调用，因此失败的订阅者不会破坏运行时。

using System.Diagnostics;
using System.Threading;
using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Runtime
{
    /// <summary>
    /// In-process pub/sub event bus. All subscribers run synchronously
    /// on the publishing thread; long-running work should be offloaded.
    /// </summary>
    /// <remarks>中文：正在进行的发布/订阅事件总线。所有订阅者在发布线程上同步运行;长期工作应当被卸下。</remarks>
    public class EventBus : IEventBus, IEventCounters
    {
        private readonly Dictionary<string, List<Action<AgentEvent>>> _subscribers = new();
        private readonly object _lock = new();

        // ── P1-C（2026-07-20）：按事件类型的发布计数器，供 TranscriptWriter 一致性校验 ──
        // English：P1-C (2026-07-20): Release counter by event type for TranscriptWriter consistency checks
        private long _toolStartedPublished;
        private long _toolCompletedPublished;
        private long _toolFailedPublished;

        /// <summary>已发布的 tool.started 事件总数。</summary>
        /// <remarks>English：Total number of published tool.started events.</remarks>
        public long ToolStartedCount => Interlocked.Read(ref _toolStartedPublished);
        /// <summary>已发布的 tool.completed 事件总数。</summary>
        /// <remarks>English：Total number of published tool.completed events.</remarks>
        public long ToolCompletedCount => Interlocked.Read(ref _toolCompletedPublished);
        /// <summary>已发布的 tool.failed 事件总数。</summary>
        /// <remarks>English：Total number of published tool.failed events.</remarks>
        public long ToolFailedCount => Interlocked.Read(ref _toolFailedPublished);

        public void Publish(AgentEvent evt)
        {
            // ★ P1-C：按事件类型递增计数器
            // English：P1-C: Incremental counter by event type
            switch (evt.EventType)
            {
                case "tool.started": Interlocked.Increment(ref _toolStartedPublished); break;
                case "tool.completed": Interlocked.Increment(ref _toolCompletedPublished); break;
                case "tool.failed": Interlocked.Increment(ref _toolFailedPublished); break;
            }

            List<Action<AgentEvent>> handlers;
            lock (_lock)
            {
                handlers = new List<Action<AgentEvent>>();
                if (_subscribers.TryGetValue("*", out var wildcard))
                    handlers.AddRange(wildcard);
                if (_subscribers.TryGetValue(evt.EventType, out var specific))
                    handlers.AddRange(specific);
            }

            foreach (var handler in handlers)
            {
                try { handler(evt); }
                catch (Exception ex)
                {
                    // ★ 保持 fail-open：订阅者异常不拖垮主流程。
                    // ★ 但必须留诊断信号：用比 EventBus 更底层的 Trace 通道，
                    //   避免"用 EventBus 报告 EventBus 自身异常"的循环依赖。
                    // English：Maintain fail-open: Subscriber exceptions do not drag down the main process. However, diagnostic signals must be retained: use a trace channel at a lower level than EventBus to avoid cyclic dependencies where "EventBus reports EventBus itself anomaly."
                    Trace.WriteLine(
                        $"[EventBus] Subscriber threw for event '{evt.EventType}' " +
                        $"(turn={evt.Turn}): {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        public IDisposable Subscribe(Action<AgentEvent> handler)
            => Subscribe("*", handler);

        public IDisposable Subscribe(string eventType, Action<AgentEvent> handler)
        {
            lock (_lock)
            {
                if (!_subscribers.ContainsKey(eventType))
                    _subscribers[eventType] = new List<Action<AgentEvent>>();
                _subscribers[eventType].Add(handler);
            }
            return new Subscription(() =>
            {
                lock (_lock)
                {
                    if (_subscribers.TryGetValue(eventType, out var list))
                        list.Remove(handler);
                }
            });
        }

        private class Subscription : IDisposable
        {
            private readonly Action _unsubscribe;
            public Subscription(Action unsubscribe) => _unsubscribe = unsubscribe;
            public void Dispose() => _unsubscribe();
        }
    }
}
