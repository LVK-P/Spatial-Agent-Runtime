namespace SpatialAgent.Runtime.Contracts
{
    public enum GoalTargetQuantifier
    {
        Unspecified,
        Explicit,
        RepresentativePerLineage,
        AllPhysicalTargets,
        AllMatchingState
    }

    /// <summary>
    /// Whether the compiled goal has enough target scope to authorize mutation.
    /// </summary>
    public enum GoalScopeSufficiency
    {
        /// <summary>No mutating work is implied; coverage may be not-in-scope.</summary>
        NotRequired,
        /// <summary>Target set is determinate (explicit or expanded).</summary>
        Sufficient,
        /// <summary>Mutating capability/intent exists but scope cannot be fixed.</summary>
        Insufficient,
        /// <summary>User clarification is required before any mutation.</summary>
        ClarificationRequired
    }

    /// <summary>
    /// Describes whether target coverage can be evaluated for the current
    /// physical batch. Enforcement is owned by Plan Admissibility; cumulative
    /// completion is owned by Goal Progress and proof closure.
    /// </summary>
    public enum CoverageApplicability
    {
        /// <summary>Read-only plan; target coverage does not apply.</summary>
        NotInScope,
        /// <summary>
        /// Plan only creates new output-scoped artifacts and does not mutate an
        /// existing business target. Safety and capability proof still apply.
        /// </summary>
        OutputOnly,
        /// <summary>Mutating work without a sufficient goal scope — block scheduling.</summary>
        ScopeInsufficient,
        /// <summary>Goal targets are comparable to planned writes.</summary>
        Applicable
    }

    public sealed class GoalFieldPredicate
    {
        public string DomainCategory { get; set; } = "";
        public List<string> Fields { get; set; } = new();
        public TargetStateExpectation DesiredState { get; set; }
            = TargetStateExpectation.Unknown;
    }

    /// <summary>
    /// Resource identity observed when a goal scope was resolved. Display name
    /// is presentation state; map-member and dataset ids are reconciliation
    /// keys. StateFingerprint determines whether prior completion evidence is
    /// still valid after a user or another process edits ArcGIS state.
    /// </summary>
    public sealed class GoalResourceHandle
    {
        public string DisplayName { get; set; } = "";
        public string MapMemberId { get; set; } = "";
        public string DatasetId { get; set; } = "";
        public string SchemaFingerprint { get; set; } = "";
        public string StateFingerprint { get; set; } = "";
        public bool StateFingerprintReliable { get; set; }
        public string LineageRoot { get; set; } = "";
        public int LineageDepth { get; set; }
    }

    /// <summary>
    /// Conversation-aware target scope. It separates linguistic resolution
    /// from physical-plan validation and can safely carry omitted constraints
    /// across a follow-up turn.
    /// </summary>
    public sealed class ResolvedGoalScope
    {
        public string GoalId { get; set; } = Guid.NewGuid().ToString("N");
        public int Version { get; set; } = 1;
        public string ParentGoalId { get; set; } = "";
        public GoalTurnRelation TurnRelation { get; set; } =
            GoalTurnRelation.New;
        public string ContractFingerprint { get; set; } = "";
        public string SourceGoal { get; set; } = "";
        public string ScopeBasis { get; set; } = "";
        public GoalTargetQuantifier Quantifier { get; set; }
        public bool InheritedConstraints { get; set; }
        public List<string> DomainCategories { get; set; } = new();
        public List<string> ExplicitTargets { get; set; } = new();
        public List<string> ReferenceTargets { get; set; } = new();
        public List<string> UnresolvedTargets { get; set; } = new();
        public List<string> SatisfiedTargets { get; set; } = new();
        public List<string> SatisfiedProofObligations { get; set; } = new();
        public List<GoalRequirement> Requirements { get; set; } = new();
        public List<GoalParameterConstraint> ParameterConstraints { get; set; } = new();
        public List<string> UnresolvedRequirements { get; set; } = new();
        public List<GoalFieldPredicate> FieldPredicates { get; set; } = new();
        public List<string> RequiredCapabilities { get; set; } = new();
        public List<GoalDeliverable> Deliverables { get; set; } = new();
        public List<GoalProofObligation> ProofObligations { get; set; } = new();
        public List<GoalResourceBinding> ResourceBindings { get; set; } = new();
        public List<GoalResourceHandle> ResourceHandles { get; set; } = new();
        /// <summary>Whether target scope is determinate enough for mutation.</summary>
        public GoalScopeSufficiency ScopeSufficiency { get; set; } =
            GoalScopeSufficiency.NotRequired;
        /// <summary>Which expansion rule produced the target set, if any.</summary>
        public string ScopeExpansionRule { get; set; } = "";
    }

    /// <summary>
    /// Evidence that a set-valued goal is fully represented by a physical plan.
    /// Coverage is independent from tool identity: it compares typed domain
    /// targets with invocation data-flow targets.
    /// </summary>
    public sealed class TargetCoverageResult
    {
        public bool IsApplicable { get; set; }
        public CoverageApplicability Applicability { get; set; } =
            CoverageApplicability.NotInScope;
        /// <summary>
        /// Pre-execution completeness of the current physical plan against the
        /// outstanding target set. This does not mean execution succeeded.
        /// </summary>
        public bool IsComplete { get; set; } = true;
        /// <summary>
        /// True after execution results have been folded into goal progress.
        /// </summary>
        public bool ProgressEvaluated { get; set; }
        /// <summary>
        /// Cumulative goal-level target completion, independent from whether the
        /// latest model batch covered every target.
        /// </summary>
        public bool IsGoalComplete { get; set; }
        public string ScopeBasis { get; set; } = "";
        public List<string> GoalTargets { get; set; } = new();
        public List<string> SatisfiedTargets { get; set; } = new();
        /// <summary>
        /// Targets retained in the goal denominator but proven to be in the
        /// desired state before the current physical batch.
        /// </summary>
        public List<string> AlreadySatisfiedTargets { get; set; } = new();
        public List<string> ExpectedTargets { get; set; } = new();
        public List<string> PlannedTargets { get; set; } = new();
        public List<string> MissingTargets { get; set; } = new();
        public List<string> ReferenceTargets { get; set; } = new();
        public List<string> RedundantTargets { get; set; } = new();
        public List<string> UnexpectedTargets { get; set; } = new();
        public List<string> UnresolvedTargets { get; set; } = new();
        public List<string> UnresolvedRequirements { get; set; } = new();
    }

    public interface IPlanCoverageValidator
    {
        TargetCoverageResult Validate(
            ResolvedGoalScope scope,
            AgentPlan plan,
            DomainContext context);
    }

    public sealed class GoalProgressUpdate
    {
        public List<string> NewlySatisfiedTargets { get; set; } = new();
        public List<string> ResidualTargets { get; set; } = new();
        public bool IsGoalComplete { get; set; }
    }

    /// <summary>
    /// Folds immutable step results into cumulative goal progress. It is separate
    /// from plan coverage because a plan may intentionally cover only a safe
    /// subset of the outstanding goal.
    /// </summary>
    public interface IGoalProgressTracker
    {
        GoalProgressUpdate Update(
            ResolvedGoalScope scope,
            TargetCoverageResult coverage,
            AgentPlan plan,
            IReadOnlyList<StepExecutionResult> results);
    }
}
