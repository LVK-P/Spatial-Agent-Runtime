namespace SpatialAgent.Core
{
    /// <summary>
    /// Centralized heuristics for classifying GP tool parameter names.
    /// All modules (Scheduler, PostConditionValidationRule, SchemaGPToolAdapter)
    /// route through this to avoid duplicated keyword matching logic.
    /// </summary>
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
        public static bool LooksLikeInputDataSource(string? key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return false;

            string lower = key.Trim().ToLowerInvariant();

            // Never consider output parameters as data sources.
            if (IsOutputParameter(lower))
                return false;

            // Field/expression/configuration parameters are NOT data sources.
            if (HasBadQualifier(lower))
                return false;

            // Must contain at least one geometry/data qualifier.
            return HasDataSourceQualifier(lower);
        }

        /// <summary>
        /// Broader match — used for input parameter inference when manifest
        /// metadata is absent (SchemaGPToolAdapter fallback).
        /// </summary>
        public static bool IsLikelyInputParameter(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            string lower = name.Trim().ToLowerInvariant();

            // Must not look like an output parameter.
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
