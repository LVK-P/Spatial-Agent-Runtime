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
    /// <remarks>中文：已编译目标是否具有足够明确的目标范围来授权修改操作。</remarks>
    public enum GoalScopeSufficiency
    {
        /// <summary>No mutating work is implied; coverage may be not-in-scope.</summary>
        /// <remarks>中文：没有隐含修改操作，覆盖检查可能不适用。</remarks>
        NotRequired,
        /// <summary>Target set is determinate (explicit or expanded).</summary>
        /// <remarks>中文：目标集是确定的（显式或扩展的）。</remarks>
        Sufficient,
        /// <summary>Mutating capability/intent exists but scope cannot be fixed.</summary>
        /// <remarks>中文：存在修改能力/意图，但无法确定目标范围。</remarks>
        Insufficient,
        /// <summary>User clarification is required before any mutation.</summary>
        /// <remarks>中文：执行任何修改前都需要用户澄清。</remarks>
        ClarificationRequired
    }

    /// <summary>
    /// Describes whether target coverage can be evaluated for the current
    /// physical batch. Enforcement is owned by Plan Admissibility; cumulative
    /// completion is owned by Goal Progress and proof closure.
    /// </summary>
    /// <remarks>中文：说明当前物理批次能否评估目标覆盖。强制执行由计划可采纳性负责；累计完成度由目标进度和证据闭环负责。</remarks>
    public enum CoverageApplicability
    {
        /// <summary>Read-only plan; target coverage does not apply.</summary>
        /// <remarks>中文：只读计划，不适用目标覆盖检查。</remarks>
        NotInScope,
        /// <summary>
        /// Plan only creates new output-scoped artifacts and does not mutate an
        /// existing business target. Safety and capability proof still apply.
        /// </summary>
        /// <remarks>中文：计划仅创建限定在输出范围内的新产物，不修改现有业务目标；安全和能力证明仍然适用。</remarks>
        OutputOnly,
        /// <summary>Mutating work without a sufficient goal scope — block scheduling.</summary>
        /// <remarks>中文：修改操作缺少足够的目标范围，因此阻止调度。</remarks>
        ScopeInsufficient,
        /// <summary>Goal targets are comparable to planned writes.</summary>
        /// <remarks>中文：目标中的对象可与计划写入对象进行比较。</remarks>
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
        /// <remarks>中文：解析目标范围时观察到的资源标识。显示名称仅用于展示；地图成员 ID 和数据集 ID 是协调键。StateFingerprint 用于判断用户或其他进程修改 ArcGIS 状态后，先前的完成证据是否仍有效。</remarks>
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
    /// <remarks>中文：对话感知目标范围。它将语言解析与物理计划验证分离，并可在后续回合安全携带遗漏的约束。</remarks>
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
        /// <remarks>中文：目标范围是否足够明确，可以执行修改。</remarks>
        public GoalScopeSufficiency ScopeSufficiency { get; set; } =
            GoalScopeSufficiency.NotRequired;
        /// <summary>Which expansion rule produced the target set, if any.</summary>
        /// <remarks>中文：是哪条扩展规则产生了目标集（如果有的话）。</remarks>
        public string ScopeExpansionRule { get; set; } = "";
    }

    /// <summary>
    /// Evidence that a set-valued goal is fully represented by a physical plan.
    /// Coverage is independent from tool identity: it compares typed domain
    /// targets with invocation data-flow targets.
    /// </summary>
    /// <remarks>中文：证明集合值目标已被物理计划完整表示。覆盖检查与工具标识无关，它比较类型化领域目标与调用数据流目标。</remarks>
    public sealed class TargetCoverageResult
    {
        public bool IsApplicable { get; set; }
        public CoverageApplicability Applicability { get; set; } =
            CoverageApplicability.NotInScope;
        /// <summary>
        /// Pre-execution completeness of the current physical plan against the
        /// outstanding target set. This does not mean execution succeeded.
        /// </summary>
        /// <remarks>中文：当前物理计划对未完成目标集的执行前完整性。这并不意味着执行成功。</remarks>
        public bool IsComplete { get; set; } = true;
        /// <summary>
        /// True after execution results have been folded into goal progress.
        /// </summary>
        /// <remarks>中文：执行结果已合并到目标进度后为 true。</remarks>
        public bool ProgressEvaluated { get; set; }
        /// <summary>
        /// Cumulative goal-level target completion, independent from whether the
        /// latest model batch covered every target.
        /// </summary>
        /// <remarks>中文：累计目标级目标完成，与最新模型批次是否覆盖所有目标无关。</remarks>
        public bool IsGoalComplete { get; set; }
        public string ScopeBasis { get; set; } = "";
        public List<string> GoalTargets { get; set; } = new();
        public List<string> SatisfiedTargets { get; set; } = new();
        /// <summary>
        /// Targets retained in the goal denominator but proven to be in the
        /// desired state before the current physical batch.
        /// </summary>
        /// <remarks>中文：目标保留在目标分母中，但在当前物理批次之前已证明处于期望状态。</remarks>
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
    /// <remarks>中文：将不可变步骤结果合并为累计目标进度。它与计划覆盖分离，因为计划可能有意只覆盖未完成目标中的安全子集。</remarks>
    public interface IGoalProgressTracker
    {
        GoalProgressUpdate Update(
            ResolvedGoalScope scope,
            TargetCoverageResult coverage,
            AgentPlan plan,
            IReadOnlyList<StepExecutionResult> results);
    }
}
