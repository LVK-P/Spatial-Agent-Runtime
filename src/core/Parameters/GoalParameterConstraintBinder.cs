using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Core
{
    /// <summary>
    /// Maps stable goal semantics to physical tool parameters. The goal
    /// compiler never knows ArcGIS parameter names; descriptors remain the
    /// authority for the concrete schema and accepted enum values.
    /// </summary>
    public static class GoalParameterConstraintBinder
    {
        private static readonly IReadOnlyDictionary<string, string[]> Aliases =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["unit"] = new[]
                {
                    "unit", "units", "measurement_unit", "area_unit",
                    "length_unit"
                },
                ["decimal_places"] = new[]
                {
                    "decimal_places", "digit", "digits", "precision",
                    "scale", "round_digits"
                },
                ["measurement_method"] = new[]
                {
                    "measurement_method", "method", "area_type", "length_type"
                }
            };

        public static IReadOnlyList<string> Apply(
            ToolCall call,
            IToolDescriptor? descriptor,
            IReadOnlyCollection<string> providedCapabilities,
            IEnumerable<GoalParameterConstraint> constraints)
        {
            var applied = new List<string>();
            foreach (GoalParameterConstraint constraint in constraints)
            {
                if (!string.IsNullOrWhiteSpace(constraint.AppliesToCapability)
                    && !providedCapabilities.Contains(
                        constraint.AppliesToCapability,
                        StringComparer.OrdinalIgnoreCase))
                    continue;

                string? parameter = ResolveParameter(
                    constraint.Key,
                    descriptor);
                if (string.IsNullOrWhiteSpace(parameter))
                    continue;
                ParameterProperty? property = descriptor?.Parameters
                    .FirstOrDefault(item => item.Key.Equals(
                        parameter,
                        StringComparison.OrdinalIgnoreCase)).Value;
                object value = ResolvePhysicalValue(
                    constraint,
                    property);
                call.Arguments[parameter] = value;
                applied.Add(
                    $"{constraint.AppliesToCapability}.{constraint.Key}"
                    + $"->{parameter}={value}");
            }
            return applied;
        }

        private static string? ResolveParameter(
            string semanticKey,
            IToolDescriptor? descriptor)
        {
            if (descriptor == null)
                return null;
            IEnumerable<string> aliases = Aliases.TryGetValue(
                semanticKey,
                out string[]? configured)
                ? configured
                : new[] { semanticKey };
            return aliases
                .Select(alias => descriptor.Parameters.Keys.FirstOrDefault(key =>
                    key.Equals(alias, StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault(key => !string.IsNullOrWhiteSpace(key));
        }

        private static object ResolvePhysicalValue(
            GoalParameterConstraint constraint,
            ParameterProperty? property)
        {
            if (constraint.Key.Equals(
                    "decimal_places",
                    StringComparison.OrdinalIgnoreCase)
                && int.TryParse(constraint.Value, out int number))
                return number;

            if (property?.Enum == null || property.Enum.Count == 0)
                return constraint.Value;
            string[] candidates = constraint.Value.ToUpperInvariant() switch
            {
                "METERS" => new[] { "米", "meter", "metre" },
                "KILOMETERS" => new[] { "千米", "公里", "kilometer", "kilometre" },
                "HECTARES" => new[] { "公顷", "hectare", "ha" },
                "SQUARE_METERS" => new[] { "平方米", "square meter", "square metre" },
                "SQUARE_KILOMETERS" => new[] { "平方千米", "平方公里", "square kilometer", "square kilometre" },
                "MU" => new[] { "亩", "mu" },
                "MILES" => new[] { "英里", "mile" },
                "GEODESIC" => new[] { "椭球", "测地线", "地线", "geodesic" },
                "PLANAR" => new[] { "投影", "平面", "planar", "projected" },
                _ => new[] { constraint.Value }
            };
            return property.Enum.FirstOrDefault(option => candidates.Any(
                       candidate => option.Equals(
                           candidate,
                           StringComparison.OrdinalIgnoreCase)
                           || option.Contains(
                               candidate,
                               StringComparison.OrdinalIgnoreCase)))
                ?? constraint.Value;
        }
    }
}
