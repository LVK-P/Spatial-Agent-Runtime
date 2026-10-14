using System.Collections;
using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Core
{
    /// <summary>
    /// Resolves parameter meaning from descriptor metadata first and stable
    /// ArcGIS naming/datatype conventions second. Runtime safety, approval,
    /// verification and UI projections should consume this single semantic
    /// classification instead of maintaining tool-specific parameter lists.
    /// </summary>
    /// <remarks>中文：首先解析描述符元数据中的参数含义，其次才确定稳定的ArcGIS命名/数据类型惯例。运行时安全、审批、验证和界面投影应使用这一单一语义分类，而非维护工具特定的参数列表。</remarks>
    public enum ParameterSemanticKind
    {
        Unknown,
        InputDataset,
        OutputDataset,
        Field,
        SourceField,
        TargetField,
        Filter,
        SpatialReference,
        Value
    }

    public static class ParameterSemanticResolver
    {
        public static ParameterSemanticKind Classify(
            string parameterName,
            ParameterProperty? property = null)
        {
            string role = property?.SemanticRole?.Trim().ToLowerInvariant() ?? "";
            if (role.Length > 0)
            {
                if (role.Contains("spatial_reference") || role.Contains("coordinate_system"))
                    return ParameterSemanticKind.SpatialReference;
                if (role.Contains("where") || role.Contains("filter") || role.Contains("sql"))
                    return ParameterSemanticKind.Filter;
                if (role.Contains("field")
                    && (role.Contains("source") || role.Contains("input")
                        || role.Contains("from") || role.Contains("lookup")))
                    return ParameterSemanticKind.SourceField;
                if (role.Contains("field")
                    && (role.Contains("target") || role.Contains("output")
                        || role.Contains("destination") || role.Contains("write")))
                    return ParameterSemanticKind.TargetField;
                if (role.Contains("field"))
                    return ParameterSemanticKind.Field;
                if (role.Contains("output") &&
                    (role.Contains("dataset") || role.Contains("layer") ||
                     role.Contains("table") || role.Contains("feature") ||
                     role.Contains("raster")))
                    return ParameterSemanticKind.OutputDataset;
                if ((role.Contains("input") || role.Contains("source") || role.Contains("target") ||
                     role.Contains("reference")) &&
                    (role.Contains("dataset") || role.Contains("layer") ||
                     role.Contains("table") || role.Contains("feature") ||
                     role.Contains("raster")))
                    return ParameterSemanticKind.InputDataset;
            }

            if (property?.IsSpatialRef == true)
                return ParameterSemanticKind.SpatialReference;
            if (property?.IsWhereClause == true)
                return ParameterSemanticKind.Filter;

            string lower = parameterName.Trim().ToLowerInvariant();
            if (lower.Contains("where") || lower.Contains("sql") || lower.Contains("filter"))
                return ParameterSemanticKind.Filter;
            if (LooksLikeSourceField(lower))
                return ParameterSemanticKind.SourceField;
            if (LooksLikeTargetField(lower))
                return ParameterSemanticKind.TargetField;
            if (property?.IsField == true ||
                property?.DataType?.Contains("field", StringComparison.OrdinalIgnoreCase) == true)
                return ParameterSemanticKind.Field;
            if (lower.Contains("field")
                && !ParameterNameClassifier.IsOutputParameter(lower))
                return ParameterSemanticKind.Field;
            if (ParameterNameClassifier.IsOutputParameter(lower))
                return ParameterSemanticKind.OutputDataset;
            if (ParameterNameClassifier.LooksLikeInputDataSource(lower))
                return ParameterSemanticKind.InputDataset;
            return ParameterSemanticKind.Unknown;
        }

        public static string FirstValue(
            IReadOnlyDictionary<string, object?> args,
            IReadOnlyDictionary<string, ParameterProperty>? properties,
            ParameterSemanticKind kind)
        {
            foreach (var argument in args)
            {
                ParameterProperty? property = null;
                properties?.TryGetValue(argument.Key, out property);
                var classified = Classify(argument.Key, property);
                if (classified != kind
                    && !(kind == ParameterSemanticKind.Field
                        && classified is ParameterSemanticKind.SourceField
                            or ParameterSemanticKind.TargetField))
                    continue;

                string value = FormatValue(argument.Value);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return "";
        }

        private static bool LooksLikeSourceField(string name) =>
            name.Contains("source_field", StringComparison.Ordinal)
            || name.Contains("input_field", StringComparison.Ordinal)
            || name.Contains("from_field", StringComparison.Ordinal)
            || name.Contains("lookup_field", StringComparison.Ordinal)
            || name.StartsWith("source_", StringComparison.Ordinal)
                && name.Contains("field", StringComparison.Ordinal);

        private static bool LooksLikeTargetField(string name) =>
            name.Contains("target_field", StringComparison.Ordinal)
            || name.Contains("output_field", StringComparison.Ordinal)
            || name.Contains("destination_field", StringComparison.Ordinal)
            || name.Contains("field_name", StringComparison.Ordinal)
            || name.Contains("calc_field", StringComparison.Ordinal)
            || name.Equals("field", StringComparison.Ordinal);

        public static string FormatValue(object? value)
        {
            if (value == null)
                return "";
            if (value is string text)
                return text.Trim();
            if (value is IEnumerable sequence)
            {
                var values = new List<string>();
                foreach (object? item in sequence)
                {
                    string formatted = item?.ToString()?.Trim() ?? "";
                    if (!string.IsNullOrWhiteSpace(formatted))
                        values.Add(formatted);
                }
                return string.Join(", ", values);
            }
            return value.ToString()?.Trim() ?? "";
        }
    }
}
