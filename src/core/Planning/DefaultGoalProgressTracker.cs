using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Core
{
    /// <summary>
    /// Maintains monotonic, target-level progress across physical plan batches.
    /// A completed subset is durable progress; it is never discarded merely
    /// because the current batch did not cover the whole goal.
    /// </summary>
    public sealed class DefaultGoalProgressTracker : IGoalProgressTracker
    {
        public GoalProgressUpdate Update(
            ResolvedGoalScope scope,
            TargetCoverageResult coverage,
            AgentPlan plan,
            IReadOnlyList<StepExecutionResult> results)
        {
            ArgumentNullException.ThrowIfNull(scope);
            ArgumentNullException.ThrowIfNull(coverage);
            ArgumentNullException.ThrowIfNull(plan);
            ArgumentNullException.ThrowIfNull(results);

            var before = scope.SatisfiedTargets.ToHashSet(
                StringComparer.OrdinalIgnoreCase);
            foreach (string target in coverage.AlreadySatisfiedTargets)
            {
                if (!scope.SatisfiedTargets.Contains(
                        target,
                        StringComparer.OrdinalIgnoreCase))
                {
                    scope.SatisfiedTargets.Add(target);
                }
            }
            var resultByStep = results
                .GroupBy(result => result.StepId)
                .ToDictionary(group => group.Key, group => group.Last());

            if (coverage.IsApplicable)
            {
                IEnumerable<string> attempted = coverage.ExpectedTargets
                    .Where(target => coverage.PlannedTargets.Contains(
                        target,
                        StringComparer.OrdinalIgnoreCase));
                foreach (string target in attempted)
                {
                    var targetSteps = plan.Steps
                        .Where(IsBusinessMutation)
                        .Where(step => StepTargets(step, target))
                        .ToList();
                    if (targetSteps.Count == 0)
                        continue;

                    bool satisfied = targetSteps.All(step =>
                        resultByStep.TryGetValue(step.Id, out var result)
                        && result.Status == StepResultStatus.Completed
                        && result.Validations.All(validation =>
                            validation.Verdict != ValidationVerdict.FAIL));
                    if (satisfied
                        && !scope.SatisfiedTargets.Contains(
                            target,
                            StringComparer.OrdinalIgnoreCase))
                    {
                        scope.SatisfiedTargets.Add(target);
                    }
                }
            }

            coverage.SatisfiedTargets = scope.SatisfiedTargets
                .Where(target => coverage.GoalTargets.Contains(
                    target,
                    StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(target => target, StringComparer.OrdinalIgnoreCase)
                .ToList();
            coverage.ExpectedTargets = coverage.GoalTargets
                .Where(target => !coverage.SatisfiedTargets.Contains(
                    target,
                    StringComparer.OrdinalIgnoreCase))
                .OrderBy(target => target, StringComparer.OrdinalIgnoreCase)
                .ToList();
            coverage.MissingTargets = coverage.ExpectedTargets.ToList();
            coverage.ProgressEvaluated = true;
            coverage.IsGoalComplete = coverage.IsApplicable
                && coverage.GoalTargets.Count > 0
                && coverage.ExpectedTargets.Count == 0
                && coverage.RedundantTargets.Count == 0
                && coverage.UnexpectedTargets.Count == 0
                && coverage.UnresolvedTargets.Count == 0
                && coverage.UnresolvedRequirements.Count == 0;

            var newlySatisfied = coverage.SatisfiedTargets
                .Where(target => !before.Contains(target))
                .OrderBy(target => target, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new GoalProgressUpdate
            {
                NewlySatisfiedTargets = newlySatisfied,
                ResidualTargets = coverage.ExpectedTargets.ToList(),
                IsGoalComplete = coverage.IsGoalComplete
            };
        }

        private static bool IsBusinessMutation(PlanStep step)
        {
            if (step.WriteSet.Count > 0)
                return true;
            DataEffectKind effects =
                step.Effect?.Profile.Effects ?? DataEffectKind.None;
            return (effects & ~DataEffectKind.ReadData) != DataEffectKind.None;
        }

        private static bool StepTargets(PlanStep step, string expected)
        {
            var candidates = step.WriteSet.Concat(new[]
            {
                step.Effect?.Target.Layer ?? "",
                step.Effect?.Target.Dataset ?? ""
            });
            return candidates.Any(candidate =>
                candidate.Equals(
                    expected,
                    StringComparison.OrdinalIgnoreCase)
                || candidate.Replace('/', '\\').EndsWith(
                    "\\" + expected,
                    StringComparison.OrdinalIgnoreCase));
        }
    }
}
