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
namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Severity of a plan validation issue.
    /// </summary>
    public enum IssueSeverity
    {
        /// <summary>Would cause execution failure or data corruption.</summary>
        Error,

        /// <summary>Likely unintended but won't corrupt data.</summary>
        Warning,

        /// <summary>Informational suggestion.</summary>
        Info
    }

    /// <summary>
    /// Architectural ownership of a constraint. Only safety, authorization and
    /// data-integrity constraints may become hard execution barriers.
    /// Goal progress and completion constraints are obligations, not safety
    /// boundaries.
    /// </summary>
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
    public class PlanIssue
    {
        public IssueSeverity Severity { get; set; }
        public PlanConstraintClass ConstraintClass { get; set; } =
            PlanConstraintClass.DataIntegrity;
        public ConstraintEnforcement Enforcement { get; set; } =
            ConstraintEnforcement.HardBlock;
        public double Confidence { get; set; } = 1;
        /// <summary>0-based index into plan.Steps.</summary>
        public int StepIndex { get; set; }
        public string ToolName { get; set; } = "";
        public string Description { get; set; } = "";
        /// <summary>Suggested fix, if one is known.</summary>
        public string? FixSuggestion { get; set; }
    }

    /// <summary>
    /// Result of validating an AgentPlan against GIS constraints.
    /// </summary>
    public class PlanValidationResult
    {
        /// <summary>True if no Error-severity issues found.</summary>
        public bool IsValid { get; set; }

        /// <summary>All issues found (errors, warnings, info).</summary>
        public List<PlanIssue> Issues { get; set; } = new();

        /// <summary>
        /// Augmented steps (original + inserted prerequisites).
        /// Future: auto-insert MakeFeatureLayer before Select, etc.
        /// Currently empty — detection-only phase.
        /// </summary>
        public List<PlanStep> AugmentedSteps { get; set; } = new();
    }

    /// <summary>
    /// Separates physical-plan correctness from goal progress. A plan can be
    /// safe and useful without closing the complete goal in one model turn.
    /// </summary>
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
    public interface IPlanValidator
    {
        /// <summary>
        /// Validate a plan against GIS domain constraints.
        /// </summary>
        /// <param name="plan">LLM-generated execution plan.</param>
        /// <param name="context">Current domain context (layers, CRS, workspace).</param>
        /// <returns>Validation result with issues and optional augmented steps.</returns>
        PlanValidationResult Validate(AgentPlan plan, DomainContext context);
    }
}
