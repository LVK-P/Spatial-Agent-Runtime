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
    /// <remarks>中文：共享纯工具用于GIS几何类别归一化和谱系偏好检测。零主机/I/O依赖——根据ADR 0056，核心层安全。此前在 DefaultGoalScopeResolver（第 426 行）、DefaultPlanCoverageValidator（第 311 行）和 DefaultScopeExpander（第 269 行）之间重复使用。合并日期为2026-07-27，处于第二阶段消毒期间。</remarks>
    public static class GisCategoryNormalizer
    {
        /// <summary>
        /// Unified lineage preference language terms. Merged from
        /// DefaultGoalContractCompiler.LineagePreferenceTerms and
        /// DefaultScopeExpander.HasLineagePreferenceLanguage.
        /// </summary>
        /// <remarks>中文：统一的血统偏好语言术语。合并自 DefaultGoalContractCompiler.LineagePreferenceTerms 和 DefaultScopeExpander.HasLineagePreferenceLanguage。</remarks>
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
        /// <remarks>中文：将ArcGIS几何类型字符串归一化为稳定的类别键。接受英文数值（“Polygon”、“Polyline”等）和中文描述（“面”、“线”等）进行纵深防御。</remarks>
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
        /// <remarks>中文：检测用户的目标文本是否偏好源自血统/CRS的代表，而非所有实体成员。</remarks>
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
