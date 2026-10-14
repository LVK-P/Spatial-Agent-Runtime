using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Core
{
    public enum GoalControlState
    {
        Open,
        Closed,
        ResidualExecutable,
        NeedClarification
    }

    /// <summary>
    /// One post-settlement decision surface shared by normal execution and
    /// approval resumption. The model proposes actions; this controller owns
    /// deterministic goal termination.
    /// </summary>
    /// <remarks>中文：和解后决策表面与正常执行和批准恢复共享。模型提出动作;该控制器拥有确定性目标终止权。</remarks>
    public sealed class DefaultGoalControlPlane
    {
        public GoalControlState Decide(
            ResolvedGoalScope? scope,
            TargetCoverageResult? coverage,
            bool completionIsComplete)
        {
            if (scope == null || coverage == null)
                return GoalControlState.Open;
            if (scope.UnresolvedTargets.Count > 0
                || scope.UnresolvedRequirements.Count > 0
                || coverage.UnresolvedTargets.Count > 0
                || coverage.UnresolvedRequirements.Count > 0)
            {
                return GoalControlState.NeedClarification;
            }
            if (completionIsComplete
                && scope.ProofObligations.Any(proof => proof.Required)
                && coverage.Applicability is
                    CoverageApplicability.Applicable
                    or CoverageApplicability.OutputOnly)
            {
                return GoalControlState.Closed;
            }
            if (coverage.ExpectedTargets.Count > 0
                || scope.ProofObligations.Any(proof =>
                    proof.Required
                    && !scope.SatisfiedProofObligations.Contains(
                        proof.Id,
                        StringComparer.OrdinalIgnoreCase)))
            {
                return GoalControlState.ResidualExecutable;
            }
            return GoalControlState.Open;
        }
    }
}
