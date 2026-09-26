namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Session state — the only stateful object in the Runtime.
    /// Partitioned namespace, not a god object.
    /// </summary>
    public enum SessionStatus { Idle, Running, WaitingForApproval, Completed, Failed, Interrupted }

    /// <summary>
    /// User-granted capability for the current session.
    /// Separate from tool risk classification.
    /// </summary>
    public enum AgentPermissionLevel { ReadOnly, RequestApproval, FullAccess }

    public interface IAgentSession
    {
        string ThreadId { get; }
        SessionStatus Status { get; }
        DateTime CreatedAt { get; }
        void SetStatus(SessionStatus status);

        // ── Conversation (LLM Memory) ──
        IReadOnlyList<Message> Messages { get; }
        void AddMessage(Message msg);
        void ResetConversation(string newThreadId);

        // ── Domain Context ──
        DomainContext Domain { get; }

        // ── Safety Permission ──
        AgentPermissionLevel PermissionLevel { get; set; }

        // ── Current Plan ──
        AgentPlan? Plan { get; set; }

        // ── Observations (append-only) ──
        IReadOnlyList<IObservation> Observations { get; }
        void AddObservation(IObservation obs);

        // ── Validations (append-only) ──
        IReadOnlyList<ValidationResult> Validations { get; }
        void AddValidation(ValidationResult result);

        // ── Token accounting ──
        TokenUsage TokenUsage { get; }

        // ── Events (append-only, for replay) ──
        IReadOnlyList<AgentEvent> Events { get; }
        void RecordEvent(AgentEvent evt);
    }

    public class Message
    {
        public string Role { get; set; } = ""; // "user" | "assistant" | "system" | "tool"
        public string Content { get; set; } = "";
        /// <summary>
        /// Opaque provider reasoning state. Thinking-mode tool calls can
        /// require this value to be echoed on continuation requests.
        /// Never render it as the assistant's answer.
        /// </summary>
        public string? ReasoningContent { get; set; }
        public string? ToolCallId { get; set; } // required for role="tool"
        public List<ToolCall>? ToolCalls { get; set; } // for role="assistant" with function calls
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    public class AgentPlan
    {
        public string PlanId { get; set; } = Guid.NewGuid().ToString("N");
        public int Version { get; set; } = 1;
        public string Goal { get; set; } = "";
        public string ProjectRevision { get; set; } = "";
        public DataRevisionSnapshot? DataRevision { get; set; }
        /// <summary>
        /// Resources this plan expects to mutate. Host revision diffs that fall
        /// inside this budget are treated as self-committed, not external edits.
        /// </summary>
        public ExpectedMutationSet ExpectedMutations { get; set; } = new();
        /// <summary>
        /// True after at least one mutating step in this plan transaction has
        /// completed successfully.
        /// </summary>
        public bool HasCommittedMutation { get; set; }
        public ComplexityAssessment? Planning { get; set; }
        public PlanValidationResult? Validation { get; set; }
        public PlanAdmissibilityResult? Admissibility { get; set; }
        public TargetCoverageResult? Coverage { get; set; }
        public CapabilityClosureResult? CapabilityClosure { get; set; }
        public ResolvedGoalScope? ResolvedScope { get; set; }
        public List<SubagentResult> Reviews { get; set; } = new();
        public List<PlanStep> Steps { get; set; } = new();
    }

    public class PlanStep
    {
        public int Id { get; set; }
        /// <summary>
        /// Stable semantic identity across replans. Unlike Step.Id and the
        /// provider tool-call id, this value is derived from goal contract,
        /// capability, target and desired state.
        /// </summary>
        public string ActionId { get; set; } = "";
        public string Description { get; set; } = "";
        public ToolCall? ToolCall { get; set; }
        public string RequestedToolName { get; set; } = "";
        public string CanonicalToolName { get; set; } = "";
        public string ExecutionToolName { get; set; } = "";
        /// <summary>
        /// Topological/single-writer ordering constraints. These do not by
        /// themselves imply that a predecessor must succeed.
        /// </summary>
        public List<int> DependsOn { get; set; } = new();
        /// <summary>
        /// True data prerequisites. A failure here makes this step ineligible
        /// because it consumes state produced by the predecessor.
        /// </summary>
        public List<int> RequiresSuccessfulSteps { get; set; } = new();
        public List<string> ReadSet { get; set; } = new();
        public List<string> WriteSet { get; set; } = new();
        public InvocationEffect? Effect { get; set; }
        public string DomainPackId { get; set; } = "";
        public List<string> Preconditions { get; set; } = new();
        public List<string> Postconditions { get; set; } = new();
        public List<string> ProvidedCapabilities { get; set; } = new();
        public PlanStepPurpose Purpose { get; set; } = PlanStepPurpose.Business;
        public List<ParameterScopedProofRequirement> ProofRequirements { get; set; } = new();
        public PlanStepStatus Status { get; set; } = PlanStepStatus.Pending;
        public ApprovalTicket? PendingApproval { get; set; }
    }

    public enum PlanStepStatus { Pending, Running, Completed, Failed, Skipped, AwaitingApproval }

    public enum PlanStepPurpose
    {
        Unspecified,
        Business,
        SupportProbe
    }

    public class DomainContext
    {
        public ProjectInfo Project { get; set; } = new();
        public MapState Map { get; set; } = new();
        public List<LayerInfo> Layers { get; set; } = new();
        public List<DomainArtifactRecord> Artifacts { get; set; } = new();
        public SelectionState Selection { get; set; } = new();
        public WorkspaceInfo Workspace { get; set; } = new();
    }

    /// <summary>
    /// Field-level provenance retained across turns. Semantic references in a
    /// later goal bind through this ledger instead of depending on a physical
    /// field name or a tool-specific runtime rule.
    /// </summary>
    public sealed class DomainArtifactRecord
    {
        public string ArtifactId { get; set; } = Guid.NewGuid().ToString("N");
        public string OwnerLayer { get; set; } = "";
        public string DatasetId { get; set; } = "";
        public string FieldName { get; set; } = "";
        public string SemanticType { get; set; } = "";
        public string ProducerCapability { get; set; } = "";
        public string ProducerTool { get; set; } = "";
        public string StateFingerprint { get; set; } = "";
    }

    public class ProjectInfo
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public string DefaultGdb { get; set; } = "";
    }

    public class MapState
    {
        public string Name { get; set; } = "";
        public string SpatialReference { get; set; } = "";
        public ExtentInfo? Extent { get; set; }
    }

    public class LayerInfo
    {
        public string Name { get; set; } = "";
        /// <summary>
        /// Stable identity of the map member when the host exposes one. A
        /// display-name edit must not turn the same layer into another target.
        /// </summary>
        public string MapMemberId { get; set; } = "";
        /// <summary>
        /// Stable, path-sanitized identity of the backing dataset. This lets a
        /// removed and re-added map member be reconciled without using its
        /// presentation name as identity.
        /// </summary>
        public string DatasetId { get; set; } = "";
        public string DatasetName { get; set; } = "";
        public string GeometryType { get; set; } = "";
        public long FeatureCount { get; set; }
        public List<string> Fields { get; set; } = new();
        /// <summary>Dataset schema revision, independent from row edits.</summary>
        public string SchemaFingerprint { get; set; } = "";
        /// <summary>
        /// Non-content-bearing host revision for schema and row edits. A
        /// changed value invalidates settled evidence; it is not user data.
        /// </summary>
        public string StateFingerprint { get; set; } = "";
        public bool StateFingerprintReliable { get; set; }
        /// <summary>
        /// Stable root of a runtime-observed derivation chain. Empty means the
        /// layer has no known lineage relationship in this session.
        /// </summary>
        public string LineageRoot { get; set; } = "";
        public int LineageDepth { get; set; }
    }

    public class SelectionState
    {
        public string? LayerName { get; set; }
        public int Count { get; set; }
    }

    public class WorkspaceInfo
    {
        public string Root { get; set; } = "";
        public string ProcessGdb { get; set; } = "";
        public string OutputGdb { get; set; } = "";
    }
}
