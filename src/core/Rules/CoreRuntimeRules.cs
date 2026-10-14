using System;
using System.Collections.Generic;
using System.Linq;
using SpatialAgent.Runtime.Contracts;

// ────────────────────────────────────────────────────────────────
// CoreRuntimeRules.cs — Pure GIS strategy tables and prediction logic.
//
// ADR 0056: 从 RuntimePolicy 中析出的纯规则——零 I/O、零宿主依赖。
// 这些规则可被 Core 测试独立验证，不依赖 agent 编排层。
//
// 内容：
//   1. ChainPredictions + PredictNextTools — GIS 操作链预测
//   2. SelectionToolLayerParams + SelectionIntentWords +
//      ResolveSelectionCleanups + BuildSelectionClearCall — 选择集收尾
//   3. StepStatusText — 纯状态→文本映射
//   4. DefaultContinuationTemplate — 续推指令兜底模板
// ────────────────────────────────────────────────────────────────
// English：CoreRuntimeRules.cs — Pure GIS strategy tables and prediction logic. ADR 0056: Pure rules derived from RuntimePolicy—zero I/O, zero host dependency. These rules can be independently verified by Core tests and do not rely on the agent orchestration layer. Content: 1. ChainPredictions + PredictNextTools — GIS Chain Operation Prediction 2. SelectionToolLayerParams + SelectionIntentWords + ResolveSelectionCleanups + BuildSelectionClearCall — End of selection set 3. StepStatusText — Pure State → Text Mapping 4. DefaultContinuationTemplate — Backup template for the push command backing function
namespace SpatialAgent.Core
{
    public static class CoreRuntimeRules
    {
        // ════════════════════════════════════════════════════════════════
        // 1. GIS 操作链预测（Chain Predictions）
        // ════════════════════════════════════════════════════════════════
        // English：1. GIS Chain Predictions

        /// <summary>Key = 上回合调用的工具名, Value = 应预提升到 Tier1 的后继工具名</summary>
        /// <remarks>English：Key = Name of the tool called last round, Value = Name of the successor tool that should be promoted to Tier 1</remarks>
        public static readonly Dictionary<string, string[]> ChainPredictions =
            new(StringComparer.OrdinalIgnoreCase)
        {
            // 投影 → 叠加分析链
            // English：Projection → Overlay analysis chain
            ["management_Project"] = new[] { "analysis_Clip", "analysis_Intersect", "analysis_Erase", "analysis_Statistics" },
            // 裁剪/相交 → 擦除 + 统计链
            // English：Clip/Intersect → Erase + Statistical Chain
            ["analysis_Clip"] = new[] { "analysis_Erase", "analysis_Statistics" },
            ["analysis_Intersect"] = new[] { "analysis_Erase", "analysis_Statistics", "management_SelectLayerByAttribute" },
            ["analysis_Erase"] = new[] { "analysis_Statistics" },
            // 缓冲 → 裁剪链
            // English：Buffer → Cutting chain
            ["analysis_Buffer"] = new[] { "analysis_Clip", "analysis_Intersect" },
            // 合并 → 溶解/统计
            // English：Merge → Dissolve/Statistics
            ["management_Merge"] = new[] { "management_Dissolve", "analysis_Statistics", "management_CalculateField" },
            ["management_Dissolve"] = new[] { "analysis_Statistics", "management_CalculateField" },
            // 字段赋值 → 统计/验证
            // English：Field assignment → Statistics/validation
            ["management_CalculateField"] = new[] { "analysis_Statistics", "cc_read_rows" },
            ["management_AddField"] = new[] { "management_CalculateField", "analysis_Statistics" },
            // 映射赋值 → 统计/自检
            // English：Mapping assignment → Statistics/self-check
            ["cc_map_field_values"] = new[] { "analysis_Statistics", "cc_lookup_land_code" },
            // 对照查询 → 映射赋值
            // English：Mapping assignments to query → reference
            ["cc_lookup_land_code"] = new[] { "cc_map_field_values", "management_CalculateField" },
            // 选择 → 计算/统计
            // English：Select → Calculate/Stat
            ["management_SelectLayerByAttribute"] = new[] { "management_CalculateField", "analysis_Statistics" },
            // 面积/长度计算 → 统计
            // English：Area/length calculation → Statistics
            ["cc_calculate_area"] = new[] { "analysis_Statistics" },
            ["cc_calculate_length"] = new[] { "analysis_Statistics" },
            // 拓扑自交（快检路径）→ 擦除（空隙）
            // English：Topological autocrossing (rapid detection path) → Erasure (voids)
            ["analysis_Union"] = new[] { "analysis_Erase", "analysis_Intersect" },
        };

        /// <summary>从上一回合的工具调用记录中提取后继预测——并入 Tier1</summary>
        /// <remarks>English：Extract successor predictions from the previous round's tool call records—merge them into Tier 1</remarks>
        public static IReadOnlyList<string> PredictNextTools(
            IReadOnlyList<string>? lastTurnToolCalls)
        {
            if (lastTurnToolCalls == null || lastTurnToolCalls.Count == 0)
                return Array.Empty<string>();

            var predicted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string called in lastTurnToolCalls)
            {
                if (ChainPredictions.TryGetValue(called, out var successors))
                    predicted.UnionWith(successors);
            }
            return predicted.ToList();
        }

        // ════════════════════════════════════════════════════════════════
        // 2. 选择集收尾策略（Selection Cleanup）
        // ════════════════════════════════════════════════════════════════
        // English：2. Selection Cleanup Strategy

        /// <summary>Selection-mutating GP tools and their layer parameter names.</summary>
        /// <remarks>中文：选择会修改状态的 GP 工具及其图层参数名。</remarks>
        public static readonly Dictionary<string, string[]> SelectionToolLayerParams =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["management_SelectLayerByAttribute"] = new[] { "in_layer_or_view" },
                ["management_SelectLayerByLocation"] = new[] { "in_layer", "in_layer_or_view" }
            };

        /// <summary>
        /// User-message keywords that signal "selecting features IS the task" —
        /// only then is a terminal selection kept. Deliberately excludes the
        /// generic "选择" (too ambiguous: "选择合适的方案").
        /// </summary>
        /// <remarks>中文：用户消息关键词表明“选择功能即为任务”——只有此时才会保留终端选择。故意排除通用的“选择”（太模糊：“选择合适的方案”）。</remarks>
        public static readonly string[] SelectionIntentWords =
            { "选中", "选出", "挑出", "框选", "高亮", "select" };

        /// <summary>
        /// Decide which layers need their selection cleared at end of turn.
        ///
        /// Boundary (tightened 2026-07-19): a selection the agent created is
        /// cleared UNLESS it is the turn's final executed step AND the user's
        /// message contains explicit selection intent.
        /// </summary>
        /// <remarks>中文：决定哪些层需要在回合结束时清除其选择。边界（2026-07-19收紧）：除非是该回合的最后执行步骤且用户消息包含明确的选择意图，否则代理创建的选择会被清除。</remarks>
        public static IReadOnlyList<string> ResolveSelectionCleanups(
            IReadOnlyList<(string ToolName, IReadOnlyDictionary<string, object?>? Args)> executedSteps,
            string userMessage = "")
        {
            if (executedSteps == null || executedSteps.Count == 0)
                return Array.Empty<string>();

            bool userWantsSelection = !string.IsNullOrEmpty(userMessage) &&
                SelectionIntentWords.Any(w =>
                    userMessage.Contains(w, StringComparison.OrdinalIgnoreCase));

            // layer → index of its last selection-setting call
            // 中文：其最后一次选择设置调用的图层→索引
            var pending = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < executedSteps.Count; i++)
            {
                var (toolName, args) = executedSteps[i];
                if (!SelectionToolLayerParams.TryGetValue(toolName ?? "", out var layerKeys))
                    continue;

                string? layer = null;
                if (args != null)
                {
                    foreach (string key in layerKeys)
                    {
                        layer = args.FirstOrDefault(kv =>
                            kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value?.ToString();
                        if (!string.IsNullOrWhiteSpace(layer)) break;
                    }
                }
                if (string.IsNullOrWhiteSpace(layer))
                    continue;

                string selectionType = args?.FirstOrDefault(kv =>
                        kv.Key.Equals("selection_type", StringComparison.OrdinalIgnoreCase))
                    .Value?.ToString() ?? "NEW_SELECTION";

                if (selectionType.Equals("CLEAR_SELECTION", StringComparison.OrdinalIgnoreCase))
                    pending.Remove(layer!);
                else
                    pending[layer!] = i;
            }

            int lastIndex = executedSteps.Count - 1;
            return pending
                .Where(kv => userWantsSelection ? kv.Value < lastIndex : true)
                .OrderBy(kv => kv.Value)
                .Select(kv => kv.Key)
                .ToList();
        }

        /// <summary>
        /// Build the standard CLEAR_SELECTION call for a layer.
        /// </summary>
        /// <remarks>中文：构建标准CLEAR_SELECTION调用层。</remarks>
        public static ToolCall BuildSelectionClearCall(string layer) => new()
        {
            Name = "management_SelectLayerByAttribute",
            Arguments = new Dictionary<string, object?>
            {
                ["in_layer_or_view"] = layer,
                ["selection_type"] = "CLEAR_SELECTION",
                ["where_clause"] = ""
            }
        };

        // ════════════════════════════════════════════════════════════════
        // 3. 状态文本映射
        // ════════════════════════════════════════════════════════════════
        // English：3. State text mapping

        public static string StepStatusText(StepResultStatus st) => st switch
        {
            StepResultStatus.Completed => "✅",
            StepResultStatus.Failed => "❌",
            StepResultStatus.AwaitingApproval => "⏸️",
            StepResultStatus.Blocked => "🚫",
            StepResultStatus.Skipped => "⏭️",
            _ => "⏭️"
        };

        // ════════════════════════════════════════════════════════════════
        // 4. 续推指令兜底模板
        // ════════════════════════════════════════════════════════════════
        // English：4. Continue push instructions to provide a bottom-line template

        public const string DefaultContinuationTemplate =
            "第{round}/{maxRounds}轮完成。本轮结果：{executedSummary}。剩余 {remaining} 轮。{warnNote}{blockedNote}\n\n" +
            "回顾用户最初的问题：{originalUserMessage}。\n" +
            "检查：用户要求的所有操作是否已全部完成？还缺哪些步骤？\n\n" +
            "如果用户要求生成图层/要素类，只有新输出存在且验证通过才算完成；读取面积或说明已有图层不能替代生成结果。\n" +
            "如果全部完成 → 纯文本回复最终结果。\n" +
            "如果还有缺失 → 输出下一批安全、可执行且能推进目标的 tool_calls。可以先做只读探测或覆盖合法目标子集；不要为了单轮凑齐全部目标而加入无关操作。\n" +
            "注意：同图层的字段创建和计算可以在同一轮 tool_calls 中一起输出（如 AddField + CalculateField）。";
    }
}
