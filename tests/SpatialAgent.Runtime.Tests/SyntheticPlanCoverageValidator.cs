using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Runtime.Tests
{
    /// <summary>
    /// Test-only authorization boundary for orchestration tests whose fake
    /// tools intentionally have no ArcGIS catalog or Goal target. Production
    /// coverage remains fail-closed for existing-state mutation without scope.
    /// </summary>
    internal sealed class SyntheticPlanCoverageValidator :
        IPlanCoverageValidator
    {
        public TargetCoverageResult Validate(
            ResolvedGoalScope scope,
            AgentPlan plan,
            DomainContext context) =>
            new()
            {
                IsApplicable = false,
                Applicability = CoverageApplicability.OutputOnly,
                IsComplete = true,
                ScopeBasis = "test-fixture:contained-output"
            };
    }
}
