using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Core
{
    /// <summary>
    /// Folds verified field-producing and field-removing transitions into the
    /// cross-turn domain artifact ledger. The ledger is driven by invocation
    /// effects and logical capabilities, never by a physical tool name.
    /// </summary>
    public sealed class DefaultDomainArtifactLedger
    {
        public void Apply(
            DomainContext domain,
            StepExecutionResult result)
        {
            ArgumentNullException.ThrowIfNull(domain);
            ArgumentNullException.ThrowIfNull(result);
            if (!result.IsSuccessful || result.Effect == null)
                return;

            string field = result.Effect.Target.Field?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(field))
                return;
            string owner = string.IsNullOrWhiteSpace(
                    result.Effect.Target.Layer)
                ? result.Effect.Target.Dataset
                : result.Effect.Target.Layer;
            owner = MatchLayerName(domain, owner);
            if (string.IsNullOrWhiteSpace(owner))
                return;

            if (result.Effect.DesiredTargetState ==
                TargetStateExpectation.Absent)
            {
                domain.Artifacts.RemoveAll(artifact =>
                    artifact.OwnerLayer.Equals(
                        owner,
                        StringComparison.OrdinalIgnoreCase)
                    && artifact.FieldName.Equals(
                        field,
                        StringComparison.OrdinalIgnoreCase));
                return;
            }

            string semanticType = result.ProvidedCapabilities.FirstOrDefault(
                capability => capability is
                    GisCapabilityIds.MeasureLength
                    or GisCapabilityIds.MeasureArea) ?? "";
            if (string.IsNullOrWhiteSpace(semanticType))
                return;

            var layer = domain.Layers.FirstOrDefault(candidate =>
                candidate.Name.Equals(
                    owner,
                    StringComparison.OrdinalIgnoreCase));
            var existing = domain.Artifacts.FirstOrDefault(artifact =>
                artifact.OwnerLayer.Equals(
                    owner,
                    StringComparison.OrdinalIgnoreCase)
                && artifact.FieldName.Equals(
                    field,
                    StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                existing = new DomainArtifactRecord
                {
                    OwnerLayer = owner,
                    FieldName = field
                };
                domain.Artifacts.Add(existing);
            }

            existing.DatasetId = layer?.DatasetId ?? "";
            existing.SemanticType = semanticType;
            existing.ProducerCapability = semanticType;
            existing.ProducerTool = result.ToolName;
            existing.StateFingerprint = layer?.StateFingerprint ?? "";
        }

        private static string MatchLayerName(
            DomainContext domain,
            string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                return "";
            string normalized = candidate.Trim()
                .Replace('/', '\\')
                .TrimEnd('\\');
            return domain.Layers.FirstOrDefault(layer =>
                normalized.Equals(
                    layer.Name,
                    StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith(
                    "\\" + layer.Name,
                    StringComparison.OrdinalIgnoreCase))?.Name ?? "";
        }
    }
}
