using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

// ────────────────────────────────────────────────────────────────
// TopologyRulesData.cs — Spatial planning topology validation rules.
//
// BUILD-TIME ASSET: consumed by tools/extract_domain_data.py as generation input.
// No runtime callers. Keep for domain reference and future topology probe integration.
//
// Source: National Spatial Planning Database Specification
//         (国土空间规划数据库规范)
//
// Three rule categories:
//   Polygon self-checks — no overlap, no gaps, must be covered
//   Polyline self-checks — no overlap, no intersection, no dangles
//   Cross-layer rules — three-lines mutual exclusion, containment,
//                         boundary consistency, planning vs. current
//
// GP tools used: Intersect, Clip, SymDiff, Erase, CheckGeometry
// ────────────────────────────────────────────────────────────────
// 中文：TopologyRulesData.cs — 空间规划拓扑验证规则。构建时间资产：作为生成输入被工具/工具/extract_domain_data.py消耗。没有运行时调用者。保留用于域引用和未来的拓扑探针集成。来源：国家空间规划数据库规范（国土空间规划数据库规范） 三类规则：多边形自检——无重叠、无空隙、必须覆盖 多边形自检——无重叠、无交叉、无悬挂 跨层规则——三线互斥、包含、边界一致性、规划与当前GP工具的比较：交集、剪辑、SymDiff、擦除、检查几何
namespace SpatialAgent.Knowledge.Data
{
    public static class TopologyRulesData
    {
        private static readonly Lazy<List<TopologyRule>> _polySelfLazy =
            new(() => LoadJsonRules("polygon_self") ?? DefaultPolygonSelfRules!);
        private static readonly Lazy<List<TopologyRule>> _lineSelfLazy =
            new(() => LoadJsonRules("polyline_self") ?? DefaultPolylineSelfRules!);
        private static readonly Lazy<List<TopologyRule>> _crossLazy =
            new(() => LoadJsonRules("cross_layer") ?? DefaultCrossLayerRules!);

        public static List<TopologyRule> PolygonSelfRules => _polySelfLazy.Value!;
        public static List<TopologyRule> PolylineSelfRules => _lineSelfLazy.Value!;
        public static List<TopologyRule> CrossLayerRules => _crossLazy.Value!;

        private static List<TopologyRule>? LoadJsonRules(string key)
        {
            try
            {
                string? path = SpatialAgent.Runtime.Contracts.ResourcePaths.Find(Path.Combine("data", "topology_rules.json"))
                    ?? SpatialAgent.Runtime.Contracts.ResourcePaths.Find("topology_rules.json");
                if (path == null) return null;
                var json = JObject.Parse(File.ReadAllText(path));
                JToken? token = json[key];
                if (token == null) return null;
                return token.ToObject<List<TopologyRule>>();
            }
            catch { return null; }
        }

        // ── Default inline data ──
        // 中文：默认的内联数据

        public enum RuleType
        {
            MustNotOverlap,          // 面/线：不得重叠。 / Polygon/line: must not overlap.
            MustNotHaveGaps,         // 面：不得有空隙。 / Polygon: must not have gaps.
            MustNotIntersect,        // 线：不得相交。 / Line: must not intersect.
            MustNotHaveDangles,      // 线：不得有悬挂点。 / Line: must not have dangles.
            MustNotHavePseudoNodes,  // 线：不得有伪节点。 / Line: must not have pseudo-nodes.
            MustBeCoveredBy,         // 面 A 必须被面 B 覆盖。 / Polygon A must be covered by polygon B.
            MustNotOverlapWith,      // 面 A 不得与面 B 重叠。 / Polygon A must not overlap polygon B.
            MustBeInside,            // 面 A 必须位于面 B 内。 / Polygon A must be inside polygon B.
            BoundaryMustBeCoveredBy  // 线边界必须被面边界覆盖。 / Line boundary must be covered by polygon boundary.
        }

        // ───────────────────────────────────────────────────────────────────
        // Topology rule definitions
        // ───────────────────────────────────────────────────────────────────
        // 中文：拓扑规则定义
        public class TopologyRule
        {
            public string Id { get; set; } = "";
            public string Name { get; set; } = "";
            public RuleType Type { get; set; }
            public string SourceDescription { get; set; } = "";     // 源图层描述。 / Source-layer description.
            public string? TargetDescription { get; set; }          // 目标图层描述（仅跨图层规则）。 / Target-layer description (cross-layer rules only).
            public string ErrorDescription { get; set; } = "";      // 违规时的错误描述。 / Error description on violation.
            public string GpCheck { get; set; } = "";               // GP 检查工具。 / GP check tool.
            public string GpFix { get; set; } = "";                 // GP 修复工具。 / GP fix tool.
            public string Severity { get; set; } = "error";         // 错误/警告。 / Error/warning.
        }

        // ───────────────────────────────────────────────────────────────────
        // Polygon self-check rules
        // ───────────────────────────────────────────────────────────────────
        // 中文：多边形自检规则
        private static readonly List<TopologyRule> DefaultPolygonSelfRules = new()
        {
            new TopologyRule
            {
                Id = "POLY-01", Name = "图斑不能重叠",
                Type = RuleType.MustNotOverlap,
                SourceDescription = "面图层内的图斑之间不能有重叠区域",
                ErrorDescription = "发现图斑重叠，请检查重叠区域",
                GpCheck = "analysis.Intersect（图层自相交）",
                GpFix = "management.Eliminate + editor.Merge",
                Severity = "error"
            },
            new TopologyRule
            {
                Id = "POLY-02", Name = "图斑不能有空隙",
                Type = RuleType.MustNotHaveGaps,
                SourceDescription = "面图层内的图斑之间不能有未覆盖的空隙",
                ErrorDescription = "发现空隙，请检查图斑间未覆盖区域",
                GpCheck = "management.CreateFeatureclass + analysis.Erase（全域范围 减去 图斑合并）",
                GpFix = "editor.CreateFeature + editor.Merge",
                Severity = "warning"
            }
        };

        // ───────────────────────────────────────────────────────────────────
        // Polyline self-check rules
        // ───────────────────────────────────────────────────────────────────
        // 中文：多线自检规则
        private static readonly List<TopologyRule> DefaultPolylineSelfRules = new()
        {
            new TopologyRule
            {
                Id = "LINE-01", Name = "线不能重叠",
                Type = RuleType.MustNotOverlap,
                SourceDescription = "线图层内的线段之间不能有重叠段",
                ErrorDescription = "发现线段重叠",
                GpCheck = "analysis.Intersect（线图层自相交）",
                GpFix = "editor.Merge + editor.Delete",
                Severity = "error"
            },
            new TopologyRule
            {
                Id = "LINE-02", Name = "线不能相交",
                Type = RuleType.MustNotIntersect,
                SourceDescription = "线要素之间不能交叉（同一平面内）",
                ErrorDescription = "发现线段相交",
                GpCheck = "analysis.Intersect（线图层自相交）",
                GpFix = "editor.Split + editor.Planarize",
                Severity = "error"
            },
            new TopologyRule
            {
                Id = "LINE-03", Name = "不能有悬挂点",
                Type = RuleType.MustNotHaveDangles,
                SourceDescription = "线端点必须连接到另一条线（道路网络必须连通）",
                ErrorDescription = "发现悬挂点",
                GpCheck = "management.AddRuleToTopology（MustNotHaveDangles）",
                GpFix = "editor.Extend + editor.Snap",
                Severity = "warning"
            },
            new TopologyRule
            {
                Id = "LINE-04", Name = "不能有伪结点",
                Type = RuleType.MustNotHavePseudoNodes,
                SourceDescription = "连续的线不应在中间无故断开",
                ErrorDescription = "发现伪结点",
                GpCheck = "management.AddRuleToTopology（MustNotHavePseudoNodes）",
                GpFix = "editor.Merge",
                Severity = "warning"
            }
        };

        // ───────────────────────────────────────────────────────────────────
        // Cross-layer rules (core spatial planning constraints)
        // ───────────────────────────────────────────────────────────────────
        // 中文：跨层规则（核心空间规划约束）
        private static readonly List<TopologyRule> DefaultCrossLayerRules = new()
        {
            // -- Three-lines mutual exclusion --
            // 中文：——三线互斥——
            new TopologyRule
            {
                Id = "CROSS-01", Name = "生态红线与永久基本农田不能重叠",
                Type = RuleType.MustNotOverlapWith,
                SourceDescription = "生态保护红线图层",
                TargetDescription = "永久基本农田图层",
                ErrorDescription = "生态红线与基本农田存在重叠区域，请检查",
                GpCheck = "analysis.Intersect(生态红线, 基本农田) → 输出应为空",
                GpFix = "人工判定：调整生态红线或补划基本农田",
                Severity = "error"
            },
            new TopologyRule
            {
                Id = "CROSS-02", Name = "生态红线与城镇开发边界不能重叠",
                Type = RuleType.MustNotOverlapWith,
                SourceDescription = "生态保护红线图层",
                TargetDescription = "城镇开发边界图层",
                ErrorDescription = "生态红线与开发边界存在重叠区域，请检查",
                GpCheck = "analysis.Intersect(生态红线, 开发边界) → 输出应为空",
                GpFix = "人工判定",
                Severity = "error"
            },
            new TopologyRule
            {
                Id = "CROSS-03", Name = "永久基本农田与城镇开发边界不能重叠",
                Type = RuleType.MustNotOverlapWith,
                SourceDescription = "永久基本农田图层",
                TargetDescription = "城镇开发边界图层",
                ErrorDescription = "基本农田与开发边界存在重叠区域，请检查",
                GpCheck = "analysis.Intersect(基本农田, 开发边界) → 输出应为空",
                GpFix = "人工判定",
                Severity = "error"
            },

            // -- Containment constraints --
            // 中文：—— 约束条件 ——
            new TopologyRule
            {
                Id = "CROSS-04", Name = "建设用地必须在城镇开发边界内",
                Type = RuleType.MustBeCoveredBy,
                SourceDescription = "规划建设用地图斑",
                TargetDescription = "城镇开发边界",
                ErrorDescription = "建设用地位于开发边界外，请检查越界区域",
                GpCheck = "analysis.SymDiff(建设用地, Clip(建设用地,开发边界)) → 越界部分",
                GpFix = "调整建设用地范围 或 调整开发边界",
                Severity = "error"
            },
            new TopologyRule
            {
                Id = "CROSS-05", Name = "道路红线不能压盖其他用地图斑",
                Type = RuleType.MustNotOverlapWith,
                SourceDescription = "道路红线图层",
                TargetDescription = "用地图层（除交通运输用地外）",
                ErrorDescription = "道路红线压盖了其他用地图斑，请检查",
                GpCheck = "analysis.Intersect(道路红线, 用地图层) WHERE 用地类型 NOT IN 交通运输",
                GpFix = "调整道路线位 或 调整用地图斑边界",
                Severity = "warning"
            },

            // -- Boundary consistency --
            // 中文：——边界一致性——
            new TopologyRule
            {
                Id = "CROSS-06", Name = "行政区边界不能裁切图斑",
                Type = RuleType.BoundaryMustBeCoveredBy,
                SourceDescription = "行政区界线",
                TargetDescription = "所有面图层",
                ErrorDescription = "行政区边界处图斑被不规则裁切",
                GpCheck = "management.AddRuleToTopology",
                GpFix = "按行政区界线重新裁切图斑",
                Severity = "warning"
            },

            // -- Current vs. planning --
            // 中文：——当前与规划——
            new TopologyRule
            {
                Id = "CROSS-07", Name = "规划用地与现状用地变更必须有迹可循",
                Type = RuleType.MustNotOverlapWith,
                SourceDescription = "规划用地图层",
                TargetDescription = "现状用地图层（仅在未变更区域检查）",
                ErrorDescription = "规划与现状重叠但未标记为变更，请检查",
                GpCheck = "analysis.Intersect(规划用地, 现状用地) → 筛选未标记变更",
                GpFix = "标记变更区域 或 调整规划",
                Severity = "warning"
            },

            // -- Central urban area rules --
            // 中文：—— 中心城市区规则 ——
            new TopologyRule
            {
                Id = "CROSS-08", Name = "中心城区城市绿线不能与建设用地重叠",
                Type = RuleType.MustNotOverlapWith,
                SourceDescription = "中心城区城市绿线图层",
                TargetDescription = "中心城区规划用地（除绿地外）",
                ErrorDescription = "城市绿线与建设用地重叠，请检查",
                GpCheck = "analysis.Intersect(城市绿线, 规划用地 WHERE 类型!=绿地)",
                GpFix = "调整绿线边界",
                Severity = "error"
            },
            new TopologyRule
            {
                Id = "CROSS-09", Name = "中心城区城市蓝线内的用地必须为水域",
                Type = RuleType.MustBeCoveredBy,
                SourceDescription = "城市蓝线内用地图斑",
                TargetDescription = "城市蓝线图层",
                ErrorDescription = "蓝线内存在非水域用地",
                GpCheck = "analysis.Clip(规划用地, 城市蓝线) WHERE 用地类型!=水域",
                GpFix = "调整用地类型为水域 或 调整蓝线",
                Severity = "error"
            },
        };

        // ───────────────────────────────────────────────────────────────────
        // Get rules by geometry type
        // ───────────────────────────────────────────────────────────────────
        // 中文：按几何类型获取规则
        public static List<TopologyRule> GetRulesByGeometry(string geometryType)
        {
            var rules = new List<TopologyRule>();
            switch (geometryType.ToLower())
            {
                case "polygon": rules.AddRange(PolygonSelfRules); break;
                case "polyline": rules.AddRange(PolylineSelfRules); break;
            }
            rules.AddRange(CrossLayerRules);
            return rules;
        }

        /// <summary>获取所有自检规则</summary>
        /// <remarks>English：Get all self-check rules</remarks>
        public static List<TopologyRule> GetAllSelfRules()
        {
            var rules = new List<TopologyRule>();
            rules.AddRange(PolygonSelfRules);
            rules.AddRange(PolylineSelfRules);
            return rules;
        }

        /// <summary>获取所有图层间规则</summary>
        /// <remarks>English：Get all rules between layers</remarks>
        public static List<TopologyRule> GetAllCrossLayerRules() => CrossLayerRules;

        /// <summary>三线互检规则（最常用的3条）</summary>
        /// <remarks>English：Three-line mutual inspection rules (the three most commonly used)</remarks>
        public static List<TopologyRule> GetThreeLinesRules()
            => CrossLayerRules.FindAll(r => r.Id is "CROSS-01" or "CROSS-02" or "CROSS-03");

        public static int PolygonSelfCount => PolygonSelfRules.Count;
        public static int PolylineSelfCount => PolylineSelfRules.Count;
        public static int CrossLayerCount => CrossLayerRules.Count;
        public static int TotalCount => PolygonSelfCount + PolylineSelfCount + CrossLayerCount;
    }
}
