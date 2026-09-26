using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Core
{
    public static class ToolSafetySemantics
    {
        public static ToolSafetyProfile? GetProfile(IToolDescriptor? descriptor)
        {
            return descriptor is IToolSafetyDescriptor safetyDescriptor
                ? safetyDescriptor.SafetyProfile
                : null;
        }

        public static ToolGovernanceMetadata? GetGovernance(IToolDescriptor? descriptor)
        {
            return descriptor is IToolGovernanceDescriptor governanceDescriptor
                ? governanceDescriptor.Governance
                : null;
        }

        public static ToolRiskLevel ResolveRisk(IToolDescriptor? descriptor)
        {
            var profile = GetProfile(descriptor);
            if (profile != null)
                return profile.DeclaredRisk >= profile.MaxInternalRisk
                    ? profile.DeclaredRisk
                    : profile.MaxInternalRisk;

            return descriptor != null
                ? ToolRiskMapper.FromAnnotation(descriptor.Annotation)
                : ToolRiskLevel.Destructive;
        }

        public static bool ShouldEvaluateInfluenceGate(IToolDescriptor? descriptor, ToolRiskLevel riskLevel)
        {
            if (riskLevel != ToolRiskLevel.Modify)
                return false;

            var governance = GetGovernance(descriptor);
            if (governance != null && governance.ModifySubtype != ToolModifySubtype.Unknown)
                return governance.ModifySubtype == ToolModifySubtype.QuantityGateCandidate;

            var profile = GetProfile(descriptor);
            if (profile != null)
            {
                return profile.RequiresReliableScope
                    || profile.Capabilities.HasFlag(ToolSafetyCapability.MutatesExistingData)
                    || profile.Capabilities.HasFlag(ToolSafetyCapability.ChangesSchema);
            }

            return descriptor?.Annotation == ToolAnnotation.Modify;
        }

        public static bool IsProfileConsistent(ToolSafetyProfile profile) =>
            profile.DeclaredRisk >= profile.MaxInternalRisk;
    }
}
