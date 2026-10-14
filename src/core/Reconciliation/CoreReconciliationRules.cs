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
// English：CoreReconciliationRules.cs — Pure session state reconciliation. ADR 0056 / Phase 3: Pure rules extracted from StateReconciler — zero file I/O, zero host dependencies. Content: 1. ParseLayerNames — "· layer:type" line parsing 2. ExtractReferencedLayers — Extracts layer references from JSONL lines in memory 3. ExtractToolChain — Extracts the toolchain from JSONL lines in memory 4. ComputeDiff — Pure diff computation (LINQ set operations) 5. ReconciliationReport + ToPromptInjection — Report model + formatted Truthfulness rule: "Change can only be claimed when there is data on both sides" — empty data = no information, not "zero layer." False change statements based on parsing failures are strictly prohibited.
namespace SpatialAgent.Core
{
    public static class CoreReconciliationRules
    {
        /// <summary>Parse "· layerName：GeometryType，…" style lines into layer names.</summary>
        /// <remarks>中文：解析 “·layerName：GeometryType,...”样式线条变成图层名称。</remarks>
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
        /// <remarks>中文：提取上次会话转录中提到的层名。解析context_gathered事件以描述地图图层。</remarks>
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
                catch (Newtonsoft.Json.JsonException) { /* 转录行损坏，跳过。 / Corrupt transcript line; skip it. */ }
            }

            return layers.ToList();
        }

        /// <summary>
        /// Extract the tool chain from a past session's transcript lines.
        /// Parses tool_end events.
        /// </summary>
        /// <remarks>中文：从上次会话的转录记录中提取工具链，并解析 tool_end 事件。</remarks>
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
                catch (Newtonsoft.Json.JsonException) { /* 转录行损坏，跳过。 / Corrupt transcript line; skip it. */ }
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
        /// <remarks>中文：纯微分计算：比较过去会话层与当前地图层。返回（stillPresent、missing、newLayers、needsAttention）。Truthfulness rule： 空数据 ≠ 零图层。只有当两边都有提取结果时才声称变更。解析失败 → canDiff=false → 不制造虚假声明。</remarks>
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
    /// <remarks>中文：表示上次会话状态与当前项目实际状态的比较结果。</remarks>
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
        /// <remarks>中文：构建一个人类可读的摘要，注入系统提示中。只陈述了实际观察到的事实。</remarks>
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
