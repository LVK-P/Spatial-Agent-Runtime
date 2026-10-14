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
    /// <remarks>中文：为单个轮次收集的空间参考上下文，通过 ToPromptInjection() 注入系统提示词。</remarks>
    public class SpatialRefContext
    {
        /// <summary>Active map WKID. 0 = unknown.</summary>
        /// <remarks>中文：活动地图的 WKID；0 表示未知。</remarks>
        public int MapWkid { get; set; }

        /// <summary>Active map CRS name (e.g. "CGCS2000_3_Degree_GK_Zone_39").</summary>
        /// <remarks>中文：活动地图的 CRS 名称（例如 "CGCS2000_3_Degree_GK_Zone_39"）。</remarks>
        public string MapSpatialRefName { get; set; } = "";

        /// <summary>Human-readable label for system prompt.</summary>
        /// <remarks>中文：用于系统提示词的易读标签。</remarks>
        public string MapSpatialRefLabel { get; set; } = "";

        /// <summary>Per-layer spatial references.</summary>
        /// <remarks>中文：各图层的空间参考。</remarks>
        public List<LayerSpatialRef> Layers { get; set; } = new();

        /// <summary>True when at least two layers have different WKIDs.</summary>
        /// <remarks>中文：至少两个图层的 WKID 不同时为 true。</remarks>
        public bool HasMixedCRS => Layers.Select(l => l.Wkid).Where(w => w != 0).Distinct().Count() > 1;

        /// <summary>Default output WKID (derived from map, or first layer).</summary>
        /// <remarks>中文：默认输出 WKID（取自地图或第一个图层）。</remarks>
        public int DefaultOutputWkid { get; set; }

        /// <summary>Detected cross-datum layer pairs that need transformations.</summary>
        /// <remarks>中文：检测到的、需要地理变换的跨基准面图层对。</remarks>
        public List<DatumShiftRecommendation> DatumShifts { get; set; } = new();

        /// <summary>True when cross-datum operations are detected without recommended transforms.</summary>
        /// <remarks>中文：检测到跨基准面操作但没有推荐变换时为 true。</remarks>
        public bool HasUnresolvedDatumShift =>
            DatumShifts.Any(d => d.IsRequired && string.IsNullOrEmpty(d.RecommendedTransformation));

        /// <summary>
        /// Generate a concise prompt injection for the system prompt.
        /// Chinese natural language — LLM reads this directly.
        /// </summary>
        /// <remarks>中文：为系统提示词生成简洁的中文自然语言注入内容，供 LLM 直接读取。</remarks>
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
    /// <remarks>中文：单个图层的空间参考信息。</remarks>
    public class LayerSpatialRef
    {
        public string LayerName { get; set; } = "";
        public int Wkid { get; set; }
        public string SpatialRefLabel { get; set; } = "";
        /// <summary>True if this layer's CRS matches the active map's CRS.</summary>
        /// <remarks>中文：该图层的 CRS 与活动地图 CRS 一致时为 true。</remarks>
        public bool MatchesMap { get; set; }
    }

    /// <summary>
    /// Cross-datum shift recommendation between two layers or a layer and the map.
    /// </summary>
    /// <remarks>中文：两个图层之间，或图层与地图之间的跨基准面变换建议。</remarks>
    public class DatumShiftRecommendation
    {
        public string FromLayer { get; set; } = "";
        public string ToLayer { get; set; } = "";
        public int FromWkid { get; set; }
        public int ToWkid { get; set; }
        /// <summary>True if datums are different — transformation is required for accurate results.</summary>
        /// <remarks>中文：基准面不同时为 true，此时需要执行变换才能获得准确结果。</remarks>
        public bool IsRequired { get; set; }
        /// <summary>Recommended geographic transformation name, e.g. "CGCS2000_To_WGS84_1".</summary>
        /// <remarks>中文：推荐的地理变换名称，例如“CGCS2000_To_WGS84_1”。</remarks>
        public string? RecommendedTransformation { get; set; }
    }
}
