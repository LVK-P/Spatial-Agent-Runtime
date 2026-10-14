namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Agent event — published to Event Bus, consumed by subscribers.
    /// Metadata: flat string key-values for simple tags.
    /// Data: nested structured payload for tools, verification, context.
    /// </summary>
    /// <remarks>中文：代理事件——发布到事件总线，由订阅者使用。元数据：简单标签的平整字符串键值。数据：用于工具、验证和上下文的嵌套结构化有效载荷。</remarks>
    public class AgentEvent
    {
        public string EventType { get; set; } = "";
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public string? SessionId { get; set; }
        public int? Turn { get; set; }
        public Dictionary<string, string>? Metadata { get; set; }
        /// <summary>Structured payload — supports nested objects for tool args, verification results, etc.</summary>
        /// <remarks>中文：结构化负载——支持嵌套对象，用于工具 arg、验证结果等。</remarks>
        public Dictionary<string, object?>? Data { get; set; }
    }

    /// <summary>
    /// Event Bus — central publish/subscribe backbone.
    /// UI, Logger, Telemetry, History are subscribers.
    /// Runtime modules are publishers.
    /// </summary>
    /// <remarks>中文：事件总线——中央发布/订阅骨干网。UI、Logger、Telemetry、History都是订阅用户。运行时模块是发布者。</remarks>
    public interface IEventBus
    {
        void Publish(AgentEvent evt);
        IDisposable Subscribe(Action<AgentEvent> handler);
        IDisposable Subscribe(string eventType, Action<AgentEvent> handler);
    }
}
