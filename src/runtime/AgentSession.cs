using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Runtime
{
    /// <summary>
    /// Default session implementation — partitioned namespace, not god object.
    /// </summary>
    /// <remarks>中文：默认会话实现——分区命名空间，而非 god 对象。</remarks>
    public class AgentSession : IAgentSession
    {
        private readonly object _sync = new();
        private readonly List<Message> _messages = new();
        private readonly List<IObservation> _observations = new();
        private readonly List<ValidationResult> _validations = new();
        private readonly List<AgentEvent> _events = new();
        private readonly IEventBus? _eventBus;
        private string _threadId;

        private SessionStatus _status = SessionStatus.Idle;
        private AgentPlan? _plan;
        public string ThreadId { get { lock (_sync) return _threadId; } }
        public SessionStatus Status { get { lock (_sync) return _status; } }
        public DateTime CreatedAt { get; } = DateTime.UtcNow;
        public DomainContext Domain { get; } = new();
        public AgentPermissionLevel PermissionLevel { get; set; } = AgentPermissionLevel.RequestApproval;

        public IReadOnlyList<Message> Messages
        {
            get { lock (_sync) return _messages.ToList().AsReadOnly(); }
        }
        public IReadOnlyList<IObservation> Observations
        {
            get { lock (_sync) return _observations.ToList().AsReadOnly(); }
        }
        public IReadOnlyList<ValidationResult> Validations
        {
            get { lock (_sync) return _validations.ToList().AsReadOnly(); }
        }
        public IReadOnlyList<AgentEvent> Events
        {
            get { lock (_sync) return _events.ToList().AsReadOnly(); }
        }
        public AgentPlan? Plan
        {
            get { lock (_sync) return _plan; }
            set { lock (_sync) _plan = value; }
        }

        public TokenUsage TokenUsage { get; } = new();

        public AgentSession(string threadId, IEventBus? eventBus = null)
        {
            _threadId = threadId;
            _eventBus = eventBus;
        }

        public void AddMessage(Message msg)
        {
            lock (_sync)
                _messages.Add(msg);
            _eventBus?.Publish(new AgentEvent { EventType = "session.message_added", SessionId = ThreadId });
        }

        public void ResetConversation(string newThreadId)
        {
            lock (_sync)
            {
                _threadId = newThreadId;
                _messages.Clear();
                _observations.Clear();
                _validations.Clear();
                _events.Clear();
                Domain.Artifacts.Clear();
                _plan = null;
                _status = SessionStatus.Idle;
                TokenUsage.PromptTokens = 0;
                TokenUsage.CompletionTokens = 0;
            }
            _eventBus?.Publish(new AgentEvent { EventType = "session.reset", SessionId = ThreadId });
        }

        public void AddObservation(IObservation obs)
        {
            lock (_sync)
                _observations.Add(obs);
            _eventBus?.Publish(new AgentEvent { EventType = "session.observation_added", SessionId = ThreadId });
        }

        public void AddValidation(ValidationResult result)
        {
            lock (_sync)
                _validations.Add(result);
            _eventBus?.Publish(new AgentEvent
            {
                EventType = result.Verdict == ValidationVerdict.PASS ? "session.validation_passed"
                          : result.Verdict == ValidationVerdict.FAIL ? "session.validation_failed"
                          : "session.validation_warned",
                SessionId = ThreadId,
                Metadata = new() { ["rule"] = result.RuleName, ["verdict"] = result.Verdict.ToString() }
            });
        }

        public void RecordEvent(AgentEvent evt)
        {
            evt.SessionId = ThreadId;
            lock (_sync)
                _events.Add(evt);
        }

        public void SetStatus(SessionStatus status)
        {
            lock (_sync)
                _status = status;
        }
    }
}
