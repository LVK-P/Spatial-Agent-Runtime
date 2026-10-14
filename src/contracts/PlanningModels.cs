namespace SpatialAgent.Runtime.Contracts
{
    [Flags]
    public enum DataEffectKind
    {
        None = 0,
        ReadData = 1 << 0,
        CreateDataset = 1 << 1,
        AppendRows = 1 << 2,
        UpdateRows = 1 << 3,
        DeleteRows = 1 << 4,
        AlterSchema = 1 << 5,
        DeleteDataset = 1 << 6,
        ModifyGeometry = 1 << 7,
        ModifySpatialReference = 1 << 8,
        ModifyMapState = 1 << 9,
        ExternalPublish = 1 << 10,
        OverwriteOutput = 1 << 11
    }

    public enum EffectTargetScope
    {
        Unknown,
        OutputOnly,
        Selection,
        Filtered,
        ExplicitObjectSet,
        EntireDataset
    }

    public enum ReversibilityKind
    {
        Unknown,
        NaturallyReversible,
        BackupRequired,
        Irreversible
    }

    public enum EvidenceConfidence
    {
        Unknown,
        Low,
        Medium,
        High
    }

    /// <summary>
    /// Desired state declared by verification metadata. This lets planning and
    /// settlement reason about state transitions without recognizing a tool
    /// name or a user-specific operation.
    /// </summary>
    /// <remarks>中文：由验证元数据声明的期望状态。这使得规划和结算能够在不识别工具名称或特定操作的情况下推理状态转换。</remarks>
    public enum TargetStateExpectation
    {
        Unknown,
        Present,
        Absent,
        Populated
    }

    /// <summary>
    /// Reusable static behavior shared by tool families. Hundreds of tools can
    /// inherit a small number of effect profiles and override only exceptions.
    /// </summary>
    /// <remarks>中文：工具族共享的可复用静态行为。大量工具可以继承少量效果配置，并仅覆盖例外项。</remarks>
    public sealed class EffectProfile
    {
        public string Id { get; set; } = "unknown";
        public string Version { get; set; } = "1.0";
        public DataEffectKind Effects { get; set; }
        public ReversibilityKind Reversibility { get; set; } = ReversibilityKind.Unknown;
        public bool RequiresInfluenceProbe { get; set; }
        public bool RequiresBackup { get; set; }
        public bool SingleWriterRequired { get; set; } = true;
        public string ProbeKind { get; set; } = "";
        public string ApprovalGroupKey { get; set; } = "";
        public string Source { get; set; } = "derived";
        public EvidenceConfidence Confidence { get; set; } = EvidenceConfidence.Medium;
    }

    /// <summary>
    /// Effect of one resolved invocation. Safety decisions bind to this object,
    /// not merely to the static name of a tool.
    /// </summary>
    /// <remarks>中文：一次已解析调用的效果。安全决策绑定到此对象，而不只绑定到工具的静态名称。</remarks>
    public sealed class InvocationEffect
    {
        public string ToolName { get; set; } = "";
        public EffectProfile Profile { get; set; } = new();
        public EffectTargetScope Scope { get; set; } = EffectTargetScope.Unknown;
        public ApprovalTarget Target { get; set; } = new();
        public int? EstimatedObjectCount { get; set; }
        public int? ExistingNonEmptyTargetCount { get; set; }
        public int? SchemaMutationCount { get; set; }
        public int TargetDatasetCount { get; set; } = 1;
        public bool OutputExists { get; set; }
        public bool UsesSelection { get; set; }
        public bool UsesWhereClause { get; set; }
        public string Summary { get; set; } = "";
        public EvidenceConfidence Confidence { get; set; } = EvidenceConfidence.Unknown;
        public string EvidenceSource { get; set; } = "";
        public TargetStateExpectation DesiredTargetState { get; set; }
            = TargetStateExpectation.Unknown;
    }

    /// <summary>
    /// Lightweight, non-content-bearing fingerprint of the live ArcGIS
    /// project state used to invalidate stale approvals after pause/restart.
    /// Component hashes make diagnostics actionable without persisting layer
    /// contents or selected object ids.
    /// </summary>
    /// <remarks>中文：用于在暂停/重启后使过期审批失效的轻量级、无内容 ArcGIS 实时项目状态指纹。组件哈希可支持诊断，而无需持久化图层内容或所选对象 ID。</remarks>
    public sealed class DataRevisionSnapshot
    {
        public string Fingerprint { get; set; } = "";
        public string ProjectFingerprint { get; set; } = "";
        public string MapFingerprint { get; set; } = "";
        public string LayerFingerprint { get; set; } = "";
        public string SelectionFingerprint { get; set; } = "";
        public string ContentFingerprint { get; set; } = "";
        public bool ContentRevisionRequired { get; set; }
        public bool ContentRevisionReliable { get; set; }
        public bool IsReliable { get; set; }
        public string ReliabilityReason { get; set; } = "";
        public DateTime CapturedAtUtc { get; set; } = DateTime.UtcNow;
    }

    public interface IInvocationEffectResolver
    {
        InvocationEffect Resolve(
            IToolDescriptor? descriptor,
            ToolCall call,
            DomainContext? domain = null);
    }

    public interface IInvocationSafetyKernel
    {
        SafetyDecision Evaluate(
            InvocationEffect effect,
            SafetyDecision catalogDecision,
            AgentPermissionLevel permissionLevel);
    }

    public enum PlanningMode
    {
        Direct,
        DeterministicRecipe,
        Planner,
        PlannerWithReviewers
    }

    public enum TaskComplexity
    {
        Simple,
        Standard,
        Complex,
        Critical
    }

    public sealed class ComplexityAssessment
    {
        public TaskComplexity Complexity { get; set; }
        public PlanningMode Mode { get; set; }
        public int Score { get; set; }
        public int RecommendedSubagents { get; set; }
        public List<string> Reasons { get; set; } = new();
    }

    public sealed class PlanningRequest
    {
        public string Goal { get; set; } = "";
        public string GoalContractFingerprint { get; set; } = "";
        public IReadOnlyList<GoalParameterConstraint> ParameterConstraints { get; set; }
            = Array.Empty<GoalParameterConstraint>();
        public IReadOnlyList<GoalProofObligation> ProofObligations { get; set; }
            = Array.Empty<GoalProofObligation>();
        public IReadOnlyList<ToolCall> ToolCalls { get; set; } = Array.Empty<ToolCall>();
        public IReadOnlyList<InvocationEffect> Effects { get; set; } = Array.Empty<InvocationEffect>();
        public DomainContext Domain { get; set; } = new();
        public string ContextRevision { get; set; } = "";
    }

    public sealed class ComplexityRoutingPolicy
    {
        public int StandardThreshold { get; set; } = 3;
        public int ComplexThreshold { get; set; } = 7;
        public int CriticalThreshold { get; set; } = 12;
        public int MaxReviewers { get; set; } = 2;
    }

    public interface IComplexityRouter
    {
        ComplexityAssessment Assess(PlanningRequest request);
    }

    public interface IPlanCompiler
    {
        AgentPlan Compile(PlanningRequest request, int firstStepId = 1);
    }

    public enum SubagentRole
    {
        ContextAnalysis,
        Strategy,
        SafetyReview,
        Verification
    }

    public sealed class SubagentTask
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public SubagentRole Role { get; set; }
        public string Goal { get; set; } = "";
        public AgentPlan? Plan { get; set; }
        public DomainContext Snapshot { get; set; } = new();
        public int TokenBudget { get; set; } = 2048;
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
    }

    public sealed class SubagentResult
    {
        public string TaskId { get; set; } = "";
        public SubagentRole Role { get; set; }
        public bool Success { get; set; }
        public string Summary { get; set; } = "";
        public EvidenceConfidence Confidence { get; set; }
        public List<string> Findings { get; set; } = new();
        public List<ReviewFinding> StructuredFindings { get; set; } = new();
        public List<string> ArtifactReferences { get; set; } = new();
    }

    public enum ReviewFindingSeverity
    {
        Info,
        Warning,
        Error,
        Critical
    }

    /// <summary>
    /// Reviewers can only request a deterministic runtime reaction. They
    /// cannot execute tools, approve a call, or lower a safety decision.
    /// </summary>
    /// <remarks>中文：审查器只能请求确定性的运行时响应，不能执行工具、批准调用或降低安全决策等级。</remarks>
    public enum ReviewDisposition
    {
        LogOnly,
        Recompile,
        Block
    }

    public sealed class ReviewFinding
    {
        public string Code { get; set; } = "";
        public ReviewFindingSeverity Severity { get; set; }
        public ReviewDisposition Disposition { get; set; }
        public int? StepId { get; set; }
        public string Message { get; set; } = "";
        public string Evidence { get; set; } = "";
    }

    /// <summary>
    /// A specialist is analysis-only. It has no tool executor and cannot mutate
    /// ArcGIS state.
    /// </summary>
    /// <remarks>中文：专家只做分析。它没有工具执行器，也无法变更 ArcGIS 状态。</remarks>
    public interface IReadOnlySubagent
    {
        SubagentRole Role { get; }
        Task<SubagentResult> AnalyzeAsync(
            SubagentTask task,
            CancellationToken cancellation = default);
    }

    public interface ISubagentCoordinator
    {
        Task<IReadOnlyList<SubagentResult>> RunAsync(
            IEnumerable<SubagentTask> tasks,
            int maxConcurrency,
            CancellationToken cancellation = default);
    }

    public enum DomainPackEffectAuthority
    {
        IncreaseOnly
    }

    public sealed class DomainPackManifest
    {
        public string Id { get; set; } = "";
        public string Version { get; set; } = "1.0.0";
        public string MinimumRuntimeVersion { get; set; } = "3.1";
        public string? MaximumRuntimeVersion { get; set; }
        public DomainPackEffectAuthority EffectAuthority { get; set; }
            = DomainPackEffectAuthority.IncreaseOnly;
        public List<string> Dependencies { get; set; } = new();
    }

    public interface IDomainPack
    {
        string Id { get; }
        string DisplayName { get; }
        int Priority { get; }
        DomainPackManifest Manifest { get; }
        bool CanHandle(IToolDescriptor descriptor);
        EffectProfile? ResolveEffectProfile(IToolDescriptor descriptor);
        IReadOnlyList<string> RecipeIds { get; }
    }
}
