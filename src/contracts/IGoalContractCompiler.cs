namespace SpatialAgent.Runtime.Contracts
{
    public enum GoalOperationFamily
    {
        Unknown,
        SchemaTransition,
        Measurement,
        SpatialTransform,
        SpatialAnalysis,
        Presentation
    }

    public enum GoalRequirementGroundingStatus
    {
        Grounded,
        Unresolved
    }

    /// <summary>
    /// One independently groundable clause of the user's goal. Requirements
    /// preserve the pairing between operation, resource category, artifact and
    /// desired state that is lost when these dimensions are stored as parallel
    /// flat lists.
    /// </summary>
    /// <remarks>中文：一个独立可基于用户目标的条款。需求保留了操作、资源类别、工件和期望状态之间的配对，而这些配对在这些维度存储为并行平面列表时会丢失。</remarks>
    public sealed class GoalRequirement
    {
        public string Id { get; set; } = "";
        public GoalOperationFamily OperationFamily { get; set; }
        public string DomainCategory { get; set; } = "";
        public string ArtifactMention { get; set; } = "";
        public string ArtifactSemanticType { get; set; } = "";
        public List<string> BoundFields { get; set; } = new();
        public TargetStateExpectation DesiredState { get; set; } =
            TargetStateExpectation.Unknown;
        public string RequiredCapability { get; set; } = "";
        public GoalRequirementGroundingStatus GroundingStatus { get; set; } =
            GoalRequirementGroundingStatus.Grounded;
        public string GroundingEvidence { get; set; } = "";
    }

    /// <summary>
    /// A typed, planner-independent constraint on how a capability must be
    /// fulfilled. Keys are stable domain semantics (for example unit or
    /// numeric precision), never ArcGIS parameter names. Tool adapters map the
    /// constraint to their own physical arguments.
    /// </summary>
    /// <remarks>中文：一个类型化、与计划者无关的约束，限制能力必须如何实现。键是稳定的领域语义（例如单位精度或数值精度），绝不是ArcGIS参数名称。工具适配器将约束映射到其自身的物理参数。</remarks>
    public sealed class GoalParameterConstraint
    {
        public string Key { get; set; } = "";
        public string Value { get; set; } = "";
        public string AppliesToCapability { get; set; } = "";
        public string SourceText { get; set; } = "";
    }

    public enum GoalResourceRole
    {
        Target,
        Reference,
        Output
    }

    public enum GoalResourceResolutionStatus
    {
        Resolved,
        Ambiguous,
        CandidateFoundAwaitingConfirmation,
        Missing,
        Derivable
    }

    /// <summary>
    /// Stable host identity retained while a resource mention is unresolved.
    /// DisplayName is presentation only; map-member/dataset identity, schema and
    /// lineage are the evidence-bearing reconciliation keys.
    /// </summary>
    /// <remarks>中文：资源提及时尚未解析完成，仍保留稳定宿主标识。DisplayName 仅用于展示；地图成员/数据集标识、模式和血缘关系才是承载证据的协调键。</remarks>
    public sealed class GoalResourceCandidate
    {
        public string CandidateId { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string MapMemberId { get; set; } = "";
        public string DatasetId { get; set; } = "";
        public string GeometryType { get; set; } = "";
        public List<string> Fields { get; set; } = new();
        public string SchemaFingerprint { get; set; } = "";
        public string LineageRoot { get; set; } = "";
        public int LineageDepth { get; set; }
        public bool HasStableIdentity =>
            !string.IsNullOrWhiteSpace(MapMemberId)
            || !string.IsNullOrWhiteSpace(DatasetId);
    }

    /// <summary>
    /// A resource mention extracted from the user's request before a planner is
    /// allowed to propose physical tool calls.
    /// </summary>
    /// <remarks>中文：在规划者提出物理工具调用之前，从用户请求中提取的资源提及。</remarks>
    public sealed class GoalResourceBinding
    {
        public string Mention { get; set; } = "";
        public GoalResourceRole Role { get; set; }
        public GoalResourceResolutionStatus Status { get; set; }
        public List<string> ResolvedTargets { get; set; } = new();
        public List<GoalResourceCandidate> Candidates { get; set; } = new();
        public List<string> EvidenceBackedCandidateIds { get; set; } = new();
        public string SemanticType { get; set; } = "";
        public double Confidence { get; set; }
        public string Evidence { get; set; } = "";
    }

    /// <summary>
    /// Planner-independent linguistic contract. ScopeText has resource names
    /// removed so geometry words inside layer names cannot accidentally become
    /// set-valued category constraints.
    /// </summary>
    /// <remarks>中文：规划者与语言独立契约。ScopeText 移除了资源名称，以防止层名内的几何词意外成为集合值的类别约束。</remarks>
    public sealed class CompiledGoalContract
    {
        public string SourceGoal { get; set; } = "";
        public string ScopeText { get; set; } = "";
        public List<GoalResourceBinding> ResourceBindings { get; set; } = new();
        public GoalTargetQuantifier Quantifier { get; set; }
        public List<string> DomainCategories { get; set; } = new();
        public List<GoalRequirement> Requirements { get; set; } = new();
        public List<GoalParameterConstraint> ParameterConstraints { get; set; } = new();
        public List<GoalFieldPredicate> FieldPredicates { get; set; } = new();
        public List<string> RequiredCapabilities { get; set; } = new();
        public List<GoalDeliverable> Deliverables { get; set; } = new();
        public List<GoalProofObligation> ProofObligations { get; set; } = new();
    }

    public interface IGoalContractCompiler
    {
        CompiledGoalContract Compile(string goal, DomainContext context);
    }
}
