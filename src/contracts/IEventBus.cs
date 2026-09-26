namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Agent event — published to Event Bus, consumed by subscribers.
    /// Metadata: flat string key-values for simple tags.
    /// Data: nested structured payload for tools, verification, context.
    /// </summary>
    public class AgentEvent
    {
        public string EventType { get; set; } = "";
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public string? SessionId { get; set; }
        public int? Turn { get; set; }
        public Dictionary<string, string>? Metadata { get; set; }
        /// <summary>Structured payload — supports nested objects for tool args, verification results, etc.</summary>
        public Dictionary<string, object?>? Data { get; set; }
    }

    /// <summary>
    /// Event Bus — central publish/subscribe backbone.
    /// UI, Logger, Telemetry, History are subscribers.
    /// Runtime modules are publishers.
    /// </summary>
    public interface IEventBus
    {
        void Publish(AgentEvent evt);
        IDisposable Subscribe(Action<AgentEvent> handler);
        IDisposable Subscribe(string eventType, Action<AgentEvent> handler);
    }
}
