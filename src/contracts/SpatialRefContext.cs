using System.Collections.Generic;
using System.Linq;
using System.Text;

// ================================================================
// SpatialRefContext.cs — 坐标系上下文 DTO
//
// ContextManager 收集地图和各图层的空间参考信息。
// 检测坐标系混用和跨基准面操作，注入 System Prompt 以
// 减少 LLM 的坐标系相关错误（GIS 领域第一坑）。
//
// 来源：跨实现架构审查后的空间参考上下文合同。
// ================================================================
namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Collected spatial reference context for a turn.
    /// Injected into System Prompt via ToPromptInjection().
    /// </summary>
    public class SpatialRefContext
    {
        /// <summary>Active map WKID. 0 = unknown.</summary>
        public int MapWkid { get; set; }

        /// <summary>Active map CRS name (e.g. "CGCS2000_3_Degree_GK_Zone_39").</summary>
        public string MapSpatialRefName { get; set; } = "";

        /// <summary>Human-readable label for system prompt.</summary>
        public string MapSpatialRefLabel { get; set; } = "";

        /// <summary>Per-layer spatial references.</summary>
        public List<LayerSpatialRef> Layers { get; set; } = new();

        /// <summary>True when at least two layers have different WKIDs.</summary>
        public bool HasMixedCRS => Layers.Select(l => l.Wkid).Where(w => w != 0).Distinct().Count() > 1;

        /// <summary>Default output WKID (derived from map, or first layer).</summary>
        public int DefaultOutputWkid { get; set; }

        /// <summary>Detected cross-datum layer pairs that need transformations.</summary>
        public List<DatumShiftRecommendation> DatumShifts { get; set; } = new();

        /// <summary>True when cross-datum operations are detected without recommended transforms.</summary>
        public bool HasUnresolvedDatumShift =>
            DatumShifts.Any(d => d.IsRequired && string.IsNullOrEmpty(d.RecommendedTransformation));

        /// <summary>
        /// Generate a concise prompt injection for the system prompt.
        /// Chinese natural language — LLM reads this directly.
        /// </summary>
        public string ToPromptInjection()
        {
            if (MapWkid == 0 && Layers.Count == 0) return "";

            var sb = new StringBuilder();
            sb.Append("【坐标系上下文】");

            if (MapWkid != 0)
                sb.Append($" 当前地图坐标系: {MapSpatialRefLabel} (WKID {MapWkid})。");

            if (HasMixedCRS)
            {
                sb.Append(" ⚠️ 以下图层坐标系不一致，跨图层操作前必须先统一坐标系：");
                foreach (var l in Layers)
                    sb.Append($"  - {l.LayerName}: {l.SpatialRefLabel} (WKID {l.Wkid})");
            }

            if (DefaultOutputWkid != 0 && DefaultOutputWkid != MapWkid)
                sb.Append($" 所有输出默认使用 WKID {DefaultOutputWkid}。");

            if (HasUnresolvedDatumShift)
            {
                sb.Append(" ⚠️ 检测到跨基准面操作，必须指定地理变换参数（geographic_transformation）：");
                foreach (var d in DatumShifts.Where(d => d.IsRequired))
                    sb.Append($"  - {d.FromLayer} (WKID {d.FromWkid}) → {d.ToLayer} (WKID {d.ToWkid})");
            }

            foreach (var d in DatumShifts.Where(d => !string.IsNullOrEmpty(d.RecommendedTransformation)))
                sb.Append($" 推荐地理变换: {d.RecommendedTransformation}");

            return sb.ToString();
        }
    }

    /// <summary>
    /// Per-layer spatial reference info.
    /// </summary>
    public class LayerSpatialRef
    {
        public string LayerName { get; set; } = "";
        public int Wkid { get; set; }
        public string SpatialRefLabel { get; set; } = "";
        /// <summary>True if this layer's CRS matches the active map's CRS.</summary>
        public bool MatchesMap { get; set; }
    }

    /// <summary>
    /// Cross-datum shift recommendation between two layers or a layer and the map.
    /// </summary>
    public class DatumShiftRecommendation
    {
        public string FromLayer { get; set; } = "";
        public string ToLayer { get; set; } = "";
        public int FromWkid { get; set; }
        public int ToWkid { get; set; }
        /// <summary>True if datums are different — transformation is required for accurate results.</summary>
        public bool IsRequired { get; set; }
        /// <summary>Recommended geographic transformation name, e.g. "CGCS2000_To_WGS84_1".</summary>
        public string? RecommendedTransformation { get; set; }
    }
}
