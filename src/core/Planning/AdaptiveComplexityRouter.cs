using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Core
{
    /// <summary>
    /// Cheap deterministic routing keeps simple desktop interactions on the
    /// one-call path and reserves planner/subagent cost for genuinely complex
    /// or high-risk work.
    /// </summary>
    /// <remarks>中文：廉价的确定性路由使简单的桌面交互保持在单通话路径上，并将规划员/分代理的成本留给真正复杂或高风险的工作。</remarks>
    public sealed class AdaptiveComplexityRouter : IComplexityRouter
    {
        private readonly ComplexityRoutingPolicy _policy;

        public AdaptiveComplexityRouter(ComplexityRoutingPolicy? policy = null)
        {
            _policy = policy ?? new ComplexityRoutingPolicy();
        }

        public ComplexityAssessment Assess(PlanningRequest request)
        {
            int score = 0;
            var reasons = new List<string>();
            int callCount = request.ToolCalls.Count;
            int distinctTools = request.ToolCalls
                .Select(c => c.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            if (callCount > 1)
            {
                score += Math.Min(6, callCount);
                reasons.Add($"{callCount} 个执行步骤");
            }
            if (distinctTools > 1)
            {
                score += Math.Min(4, distinctTools - 1);
                reasons.Add($"{distinctTools} 种工具");
            }

            string goal = request.Goal ?? "";
            if (ContainsAny(
                    goal,
                    "批量", "多个图层", "所有图层", "逐层", "跨图层",
                    "batch", "multiple layers", "all layers", "each layer"))
            {
                score += 3;
                reasons.Add("多目标批处理");
            }
            if (ContainsAny(
                    goal,
                    "删除", "清空", "覆盖", "重建", "替换",
                    "delete", "truncate", "overwrite", "rebuild", "replace"))
            {
                score += 4;
                reasons.Add("高风险意图");
            }
            if (ContainsAny(
                    goal,
                    "如果", "根据", "分别", "然后", "最后",
                    "if ", "depending on", "respectively", "then", "finally"))
            {
                score += 2;
                reasons.Add("条件或依赖链");
            }

            int likelyTargetCount = EstimateTargetCount(request.ToolCalls);
            if (likelyTargetCount > 1)
            {
                score += Math.Min(5, likelyTargetCount - 1);
                reasons.Add($"至少 {likelyTargetCount} 个目标");
            }

            if (request.Effects.Any(effect =>
                    effect.Profile.Effects.HasFlag(DataEffectKind.DeleteDataset)
                    || effect.Profile.Effects.HasFlag(DataEffectKind.DeleteRows)
                    || effect.Profile.Reversibility == ReversibilityKind.Irreversible))
            {
                score += 4;
                reasons.Add("解析后的调用包含删除或不可逆影响");
            }
            if (request.Effects.Any(effect =>
                    effect.Scope == EffectTargetScope.EntireDataset
                    && (effect.Profile.Effects.HasFlag(DataEffectKind.UpdateRows)
                        || effect.Profile.Effects.HasFlag(DataEffectKind.DeleteRows))))
            {
                score += 2;
                reasons.Add("解析后的调用包含全数据集写入");
            }
            if (request.Effects.Any(effect =>
                    effect.Confidence <= EvidenceConfidence.Low
                    || effect.Profile.Confidence <= EvidenceConfidence.Low))
            {
                score += 2;
                reasons.Add("影响证据置信度较低");
            }

            TaskComplexity complexity;
            PlanningMode mode;
            int subagents;
            if (score >= _policy.CriticalThreshold)
            {
                complexity = TaskComplexity.Critical;
                mode = PlanningMode.PlannerWithReviewers;
                subagents = Math.Min(2, Math.Max(0, _policy.MaxReviewers));
            }
            else if (score >= _policy.ComplexThreshold)
            {
                complexity = TaskComplexity.Complex;
                mode = PlanningMode.PlannerWithReviewers;
                subagents = Math.Min(1, Math.Max(0, _policy.MaxReviewers));
            }
            else if (score >= _policy.StandardThreshold)
            {
                complexity = TaskComplexity.Standard;
                mode = callCount > 1
                    ? PlanningMode.Planner
                    : PlanningMode.DeterministicRecipe;
                subagents = 0;
            }
            else
            {
                complexity = TaskComplexity.Simple;
                mode = PlanningMode.Direct;
                subagents = 0;
            }

            return new ComplexityAssessment
            {
                Complexity = complexity,
                Mode = mode,
                Score = score,
                RecommendedSubagents = subagents,
                Reasons = reasons
            };
        }

        private static bool ContainsAny(string value, params string[] terms) =>
            terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

        private static int EstimateTargetCount(IEnumerable<ToolCall> calls)
        {
            int count = 1;
            foreach (var value in calls.SelectMany(c => c.Arguments.Values))
            {
                if (value is System.Collections.IEnumerable sequence && value is not string)
                    count = Math.Max(count, sequence.Cast<object?>().Count());
                else if (value is string text && text.Contains(';'))
                    count = Math.Max(
                        count,
                        text.Split(
                            ';',
                            StringSplitOptions.RemoveEmptyEntries
                            | StringSplitOptions.TrimEntries).Length);
            }
            return count;
        }
    }
}
