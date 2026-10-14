namespace SpatialAgent.Core
{
    /// <summary>
    /// Centralized heuristics for classifying GP tool parameter names.
    /// All modules (Scheduler, PostConditionValidationRule, SchemaGPToolAdapter)
    /// route through this to avoid duplicated keyword matching logic.
    /// </summary>
    /// <remarks>中文：用于分类GP工具参数名称的集中启发式方法。所有模块（Scheduler、PostConditionValidationRule、SchemaGPToolAdapter）都会通过该流程路由，以避免重复的关键词匹配逻辑。</remarks>
    public static class ParameterNameClassifier
    {
        public static bool IsOutputParameter(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            string lower = name.Trim().ToLowerInvariant();
            if (IsOutputConfigurationParameter(lower))
                return false;

            return lower.StartsWith("out_")
                || lower.StartsWith("output")
                || lower.StartsWith("derived")
                || lower == "output";
        }

        private static bool IsOutputConfigurationParameter(string lower)
        {
            // ArcGIS uses the "out_*" prefix for both datasets and settings.
            // Treating settings such as out_coor_system=4490 as paths makes a
            // valid multi-layer batch look like it has duplicate outputs.
            // 中文：ArcGIS 在数据集和设置中都使用“out_*”前缀。将诸如 out_coor_system=4490 这样的设置视为路径，使有效的多层批次看起来像是有重复的输出。
            string[] exact =
            {
                "out_coor_system", "output_coordinate_system",
                "out_coordinate_system", "out_spatial_reference",
                "output_spatial_reference", "out_cell_size", "output_cell_size",
                "out_extent", "output_extent", "output_type", "out_type",
                "out_unit", "output_unit", "out_method", "output_method",
                "out_resolution", "output_resolution", "out_count",
                "output_count", "out_fields", "output_fields"
            };
            return exact.Contains(lower, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Tight match — used by backup resolver and verify_no_overlap.
        /// Returns true when the parameter name looks like a GIS data source
        /// (feature class, layer, table, dataset, etc.).
        /// </summary>
        /// <remarks>中文：紧密匹配——由备份解析器和verify_no_overlap使用。当参数名称看起来像GIS数据源（特征类、图层、表、数据集等）时，返回为真。</remarks>
        public static bool LooksLikeInputDataSource(string? key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return false;

            string lower = key.Trim().ToLowerInvariant();

            // Never consider output parameters as data sources.
            // 中文：永远不要把输出参数当作数据源。
            if (IsOutputParameter(lower))
                return false;

            // Field/expression/configuration parameters are NOT data sources.
            // 中文：字段/表达式/配置参数不是数据源。
            if (HasBadQualifier(lower))
                return false;

            // Must contain at least one geometry/data qualifier.
            // 中文：必须包含至少一个几何/数据限定词。
            return HasDataSourceQualifier(lower);
        }

        /// <summary>
        /// Broader match — used for input parameter inference when manifest
        /// metadata is absent (SchemaGPToolAdapter fallback).
        /// </summary>
        /// <remarks>中文：更广泛的匹配——在缺乏manifest元数据时用于输入参数推断（SchemaGPToolAdapter的备用）。</remarks>
        public static bool IsLikelyInputParameter(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            string lower = name.Trim().ToLowerInvariant();

            // Must not look like an output parameter.
            // 中文：不能看起来像输出参数。
            if (IsOutputParameter(lower))
                return false;

            return lower.StartsWith("in_")
                || lower.StartsWith("input")
                || lower.StartsWith("source")
                || lower.Contains("layer")
                || lower.Contains("table")
                || lower.Contains("dataset")
                || lower.Contains("features")
                || lower.Contains("raster")
                || lower.Contains("workspace");
        }

        private static bool HasBadQualifier(string lower)
        {
            string[] bad =
            {
                "field", "expression", "where", "sql", "type", "method", "option",
                "distance", "unit", "statistic", "count", "value"
            };
            foreach (var word in bad)
                if (lower.Contains(word))
                    return true;
            return false;
        }

        private static bool HasDataSourceQualifier(string lower)
        {
            string[] good =
            {
                "feature", "features", "layer", "table", "dataset", "class",
                "source", "target", "input", "clip", "erase", "mask", "boundary",
                "overlay", "join", "compare", "against", "reference", "barrier",
                "in_", "raster", "workspace"
            };
            foreach (var word in good)
                if (lower.Contains(word))
                    return true;
            return false;
        }
    }
}
