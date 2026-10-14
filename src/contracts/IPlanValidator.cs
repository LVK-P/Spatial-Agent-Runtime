using System.Collections.Generic;

// ================================================================
// IPlanValidator.cs — 计划校验器接口
//
// 在 LLM 生成的 AgentPlan 执行前进行 GIS 特有的合理性校验。
// 不替代通用 Planner——只补充 GIS 领域约束（依赖链、输出冲突等）。
//
// 当前阶段：接口先定义、实现先做检测+告警，后续再加自动修正。
//
// 来源：跨实现架构审查后的计划验证合同。
// ================================================================
// English：================================================================ IPlanValidator.cs — Schedule CheckUser Interface performs GIS-specific plausibility validation before LLM-generated AgentPlan is executed. It does not replace the general-purpose Planner—only supplementing GIS domain constraints (dependency chains, output collisions, etc.). Current stage: Interface definition and implementation first, detection + alerts, then automatic correction later. Source: Plan validation contract after implementation architecture review. ================================================================
namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Severity of a plan validation issue.
    /// </summary>
    /// <remarks>中文：计划验证问题的严重程度。</remarks>
    public enum IssueSeverity
    {
        /// <summary>Would cause execution failure or data corruption.</summary>
        /// <remarks>中文：会导致执行失败或数据损坏。</remarks>
        Error,

        /// <summary>Likely unintended but won't corrupt data.</summary>
        /// <remarks>中文：很可能是无意的，但不会损坏数据。</remarks>
        Warning,

        /// <summary>Informational suggestion.</summary>
        /// <remarks>中文：信息建议。</remarks>
        Info
    }

    /// <summary>
    /// Architectural ownership of a constraint. Only safety, authorization and
    /// data-integrity constraints may become hard execution barriers.
    /// Goal progress and completion constraints are obligations, not safety
    /// boundaries.
    /// </summary>
    /// <remarks>中文：约束的架构所有权。只有安全性、授权和数据完整性约束可能成为硬性执行障碍。目标进度和完成度限制是义务，而非安全界限。</remarks>
    public enum PlanConstraintClass
    {
        SafetyInvariant,
        AuthorizationBoundary,
        DataIntegrity,
        GoalProgress,
        CompletionObligation,
        Heuristic
    }

    /// <summary>
    /// Runtime action associated with a constraint finding.
    /// </summary>
    /// <remarks>中文：与约束查找相关的运行时动作。</remarks>
    public enum ConstraintEnforcement
    {
        Observe,
        AllowWithObligation,
        Replan,
        Clarify,
        RequireApproval,
        HardBlock
    }

    public enum PlanAdmissibilityDecision
    {
        Execute,
        ExecuteWithObligations,
        Probe,
        Replan,
        Clarify,
        Block
    }

    /// <summary>
    /// A single issue found during plan validation.
    /// </summary>
    /// <remarks>中文：在计划验证过程中发现的单一问题。</remarks>
    public class PlanIssue
    {
        public IssueSeverity Severity { get; set; }
        public PlanConstraintClass ConstraintClass { get; set; } =
            PlanConstraintClass.DataIntegrity;
        public ConstraintEnforcement Enforcement { get; set; } =
            ConstraintEnforcement.HardBlock;
        public double Confidence { get; set; } = 1;
        /// <summary>0-based index into plan.Steps.</summary>
        /// <remarks>中文：以0为基础的索引到平面图。台阶。</remarks>
        public int StepIndex { get; set; }
        public string ToolName { get; set; } = "";
        public string Description { get; set; } = "";
        /// <summary>Suggested fix, if one is known.</summary>
        /// <remarks>中文：如果有解决办法，建议一下。</remarks>
        public string? FixSuggestion { get; set; }
    }

    /// <summary>
    /// Result of validating an AgentPlan against GIS constraints.
    /// </summary>
    /// <remarks>中文：验证AgentPlan与GIS约束的结果。</remarks>
    public class PlanValidationResult
    {
        /// <summary>True if no Error-severity issues found.</summary>
        /// <remarks>中文：如果没有发现错误严重性问题，那是正确的。</remarks>
        public bool IsValid { get; set; }

        /// <summary>All issues found (errors, warnings, info).</summary>
        /// <remarks>中文：所有发现的问题（错误、警告、信息）。</remarks>
        public List<PlanIssue> Issues { get; set; } = new();

        /// <summary>
        /// Augmented steps (original + inserted prerequisites).
        /// Future: auto-insert MakeFeatureLayer before Select, etc.
        /// Currently empty — detection-only phase.
        /// </summary>
        /// <remarks>中文：增补步骤（原 + 插入先修条件）。未来：在选择前自动插入MakeFeatureLayer等。目前空无一人——仅限检测阶段。</remarks>
        public List<PlanStep> AugmentedSteps { get; set; } = new();
    }

    /// <summary>
    /// Separates physical-plan correctness from goal progress. A plan can be
    /// safe and useful without closing the complete goal in one model turn.
    /// </summary>
    /// <remarks>中文：将物理计划的正确性与目标进展分开。一个计划可以安全且有用，而无需在一次模型回合内完成全部目标。</remarks>
    public sealed class PlanAdmissibilityResult
    {
        public PlanAdmissibilityDecision Decision { get; set; }
        public List<PlanIssue> Issues { get; set; } = new();
        public List<string> OutstandingCapabilities { get; set; } = new();
        public List<string> OutstandingTargets { get; set; } = new();
        public bool HasBusinessWrites { get; set; }
        public bool MakesGoalProgress { get; set; }
        public bool CanExecute => Decision is
            PlanAdmissibilityDecision.Execute
            or PlanAdmissibilityDecision.ExecuteWithObligations
            or PlanAdmissibilityDecision.Probe;
    }

    public interface IPlanAdmissibilityEvaluator
    {
        PlanAdmissibilityResult Evaluate(AgentPlan plan);
    }

    /// <summary>
    /// Validates an AgentPlan before execution.
    /// Implementations encode GIS domain rules (dependency chains, CRS consistency, output collisions).
    /// </summary>
    /// <remarks>中文：在执行前验证AgentPlan。实现编码GIS领域规则（依赖链、CRS一致性、输出碰撞）。</remarks>
    public interface IPlanValidator
    {
        /// <summary>
        /// Validate a plan against GIS domain constraints.
        /// </summary>
        /// <param name="plan">LLM-generated execution plan.</param>
        /// <param name="context">Current domain context (layers, CRS, workspace).</param>
        /// <returns>Validation result with issues and optional augmented steps.</returns>
        /// <remarks>中文：验证计划是否符合GIS领域的约束条件。LLM生成的执行计划。当前域上下文（图层、CRS、工作区）。验证结果，包含问题和可选的增强步骤。</remarks>
        PlanValidationResult Validate(AgentPlan plan, DomainContext context);
    }
}
