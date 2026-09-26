using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

// ────────────────────────────────────────────────────────────────
// CoreReconciliationRules.cs — Pure session state reconciliation.
//
// ADR 0056 / Phase 3：从 StateReconciler 中析出的纯规则——零文件 I/O、
// 零宿主依赖。内容：
//   1. ParseLayerNames — "· layer：type" 行解析
//   2. ExtractReferencedLayers — 从内存 JSONL 行提取图层引用
//   3. ExtractToolChain — 从内存 JSONL 行提取工具链
//   4. ComputeDiff — 纯 diff 计算（LINQ 集合运算）
//   5. ReconciliationReport + ToPromptInjection — 报告模型 + 格式化
//
// Truthfulness 规则："两边都有数据才可声称变化"——空数据 = 无信息，
// 不是"零图层"。杜绝基于解析失败构造虚假变更声明。
// ────────────────────────────────────────────────────────────────
namespace SpatialAgent.Core
{
    public static class CoreReconciliationRules
    {
        /// <summary>Parse "· layerName：GeometryType，…" style lines into layer names.</summary>
        public static IEnumerable<string> ParseLayerNames(string? mapInfo)
        {
            if (string.IsNullOrWhiteSpace(mapInfo))
                yield break;

            foreach (var layerLine in mapInfo.Split('\n'))
            {
                var trimmed = layerLine.Trim();
                if (!trimmed.StartsWith("· ")) continue;

                int colon = trimmed.IndexOf('：');
                if (colon < 0) colon = trimmed.IndexOf(':');
                if (colon > 2)
                {
                    string name = trimmed[2..colon].Trim();
                    if (!string.IsNullOrWhiteSpace(name))
                        yield return name;
                }
            }
        }

        /// <summary>
        /// Extract the layer names referenced in a past session's transcript lines.
        /// Parses context_gathered events for map layer descriptions.
        /// </summary>
        public static List<string> ExtractReferencedLayers(IEnumerable<string> transcriptLines)
        {
            var layers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var line in transcriptLines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var obj = JObject.Parse(line);
                    if (obj["type"]?.ToString() != "context_gathered") continue;

                    string? mapInfo = obj["map"]?.ToString();
                    if (string.IsNullOrWhiteSpace(mapInfo)) continue;

                    foreach (var name in ParseLayerNames(mapInfo))
                        layers.Add(name);
                }
                catch (Newtonsoft.Json.JsonException) { /* corrupt transcript line — skip */ }
            }

            return layers.ToList();
        }

        /// <summary>
        /// Extract the tool chain from a past session's transcript lines.
        /// Parses tool_end events.
        /// </summary>
        public static List<string> ExtractToolChain(IEnumerable<string> transcriptLines)
        {
            var tools = new List<string>();

            foreach (var line in transcriptLines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var obj = JObject.Parse(line);
                    if (obj["type"]?.ToString() != "tool_end") continue;

                    string? toolName = obj["tool"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(toolName))
                        tools.Add(toolName);
                }
                catch (Newtonsoft.Json.JsonException) { /* corrupt transcript line — skip */ }
            }

            return tools;
        }

        /// <summary>
        /// Pure diff computation: compare past session layers against current
        /// map layers. Returns (stillPresent, missing, newLayers, needsAttention).
        ///
        /// ★ Truthfulness rule: 空数据 ≠ 零图层。只有当两边都有提取结果时才声称变更。
        /// 解析失败 → canDiff=false → 不制造虚假声明。
        /// </summary>
        public static (List<string> StillPresent, List<string> Missing, List<string> New, bool NeedsAttention)
            ComputeDiff(
                IReadOnlyList<string> pastLayers,
                IReadOnlyCollection<string> currentLayers)
        {
            bool canDiff = pastLayers.Count > 0 && currentLayers.Count > 0;

            var stillPresent = canDiff
                ? pastLayers.Where(l => currentLayers.Contains(l)).ToList()
                : new List<string>();

            var missing = canDiff
                ? pastLayers.Where(l => !currentLayers.Contains(l, StringComparer.OrdinalIgnoreCase)).ToList()
                : new List<string>();

            var newLayers = canDiff
                ? currentLayers.Where(l => !pastLayers.Contains(l, StringComparer.OrdinalIgnoreCase)).ToList()
                : new List<string>();

            return (stillPresent, missing, newLayers, missing.Count > 0 || newLayers.Count > 0);
        }
    }

    /// <summary>
    /// Result of comparing past session state with current project reality.
    /// </summary>
    public class ReconciliationReport
    {
        public string PastThreadId { get; set; } = "";
        public string PastToolChain { get; set; } = "";
        public int PastLayerCount { get; set; }

        public List<string> LayersStillPresent { get; set; } = new();
        public List<string> LayersMissing { get; set; } = new();
        public List<string> LayersNew { get; set; } = new();

        public bool NeedsAttention { get; set; }

        /// <summary>
        /// Build a human-readable summary for injection into the system prompt.
        /// Only states facts that were actually observed.
        /// </summary>
        public string ToPromptInjection()
        {
            var parts = new List<string>
            {
                $"这是历史会话 {PastThreadId} 的继续。"
            };

            if (!string.IsNullOrWhiteSpace(PastToolChain))
                parts.Add($"上次工具链：{PastToolChain}");

            if (LayersStillPresent.Count > 0)
                parts.Add($"上次使用的图层仍在：{string.Join("、", LayersStillPresent)}");

            if (LayersMissing.Count > 0)
                parts.Add($"⚠ 上次使用的图层已删除或改名：{string.Join("、", LayersMissing)}");

            if (LayersNew.Count > 0)
                parts.Add($"新增图层（上次会话时不存在）：{string.Join("、", LayersNew)}");

            if (NeedsAttention)
                parts.Add("注意：项目状态已变更。优先用 agent_describe_layer 核实图层结构后再操作。");

            return string.Join("\n", parts);
        }
    }
}
