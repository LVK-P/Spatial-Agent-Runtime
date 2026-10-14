using SpatialAgent.Core;
using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Runtime
{
    public enum GoalOutcomeStatus
    {
        Completed,
        CompletedWithWarnings,
        Partial,
        Failed,
        ClarificationRequired
    }

    /// <summary>
    /// Reconciles the resolved goal with execution and verification evidence.
    /// The user-facing outcome is goal-first; tool steps remain available in
    /// the execution timeline and are not used as a substitute for intent
    /// completion.
    /// </summary>
    /// <remarks>中文：将已解决的目标与执行和验证证据进行调和。面向用户的结果是优先的;工具步骤仍可在执行时间轴中保留，且不作为意图完成的替代。</remarks>
    public sealed class GoalCompletionReport
    {
        public string Goal { get; init; } = "";
        public List<GoalCompletionItem> Items { get; init; } = new();
        public List<GoalCompletionItem> SupportItems { get; init; } = new();
        public int PassedVerificationCount { get; init; }
        public GoalOutcomeStatus Outcome { get; init; }
        public int GoalTargetCount { get; init; }
        public int SatisfiedTargetCount { get; init; }
        public int RequiredProofCount { get; init; }
        public int SatisfiedProofCount { get; init; }
        public List<string> MissingTargets { get; init; } = new();
        public List<string> UnexpectedTargets { get; init; } = new();
        public List<string> UnresolvedTargets { get; init; } = new();
        public List<GoalResourceBinding> UnresolvedBindings { get; init; } = new();
        public List<string> UnresolvedRequirements { get; init; } = new();
        public List<string> MissingProofs { get; init; } = new();
        public bool IsComplete => Outcome is
            GoalOutcomeStatus.Completed
            or GoalOutcomeStatus.CompletedWithWarnings;
    }

    public sealed class GoalCompletionItem
    {
        public int StepId { get; init; }
        public string ToolName { get; init; } = "";
        public StepResultStatus Status { get; init; }
        public ApprovalTarget Target { get; init; } = new();
        public string Detail { get; init; } = "";
        public bool Success { get; init; }
    }

    public static class CompletionReportBuilder
    {
        public static GoalCompletionReport Build(
            string goal,
            IReadOnlyList<StepExecutionResult> steps,
            ResolvedGoalScope? scope = null,
            TargetCoverageResult? coverage = null)
        {
            // New plans carry an explicit purpose. Older checkpoints and
            // direct callers do not, so retain the legacy effect-based split
            // only for Unspecified records during the v11 -> v12 migration.
            // 中文：新计划有明确的目的。旧的检查点和直接调用器则不支持，因此在v11->v12迁移期间，仅保留未指定记录的基于遗留效应的分割。
            var operational = steps.Where(step =>
                step.Purpose == PlanStepPurpose.Business
                || (step.Purpose == PlanStepPurpose.Unspecified
                    && IsLegacyBusiness(step))).ToList();
            var support = steps.Where(step =>
                step.Purpose == PlanStepPurpose.SupportProbe
                || (step.Purpose == PlanStepPurpose.Unspecified
                    && !IsLegacyBusiness(step))).ToList();
            var items = operational.Select(ToItem).ToList();
            var goalTargets = coverage?.GoalTargets ?? new List<string>();
            var satisfiedTargets = scope?.SatisfiedTargets
                .Where(target => goalTargets.Contains(
                    target, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
                ?? new List<string>();
            var unresolved = scope?.UnresolvedTargets
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
                ?? new List<string>();
            var unresolvedRequirements = scope?.UnresolvedRequirements
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
                ?? new List<string>();
            var unresolvedBindings = scope?.ResourceBindings
                .Where(binding => binding.Status is
                    GoalResourceResolutionStatus.Missing
                    or GoalResourceResolutionStatus.Ambiguous
                    or GoalResourceResolutionStatus.CandidateFoundAwaitingConfirmation)
                .ToList()
                ?? new List<GoalResourceBinding>();
            var missing = goalTargets
                .Except(satisfiedTargets, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var unexpected = coverage?.UnexpectedTargets
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
                ?? new List<string>();
            GoalProofEvaluation proofEvaluation = scope == null
                ? new GoalProofEvaluation()
                : new DefaultGoalClosureEvaluator().Evaluate(
                    scope,
                    coverage,
                    steps);

            GoalOutcomeStatus outcome;
            bool goalStateClosed = scope != null
                && coverage != null
                && unresolved.Count == 0
                && unresolvedRequirements.Count == 0
                && missing.Count == 0
                && unexpected.Count == 0
                && proofEvaluation.MissingProofs.Count == 0
                && (coverage.ProgressEvaluated
                    ? coverage.IsGoalComplete
                    : coverage.Applicability == CoverageApplicability.OutputOnly
                        && coverage.IsComplete
                        && items.Any(item => item.Success));
            if (unresolved.Count > 0
                || unresolvedRequirements.Count > 0)
                outcome = GoalOutcomeStatus.ClarificationRequired;
            else if (goalStateClosed)
                outcome = items.Any(item => !item.Success)
                    ? GoalOutcomeStatus.CompletedWithWarnings
                    : GoalOutcomeStatus.Completed;
            else if (items.Count == 0 || items.All(item => !item.Success))
                outcome = GoalOutcomeStatus.Failed;
            else if (items.Any(item => !item.Success)
                || missing.Count > 0
                || unexpected.Count > 0
                || proofEvaluation.MissingProofs.Count > 0)
                outcome = GoalOutcomeStatus.Partial;
            else
                outcome = GoalOutcomeStatus.Completed;

            return new GoalCompletionReport
            {
                Goal = goal?.Trim() ?? "",
                Items = items,
                SupportItems = support.Select(ToItem).ToList(),
                PassedVerificationCount = steps.Sum(step =>
                    step.Validations.Count(validation =>
                        validation.Verdict == ValidationVerdict.PASS)),
                Outcome = outcome,
                GoalTargetCount = goalTargets.Count,
                SatisfiedTargetCount = satisfiedTargets.Count,
                RequiredProofCount = proofEvaluation.RequiredProofs.Count,
                SatisfiedProofCount = proofEvaluation.SatisfiedProofs.Count,
                MissingTargets = missing,
                UnexpectedTargets = unexpected,
                UnresolvedTargets = unresolved,
                UnresolvedBindings = unresolvedBindings,
                UnresolvedRequirements = unresolvedRequirements,
                MissingProofs = proofEvaluation.MissingProofs
            };
        }

        private static GoalCompletionItem ToItem(StepExecutionResult step) => new()
        {
            StepId = step.StepId,
            ToolName = step.ToolName,
            Status = step.Status,
            Target = ResolveTarget(step),
            Detail = ResolveDetail(step),
            Success = step.IsSuccessful
        };

        public static string Render(GoalCompletionReport report)
        {
            int succeeded = report.Items.Count(item => item.Success);
            int failed = report.Items.Count - succeeded;
            string goal = string.IsNullOrWhiteSpace(report.Goal)
                ? "当前请求"
                : TrimTo(report.Goal, 300);
            var lines = new List<string>();

            switch (report.Outcome)
            {
                case GoalOutcomeStatus.Completed:
                    lines.Add($"已完成：{goal}");
                    break;
                case GoalOutcomeStatus.CompletedWithWarnings:
                    lines.Add($"已完成（有警告）：{goal}");
                    lines.Add("目标状态和必需证据已经闭合；执行过程中存在被阻止、跳过或失败的历史步骤。未重复执行已满足的目标。");
                    break;
                case GoalOutcomeStatus.Partial:
                    lines.Add($"部分完成：{goal}");
                    break;
                case GoalOutcomeStatus.ClarificationRequired:
                    lines.Add($"尚未执行目标变更：{goal}");
                    RenderResourceIssues(report, lines);
                    if (report.UnresolvedRequirements.Count > 0)
                    {
                        lines.Add("以下目标要求尚未绑定到可验证的 GIS 资产："
                            + string.Join(
                                "、",
                                report.UnresolvedRequirements)
                            + "。请先探测资产来源或澄清目标属性。");
                    }
                    break;
                default:
                    lines.Add($"未完成：{goal}");
                    break;
            }

            if (report.UnexpectedTargets.Count > 0)
                lines.Add("计划包含目标合同之外的对象：" +
                    string.Join("、", report.UnexpectedTargets) + "。");
            if (report.RequiredProofCount > 0)
            {
                lines.Add($"目标证据：{report.SatisfiedProofCount}/{report.RequiredProofCount} 项闭合。");
                if (report.MissingProofs.Count > 0)
                    lines.Add("尚缺证据：" +
                        string.Join("、", report.MissingProofs) + "。");
            }

            if (report.GoalTargetCount > 0)
            {
                lines.Add($"目标结果：{report.SatisfiedTargetCount}/{report.GoalTargetCount} 个目标已满足。");
                if (report.MissingTargets.Count > 0
                    && report.Outcome != GoalOutcomeStatus.ClarificationRequired)
                    lines.Add("尚未满足：" + string.Join("、", report.MissingTargets) + "。");
            }
            else if (report.Items.Count > 0)
            {
                lines.Add($"业务结果：{succeeded}/{report.Items.Count} 项成功"
                    + (failed > 0 ? $"，{failed} 项未成功。" : "。"));
            }

            var failedItems = report.Items
                .Where(item => !item.Success)
                .Take(5)
                .ToList();
            foreach (GoalCompletionItem item in failedItems)
            {
                lines.Add($"未成功：{FormatItemTarget(item)}；"
                    + TrimTo(item.Detail, 240) + "。");
            }
            if (failed > failedItems.Count)
                lines.Add($"另有 {failed - failedItems.Count} 项未成功，详见执行日志。");

            if (report.SupportItems.Count > 0)
            {
                int supportSucceeded = report.SupportItems.Count(item => item.Success);
                lines.Add($"支持性检查：{supportSucceeded}/{report.SupportItems.Count} 项完成。");
            }

            lines.Add(report.PassedVerificationCount > 0
                ? $"验证结果：{report.PassedVerificationCount} 项通过。"
                : "验证结果：没有可用的后置验证记录。");
            return string.Join(Environment.NewLine, lines);
        }

        private static void RenderResourceIssues(
            GoalCompletionReport report,
            ICollection<string> lines)
        {
            var represented = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (GoalResourceBinding binding in report.UnresolvedBindings)
            {
                represented.Add(binding.Mention);
                var candidates = binding.Candidates
                    .Where(candidate => binding.EvidenceBackedCandidateIds.Count == 0
                        || binding.EvidenceBackedCandidateIds.Contains(
                            candidate.CandidateId,
                            StringComparer.OrdinalIgnoreCase))
                    .Select(candidate => candidate.DisplayName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                switch (binding.Status)
                {
                    case GoalResourceResolutionStatus.Missing:
                        lines.Add($"当前项目中未找到“{binding.Mention}”的可验证候选。请确认名称或数据来源。");
                        break;
                    case GoalResourceResolutionStatus.CandidateFoundAwaitingConfirmation:
                        lines.Add($"已通过只读探测确认“{binding.Mention}”存在候选："
                            + FormatCandidates(candidates)
                            + "；候选身份已保留，但仍需确认后才能授权目标变更。");
                        break;
                    case GoalResourceResolutionStatus.Ambiguous:
                        var allCandidates = binding.Candidates
                            .Select(candidate => candidate.DisplayName)
                            .Where(name => !string.IsNullOrWhiteSpace(name))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        lines.Add($"“{binding.Mention}”匹配多个候选："
                            + FormatCandidates(allCandidates)
                            + "；请确认要使用的资源。");
                        break;
                }
            }

            var legacy = report.UnresolvedTargets
                .Where(target => !represented.Contains(target))
                .ToList();
            if (legacy.Count > 0)
            {
                lines.Add("以下资源保持未解析状态："
                    + string.Join("、", legacy)
                    + "。请确认名称或目标图层。");
            }
        }

        private static string FormatCandidates(IReadOnlyCollection<string> candidates) =>
            candidates.Count == 0
                ? "（候选身份未完整记录）"
                : string.Join("、", candidates);

        private static ApprovalTarget ResolveTarget(StepExecutionResult step)
        {
            var source = step.Effect?.Target;
            var target = new ApprovalTarget
            {
                Dataset = source?.Dataset ?? "",
                Layer = source?.Layer ?? "",
                Field = source?.Field ?? "",
                WhereClause = source?.WhereClause ?? "",
                OutputTarget = source?.OutputTarget ?? "",
                Signature = source?.Signature ?? ""
            };

            if (string.IsNullOrWhiteSpace(target.Dataset)
                && string.IsNullOrWhiteSpace(target.Layer))
            {
                target.Dataset = ParameterSemanticResolver.FirstValue(
                    step.InputParameters, null, ParameterSemanticKind.InputDataset);
            }
            if (string.IsNullOrWhiteSpace(target.Field))
            {
                target.Field = ParameterSemanticResolver.FirstValue(
                    step.InputParameters, null, ParameterSemanticKind.TargetField);
                if (string.IsNullOrWhiteSpace(target.Field))
                    target.Field = ParameterSemanticResolver.FirstValue(
                        step.InputParameters, null, ParameterSemanticKind.Field);
            }
            if (string.IsNullOrWhiteSpace(target.OutputTarget))
            {
                target.OutputTarget = ParameterSemanticResolver.FirstValue(
                    step.InputParameters, null, ParameterSemanticKind.OutputDataset);
            }
            return target;
        }

        private static string ResolveDetail(StepExecutionResult step)
        {
            if (step.Status == StepResultStatus.AwaitingApproval)
                return step.ApprovalReason ?? "等待批准";
            if (step.Status == StepResultStatus.Blocked)
                return step.BlockReason ?? "被安全策略阻止";
            if (step.Status == StepResultStatus.Failed)
                return step.Validations.FirstOrDefault(validation =>
                        validation.Verdict == ValidationVerdict.FAIL)?.Detail
                    ?? step.ToolResult?.Error
                    ?? "工具执行失败";
            if (step.TransitionOutcome == StateTransitionOutcome.AlreadySatisfied)
                return "执行前目标状态已经满足，本次未产生重复变更";
            return step.Validations.FirstOrDefault(validation =>
                validation.Verdict == ValidationVerdict.FAIL)?.Detail ?? "";
        }

        private static bool IsMutating(InvocationEffect? effect)
        {
            var kinds = effect?.Profile.Effects ?? DataEffectKind.None;
            return (kinds & ~DataEffectKind.ReadData) != DataEffectKind.None;
        }

        private static bool IsLegacyBusiness(StepExecutionResult step)
        {
            if (IsMutating(step.Effect))
                return true;
            DataEffectKind effects =
                step.Effect?.Profile.Effects ?? DataEffectKind.None;
            if (effects == DataEffectKind.ReadData)
                return false;
            ApprovalTarget? target = step.Effect?.Target;
            return target != null
                && (!string.IsNullOrWhiteSpace(target.Dataset)
                    || !string.IsNullOrWhiteSpace(target.Layer)
                    || !string.IsNullOrWhiteSpace(target.Field)
                    || !string.IsNullOrWhiteSpace(target.OutputTarget));
        }

        private static string FormatItemTarget(GoalCompletionItem item)
        {
            if (!string.IsNullOrWhiteSpace(item.Target.OutputTarget))
                return item.Target.OutputTarget;
            if (!string.IsNullOrWhiteSpace(item.Target.Layer))
                return string.IsNullOrWhiteSpace(item.Target.Field)
                    ? item.Target.Layer
                    : $"{item.Target.Layer}.{item.Target.Field}";
            if (!string.IsNullOrWhiteSpace(item.Target.Dataset))
                return item.Target.Dataset;
            return item.ToolName;
        }

        private static string TrimTo(string value, int maxChars) =>
            value.Length <= maxChars ? value : value[..(maxChars - 1)] + "…";
    }

    /// <summary>
    /// Composes deterministic goal state with a model-authored clarification.
    /// The model text is supplemental and can never replace or upgrade Outcome.
    /// </summary>
    /// <remarks>中文：结合确定性目标状态与模型作者的说明。模型文本是补充性的，永远无法替代或升级Outcome。</remarks>
    public static class CompletionResponseArbitrator
    {
        public static string Compose(
            GoalCompletionReport report,
            string? modelText)
        {
            string deterministic = CompletionReportBuilder.Render(report);
            if (report.Outcome != GoalOutcomeStatus.ClarificationRequired
                || string.IsNullOrWhiteSpace(modelText))
                return deterministic;
            return deterministic
                + Environment.NewLine
                + Environment.NewLine
                + "待确认问题（模型建议，不改变上述确定性状态）："
                + Environment.NewLine
                + modelText.Trim();
        }
    }
}
