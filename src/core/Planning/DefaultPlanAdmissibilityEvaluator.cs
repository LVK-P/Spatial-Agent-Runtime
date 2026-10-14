using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Core
{
    /// <summary>
    /// Converts independent validator findings into one runtime decision.
    ///
    /// The evaluator deliberately distinguishes:
    /// - hard safety / authorization / integrity boundaries;
    /// - goal-progress obligations that may remain open across model rounds;
    /// - read-only probes that improve knowledge without claiming completion.
    ///
    /// It never evaluates permission, backup or host freshness. Those remain
    /// authoritative in SafetyGate and Scheduler immediately before execution.
    /// </summary>
    /// <remarks>中文：将独立验证者的发现转换为一个运行时决策。评估者有意区分：- 硬安全/授权/完整性边界;- 目标进度义务，可能在模型轮次中保持开放;- 只读探针，提升知识但不声称完成。它从不评估权限、备份或主机的新鲜度。这些在执行前立即在SafetyGate和Scheduler中保持权威。</remarks>
    public sealed class DefaultPlanAdmissibilityEvaluator :
        IPlanAdmissibilityEvaluator
    {
        public PlanAdmissibilityResult Evaluate(AgentPlan plan)
        {
            ArgumentNullException.ThrowIfNull(plan);

            var result = new PlanAdmissibilityResult();
            PlanValidationResult validation = plan.Validation
                ?? new PlanValidationResult { IsValid = true };

            var validationErrors = validation.Issues
                .Where(issue => issue.Severity == IssueSeverity.Error)
                .ToList();
            if (validationErrors.Any(issue =>
                    issue.Enforcement is
                        ConstraintEnforcement.HardBlock
                        or ConstraintEnforcement.RequireApproval))
            {
                result.Decision = PlanAdmissibilityDecision.Block;
                result.Issues.AddRange(validationErrors);
                return result;
            }
            if (validationErrors.Any(issue =>
                    issue.Enforcement == ConstraintEnforcement.Clarify))
            {
                result.Decision = PlanAdmissibilityDecision.Clarify;
                result.Issues.AddRange(validationErrors);
                return result;
            }
            if (validationErrors.Any(issue =>
                    issue.Enforcement == ConstraintEnforcement.Replan))
            {
                result.Decision = PlanAdmissibilityDecision.Replan;
                result.Issues.AddRange(validationErrors);
                return result;
            }
            if (validationErrors.Count > 0)
            {
                // Error-severity findings without an executable disposition
                // remain fail-closed for backward-compatible validators.
                // 中文：没有可执行判定的错误严重性发现对向后兼容的验证者来说仍然是失败封闭状态。
                result.Decision = PlanAdmissibilityDecision.Block;
                result.Issues.AddRange(validationErrors);
                return result;
            }

            if (plan.Steps.Count == 0)
            {
                result.Decision = PlanAdmissibilityDecision.Replan;
                result.Issues.Add(Issue(
                    PlanConstraintClass.GoalProgress,
                    ConstraintEnforcement.Replan,
                    "plan-admissibility",
                    "计划没有可执行步骤。",
                    "生成一个只读探测或一个能推进当前目标的工具调用。"));
                return result;
            }

            bool hasBusinessWrites = plan.Steps.Any(IsBusinessWrite);
            result.HasBusinessWrites = hasBusinessWrites;

            TargetCoverageResult? coverage = plan.Coverage;
            CapabilityClosureResult? closure = plan.CapabilityClosure;
            if (closure != null)
            {
                result.OutstandingCapabilities = closure.MissingCapabilities
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            if (coverage != null)
            {
                result.OutstandingTargets = coverage.MissingTargets
                    .Concat(coverage.UnresolvedTargets)
                    .Concat(coverage.UnresolvedRequirements)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            // A read-only action is an epistemic probe. Goal closure is not an
            // execution prerequisite because the probe cannot mutate business
            // state or falsely satisfy a mutating obligation.
            // 中文：只读动作是用于获取信息的探测。目标闭合不是其执行前提，因为探测不会改变业务状态，也不会虚假满足变更义务。
            if (!hasBusinessWrites)
            {
                result.Decision = PlanAdmissibilityDecision.Probe;
                result.MakesGoalProgress = true;
                if (result.OutstandingCapabilities.Count > 0
                    || result.OutstandingTargets.Count > 0)
                {
                    result.Issues.Add(Issue(
                        PlanConstraintClass.GoalProgress,
                        ConstraintEnforcement.AllowWithObligation,
                        "progress-obligations",
                        "只读探测不会关闭完整目标；未完成能力和目标继续保留。",
                        "根据探测结果规划下一批安全动作。",
                        IssueSeverity.Info));
                }
                return result;
            }

            if (coverage?.Applicability ==
                CoverageApplicability.ScopeInsufficient)
            {
                result.Decision = PlanAdmissibilityDecision.Clarify;
                result.Issues.Add(Issue(
                    PlanConstraintClass.AuthorizationBoundary,
                    ConstraintEnforcement.Clarify,
                    "scope-sufficiency",
                    "目标范围不足以授权写操作。",
                    "先用只读探测解析目标；仍无法唯一确定时请求用户澄清。"));
                return result;
            }

            if (coverage?.Applicability == CoverageApplicability.NotInScope)
            {
                result.Decision = PlanAdmissibilityDecision.Clarify;
                result.Issues.Add(Issue(
                    PlanConstraintClass.AuthorizationBoundary,
                    ConstraintEnforcement.Clarify,
                    "mutation-scope",
                    "写计划缺少可验证的目标范围或新输出边界。",
                    "解析现有业务目标；如果只创建新成果，必须声明 OutputOnly 效应且不得覆盖已有输出。"));
                return result;
            }

            if (coverage?.UnresolvedTargets.Count > 0)
            {
                result.Decision = PlanAdmissibilityDecision.Clarify;
                result.Issues.Add(Issue(
                    PlanConstraintClass.AuthorizationBoundary,
                    ConstraintEnforcement.Clarify,
                    "target-resolution",
                    "写计划包含尚未解析的业务目标："
                        + string.Join("、", coverage.UnresolvedTargets),
                    "不要推测替代对象；先执行只读探测或请求澄清。"));
                return result;
            }

            if (coverage?.UnresolvedRequirements.Count > 0)
            {
                result.Decision = PlanAdmissibilityDecision.Clarify;
                result.Issues.Add(Issue(
                    PlanConstraintClass.AuthorizationBoundary,
                    ConstraintEnforcement.Clarify,
                    "requirement-grounding",
                    "写计划依赖尚未绑定到实际 GIS 资产的目标要求："
                        + string.Join("、", coverage.UnresolvedRequirements),
                    "先执行只读探测或使用字段资产来源完成绑定；仍无法唯一确定时请求用户澄清。"));
                return result;
            }

            if (coverage?.UnexpectedTargets.Count > 0
                || coverage?.RedundantTargets.Count > 0)
            {
                var details = new List<string>();
                if (coverage.UnexpectedTargets.Count > 0)
                    details.Add("合同外目标："
                        + string.Join("、", coverage.UnexpectedTargets));
                if (coverage.RedundantTargets.Count > 0)
                    details.Add("重复谱系目标："
                        + string.Join("、", coverage.RedundantTargets));
                result.Decision = PlanAdmissibilityDecision.Replan;
                result.Issues.Add(Issue(
                    PlanConstraintClass.AuthorizationBoundary,
                    ConstraintEnforcement.Replan,
                    "target-boundary",
                    string.Join("；", details),
                    "移除边界外写入；合法目标子集可以单独执行。"));
                return result;
            }

            var required = closure?.RequiredCapabilities
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var provided = closure?.ProvidedCapabilities
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool hasDeclaredContribution = required.Count > 0
                && provided.Overlaps(required);
            bool hasTargetContribution = required.Count == 0
                && coverage?.Applicability == CoverageApplicability.Applicable
                && coverage.PlannedTargets.Any(target =>
                    coverage.ExpectedTargets.Contains(
                        target,
                        StringComparer.OrdinalIgnoreCase)
                    || coverage.GoalTargets.Contains(
                        target,
                        StringComparer.OrdinalIgnoreCase));
            bool hasContainedOutputContribution = required.Count == 0
                && coverage?.Applicability == CoverageApplicability.OutputOnly;
            bool makesProgress = hasDeclaredContribution
                || hasTargetContribution
                || hasContainedOutputContribution;
            result.MakesGoalProgress = makesProgress;

            if (!makesProgress)
            {
                result.Decision = PlanAdmissibilityDecision.Replan;
                result.Issues.Add(Issue(
                    PlanConstraintClass.GoalProgress,
                    ConstraintEnforcement.Replan,
                    "capability-contribution",
                    "当前写计划没有声明任何目标所需能力，也没有可验证的目标或新输出进展。",
                    "改为只读探测，或选择显式声明所需能力的工具；启发式相似度不能授权写入。"));
                return result;
            }

            if (result.OutstandingCapabilities.Count > 0)
            {
                string plausible = closure?.PlausibleButUnconfirmed.Count > 0
                    ? "；其中可能存在工具能力声明漏项："
                        + string.Join(
                            "、",
                            closure.PlausibleButUnconfirmed)
                    : "";
                result.Issues.Add(Issue(
                    PlanConstraintClass.CompletionObligation,
                    ConstraintEnforcement.AllowWithObligation,
                    "capability-closure",
                    "当前批次尚未关闭全部目标能力："
                        + string.Join("、", result.OutstandingCapabilities)
                        + plausible,
                    "允许执行有贡献的步骤；剩余能力继续保留，完成态由执行证据判定。",
                    IssueSeverity.Warning));
            }

            if (coverage?.MissingTargets.Count > 0)
            {
                result.Issues.Add(Issue(
                    PlanConstraintClass.CompletionObligation,
                    ConstraintEnforcement.AllowWithObligation,
                    "target-coverage",
                    "当前批次仅覆盖目标子集；剩余："
                        + string.Join("、", coverage.MissingTargets),
                    "先执行已覆盖目标，成功结果入账后再处理剩余目标。",
                    IssueSeverity.Warning));
            }

            bool hasOutstanding = result.OutstandingCapabilities.Count > 0
                || result.OutstandingTargets.Count > 0;
            result.Decision = hasOutstanding
                ? PlanAdmissibilityDecision.ExecuteWithObligations
                : PlanAdmissibilityDecision.Execute;
            return result;
        }

        private static bool IsBusinessWrite(PlanStep step)
        {
            if (step.WriteSet.Count > 0)
                return true;
            DataEffectKind effects =
                step.Effect?.Profile.Effects ?? DataEffectKind.None;
            return (effects & ~DataEffectKind.ReadData) != DataEffectKind.None;
        }

        private static PlanIssue Issue(
            PlanConstraintClass constraintClass,
            ConstraintEnforcement enforcement,
            string toolName,
            string description,
            string fixSuggestion,
            IssueSeverity severity = IssueSeverity.Error) =>
            new()
            {
                Severity = severity,
                ConstraintClass = constraintClass,
                Enforcement = enforcement,
                StepIndex = -1,
                ToolName = toolName,
                Description = description,
                FixSuggestion = fixSuggestion
            };
    }
}
