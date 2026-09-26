namespace SpatialAgent.Core
{
    /// <summary>
    /// Shared pure utilities for GIS geometry category normalization and
    /// lineage preference detection. Zero host / I/O dependencies — safe
    /// for Core layer per ADR 0056.
    ///
    /// Previously duplicated across DefaultGoalScopeResolver (line 426),
    /// DefaultPlanCoverageValidator (line 311), and DefaultScopeExpander
    /// (line 269). Consolidated 2026-07-27 during phase2 sanitization.
    /// </summary>
    public static class GisCategoryNormalizer
    {
        /// <summary>
        /// Unified lineage preference language terms. Merged from
        /// DefaultGoalContractCompiler.LineagePreferenceTerms and
        /// DefaultScopeExpander.HasLineagePreferenceLanguage.
        /// </summary>
        public static readonly string[] LineagePreferenceTerms =
        {
            "每条血缘", "每个血缘", "最新版本", "最深版本", "派生版本优先",
            "最新图层", "派生优先", "只要投影后", "仅派生", "投影后的",
            "投影后图层", "刚生成的", "刚才生成的", "刚投影的",
            "latest per lineage", "latest version", "most derived",
            "derived only", "projected only"
        };

        /// <summary>
        /// Normalize an ArcGIS geometry type string to a stable category key.
        /// Accepts both English values ("Polygon", "Polyline", …) and
        /// Chinese descriptions ("面", "线", …) for defense-in-depth.
        /// </summary>
        public static string NormalizeGeometry(string? geometryType)
        {
            string lower = (geometryType ?? "").Trim().ToLowerInvariant();
            if (lower.Length == 0)
                return "";

            if (lower.Contains("polygon") || lower.Contains("面"))
                return "polygon";
            if (lower.Contains("polyline") || lower.Contains("line") || lower.Contains("线"))
                return "line";
            if (lower.Contains("multipoint") || lower.Contains("point") || lower.Contains("点"))
                return "point";
            if (lower.Contains("raster") || lower.Contains("栅格"))
                return "raster";
            if (lower.Contains("table") || lower.Contains("表"))
                return "table";

            return lower;
        }

        /// <summary>
        /// Detect whether the user's goal text expresses a preference for
        /// lineage/crs-derived representatives rather than all physical members.
        /// </summary>
        public static bool HasLineagePreferenceLanguage(string? goal)
        {
            if (string.IsNullOrWhiteSpace(goal))
                return false;
            string source = $" {goal.Trim().ToLowerInvariant()} ";
            return System.Linq.Enumerable.Any(
                LineagePreferenceTerms,
                term => source.Contains(term, System.StringComparison.OrdinalIgnoreCase));
        }
    }
}
