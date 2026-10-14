using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Core
{
    /// <summary>
    /// Pure deterministic policy kernel. LLM planners and reviewers may supply
    /// evidence, but they cannot override this decision.
    /// </summary>
    /// <remarks>中文：纯确定性策略内核。LLM的规划者和审核员可以提供证据，但他们不能推翻这一决定。</remarks>
    public sealed class DeterministicSafetyKernel : IInvocationSafetyKernel
    {
        public SafetyDecision Evaluate(
            InvocationEffect effect,
            SafetyDecision catalogDecision,
            AgentPermissionLevel permissionLevel)
        {
            ToolRiskLevel effectRisk = RiskFromEffect(effect);
            ToolRiskLevel risk = effectRisk > catalogDecision.RiskLevel
                ? effectRisk
                : catalogDecision.RiskLevel;

            var decision = new SafetyDecision
            {
                ToolName = effect.ToolName,
                RiskLevel = risk,
                Effect = effect,
                Reason = catalogDecision.Reason
            };

            if (catalogDecision.Kind == SafetyDecisionKind.Block)
            {
                decision.Kind = SafetyDecisionKind.Block;
                return decision;
            }

            decision.Kind = permissionLevel switch
            {
                AgentPermissionLevel.FullAccess => SafetyDecisionKind.Allow,
                AgentPermissionLevel.RequestApproval when risk == ToolRiskLevel.Destructive
                    => SafetyDecisionKind.RequireApproval,
                AgentPermissionLevel.RequestApproval => catalogDecision.Kind,
                AgentPermissionLevel.ReadOnly when risk == ToolRiskLevel.ReadOnly
                    => SafetyDecisionKind.Allow,
                _ => SafetyDecisionKind.Block
            };

            if (decision.Kind == SafetyDecisionKind.RequireApproval
                && string.IsNullOrWhiteSpace(decision.Reason))
                decision.Reason = effect.Summary;
            if (decision.Kind == SafetyDecisionKind.Block
                && string.IsNullOrWhiteSpace(decision.Reason))
                decision.Reason = $"当前权限不允许执行：{effect.Summary}";

            return decision;
        }

        private static ToolRiskLevel RiskFromEffect(InvocationEffect effect)
        {
            var kinds = effect.Profile.Effects;
            if (kinds.HasFlag(DataEffectKind.DeleteDataset)
                || kinds.HasFlag(DataEffectKind.DeleteRows)
                || effect.Profile.Reversibility == ReversibilityKind.Irreversible)
                return ToolRiskLevel.Destructive;

            if (kinds.HasFlag(DataEffectKind.OverwriteOutput)
                || kinds.HasFlag(DataEffectKind.AlterSchema)
                || kinds.HasFlag(DataEffectKind.UpdateRows)
                || kinds.HasFlag(DataEffectKind.ModifyGeometry)
                || kinds.HasFlag(DataEffectKind.ModifySpatialReference)
                || kinds.HasFlag(DataEffectKind.ModifyMapState)
                || kinds.HasFlag(DataEffectKind.ExternalPublish))
                return ToolRiskLevel.Modify;

            if (kinds.HasFlag(DataEffectKind.CreateDataset)
                || kinds.HasFlag(DataEffectKind.AppendRows))
                return ToolRiskLevel.Increment;

            return ToolRiskLevel.ReadOnly;
        }
    }
}
