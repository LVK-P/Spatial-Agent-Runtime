using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Core
{
    public static class ParameterScopedProofBuilder
    {
        public static List<ParameterScopedProofRequirement> Build(
            PlanStep step,
            IToolDescriptor? descriptor,
            IReadOnlyCollection<GoalParameterConstraint> constraints,
            IReadOnlyCollection<GoalProofObligation> obligations,
            DomainContext domain)
        {
            if (step.ToolCall == null)
                return new List<ParameterScopedProofRequirement>();

            var requirements = new List<ParameterScopedProofRequirement>();
            foreach (string capability in step.ProvidedCapabilities.Where(
                         item => item is GisCapabilityIds.MeasureArea
                             or GisCapabilityIds.MeasureLength))
            {
                GoalProofObligation? proof = obligations.FirstOrDefault(item =>
                    item.Required
                    && item.Kind == GoalProofKind.ParameterSemantics
                    && item.CapabilityId.Equals(
                        capability,
                        StringComparison.OrdinalIgnoreCase));
                if (proof == null)
                    continue;

                var scoped = constraints.Where(item =>
                        item.AppliesToCapability.Equals(
                            capability,
                            StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(
                        item => item.Key,
                        item => item.Value,
                        StringComparer.OrdinalIgnoreCase);
                string layer = FirstNonEmpty(
                    step.Effect?.Target.Layer,
                    step.Effect?.Target.Dataset,
                    ParameterSemanticResolver.FirstValue(
                        step.ToolCall.Arguments,
                        descriptor?.Parameters,
                        ParameterSemanticKind.InputDataset));
                string field = FirstNonEmpty(
                    step.Effect?.Target.Field,
                    ParameterSemanticResolver.FirstValue(
                        step.ToolCall.Arguments,
                        descriptor?.Parameters,
                        ParameterSemanticKind.TargetField),
                    ParameterSemanticResolver.FirstValue(
                        step.ToolCall.Arguments,
                        descriptor?.Parameters,
                        ParameterSemanticKind.Field));
                string unit = scoped.GetValueOrDefault("unit")
                    ?? NormalizeUnit(FirstArgument(
                        step.ToolCall.Arguments,
                        "unit", "units", "area_unit", "length_unit"));
                string method = scoped.GetValueOrDefault("measurement_method")
                    ?? NormalizeMethod(FirstArgument(
                        step.ToolCall.Arguments,
                        "measurement_method", "method", "area_type", "length_type"));
                int decimalPlaces = int.TryParse(
                    scoped.GetValueOrDefault("decimal_places")
                        ?? FirstArgument(
                            step.ToolCall.Arguments,
                            "decimal_places", "digit", "digits", "precision", "scale"),
                    out int digits)
                    ? digits
                    : -1;
                string rounding = scoped.GetValueOrDefault("rounding_mode")
                    ?? "TO_EVEN";
                LayerInfo? layerInfo = domain.Layers.FirstOrDefault(item =>
                    Matches(item.Name, layer)
                    || Matches(item.DatasetId, layer));

                requirements.Add(new ParameterScopedProofRequirement
                {
                    ProofObligationId = proof.Id,
                    CapabilityId = capability,
                    MeasurementKind = capability == GisCapabilityIds.MeasureArea
                        ? MeasurementProofKind.Area
                        : MeasurementProofKind.Length,
                    Layer = layer,
                    DatasetId = layerInfo?.DatasetId ?? "",
                    Field = field,
                    Unit = unit,
                    MeasurementMethod = method,
                    DecimalPlaces = decimalPlaces,
                    RoundingMode = rounding,
                    SampleSize = 64
                });
            }
            return requirements;
        }

        private static string FirstArgument(
            IReadOnlyDictionary<string, object?> arguments,
            params string[] names)
        {
            foreach (string name in names)
            {
                var pair = arguments.FirstOrDefault(item => item.Key.Equals(
                    name,
                    StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(pair.Key)
                    && !string.IsNullOrWhiteSpace(pair.Value?.ToString()))
                    return pair.Value!.ToString()!;
            }
            return "";
        }

        private static string NormalizeUnit(string value)
        {
            string source = value.Trim().ToUpperInvariant();
            if (source is "米" or "METER" or "METERS" or "METRE" or "METRES")
                return "METERS";
            if (source is "千米" or "公里" or "KILOMETER" or "KILOMETERS"
                or "KILOMETRE" or "KILOMETRES")
                return "KILOMETERS";
            if (source is "公顷" or "HECTARE" or "HECTARES" or "HA")
                return "HECTARES";
            if (source is "平方米" or "SQUARE_METERS" or "SQUARE_METRES")
                return "SQUARE_METERS";
            if (source is "平方千米" or "平方公里" or "SQUARE_KILOMETERS"
                or "SQUARE_KILOMETRES")
                return "SQUARE_KILOMETERS";
            if (source is "亩" or "MU")
                return "MU";
            if (source is "英里" or "MILE" or "MILES")
                return "MILES";
            return source;
        }

        private static string NormalizeMethod(string value)
        {
            string source = value.Trim().ToUpperInvariant();
            if (source.Contains("椭球", StringComparison.Ordinal)
                || source.Contains("测地", StringComparison.Ordinal)
                || source.Contains("地线", StringComparison.Ordinal)
                || source.Contains("GEODESIC", StringComparison.Ordinal))
                return "GEODESIC";
            if (source.Contains("投影", StringComparison.Ordinal)
                || source.Contains("平面", StringComparison.Ordinal)
                || source.Contains("PLANAR", StringComparison.Ordinal)
                || source.Contains("PROJECTED", StringComparison.Ordinal))
                return "PLANAR";
            return source;
        }

        private static string FirstNonEmpty(params string?[] values) =>
            values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                ?.Trim() ?? "";

        private static bool Matches(string? left, string? right) =>
            !string.IsNullOrWhiteSpace(left)
            && !string.IsNullOrWhiteSpace(right)
            && (left.Equals(right, StringComparison.OrdinalIgnoreCase)
                || left.Replace('/', '\\').EndsWith(
                    "\\" + right.Replace('/', '\\'),
                    StringComparison.OrdinalIgnoreCase));
    }
}
