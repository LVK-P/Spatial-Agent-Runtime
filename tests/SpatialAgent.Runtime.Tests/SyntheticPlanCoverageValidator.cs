using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Runtime.Tests
{
    /// <summary>
    /// Test-only authorization boundary for orchestration tests whose fake
    /// tools intentionally have no ArcGIS catalog or Goal target. Production
    /// coverage remains fail-closed for existing-state mutation without scope.
    /// </summary>
    /// <remarks>中文：仅用于测试的授权边界，适用于故意不提供 ArcGIS 目录或目标的编排测试。对于未限定范围的现有状态变更，生产覆盖验证仍保持失败关闭。</remarks>
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
