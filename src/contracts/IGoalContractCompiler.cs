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
