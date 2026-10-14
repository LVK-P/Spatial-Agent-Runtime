namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Session state — the only stateful object in the Runtime.
    /// Partitioned namespace, not a god object.
    /// </summary>
    /// <remarks>中文：会话状态——Runtime 中唯一有状态的对象。采用分区命名空间，而非上帝对象。</remarks>
    public enum SessionStatus { Idle, Running, WaitingForApproval, Completed, Failed, Interrupted }

    /// <summary>
    /// User-granted capability for the current session.
    /// Separate from tool risk classification.
    /// </summary>
    /// <remarks>中文：用户授予当前会话的能力，与工具风险分类相互独立。</remarks>
    public enum AgentPermissionLevel { ReadOnly, RequestApproval, FullAccess }

    public interface IAgentSession
    {
        string ThreadId { get; }
        SessionStatus Status { get; }
        DateTime CreatedAt { get; }
        void SetStatus(SessionStatus status);

        // ── Conversation (LLM Memory) ──
        // 中文：会话（LLM 记忆）
        IReadOnlyList<Message> Messages { get; }
        void AddMessage(Message msg);
        void ResetConversation(string newThreadId);

        // ── Domain Context ──
        // 中文：领域上下文
        DomainContext Domain { get; }

        // ── Safety Permission ──
        // 中文：安全许可
        AgentPermissionLevel PermissionLevel { get; set; }

        // ── Current Plan ──
        // 中文：当前计划
        AgentPlan? Plan { get; set; }

        // ── Observations (append-only) ──
        // 中文：观察（仅追加）
        IReadOnlyList<IObservation> Observations { get; }
        void AddObservation(IObservation obs);

        // ── Validations (append-only) ──
        // 中文：验证（仅追加）
        IReadOnlyList<ValidationResult> Validations { get; }
        void AddValidation(ValidationResult result);

        // ── Token accounting ──
        // 中文：Token 统计
        TokenUsage TokenUsage { get; }

        // ── Events (append-only, for replay) ──
        // 中文：事件（仅追加，用于回放）
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
        /// <remarks>中文：不透明的提供程序推理状态。思考模式下的工具调用可能要求在续传请求中回传此值。绝不能将其呈现为助手答复。</remarks>
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
        /// <remarks>中文：该计划预期修改的资源。落在此预算内的宿主修订差异视为计划自身提交的变更，而不是外部编辑。</remarks>
        public ExpectedMutationSet ExpectedMutations { get; set; } = new();
        /// <summary>
        /// True after at least one mutating step in this plan transaction has
        /// completed successfully.
        /// </summary>
        /// <remarks>中文：该计划事务中至少一个修改步骤成功完成后为 true。</remarks>
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
        /// <remarks>中文：跨重新规划保持稳定的语义标识。不同于 Step.Id 和提供程序工具调用 ID，该值由目标契约、能力、目标对象和期望状态共同派生。</remarks>
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
        /// <remarks>中文：拓扑/单写者排序约束；这些约束本身不表示前置步骤必须成功。</remarks>
        public List<int> DependsOn { get; set; } = new();
        /// <summary>
        /// True data prerequisites. A failure here makes this step ineligible
        /// because it consumes state produced by the predecessor.
        /// </summary>
        /// <remarks>中文：真正的数据前提条件。若失败，该步骤不具备资格，因为它消耗了前一步产生的状态。</remarks>
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
    /// <remarks>中文：跨轮次保留的字段级来源信息。后续目标中的语义引用通过此账本绑定，而不依赖物理字段名或工具专用的运行时规则。</remarks>
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
        /// <remarks>中文：宿主可提供时使用的地图成员稳定标识。修改显示名称不得使同一图层变成另一个目标。</remarks>
        public string MapMemberId { get; set; } = "";
        /// <summary>
        /// Stable, path-sanitized identity of the backing dataset. This lets a
        /// removed and re-added map member be reconciled without using its
        /// presentation name as identity.
        /// </summary>
        /// <remarks>中文：底层数据集经路径脱敏后的稳定标识。地图成员删除后重新添加时，可据此完成协调，而无需把显示名称当作标识。</remarks>
        public string DatasetId { get; set; } = "";
        public string DatasetName { get; set; } = "";
        public string GeometryType { get; set; } = "";
        public long FeatureCount { get; set; }
        public List<string> Fields { get; set; } = new();
        /// <summary>Dataset schema revision, independent from row edits.</summary>
        /// <remarks>中文：数据集模式修订，独立于行编辑。</remarks>
        public string SchemaFingerprint { get; set; } = "";
        /// <summary>
        /// Non-content-bearing host revision for schema and row edits. A
        /// changed value invalidates settled evidence; it is not user data.
        /// </summary>
        /// <remarks>中文：不承载内容的宿主修订标识，用于模式和行编辑。值发生变化时会使已确认的证据失效；它本身不是用户数据。</remarks>
        public string StateFingerprint { get; set; } = "";
        public bool StateFingerprintReliable { get; set; }
        /// <summary>
        /// Stable root of a runtime-observed derivation chain. Empty means the
        /// layer has no known lineage relationship in this session.
        /// </summary>
        /// <remarks>中文：运行时观察到的派生链稳定根标识。为空表示该图层在本会话中没有已知的血缘关系。</remarks>
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
