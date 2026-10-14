using System;
using System.Collections.Generic;
using System.Linq;
using SpatialAgent.Runtime.Contracts;

// ================================================================
// BatchPlanExpander.cs — 批量操作计划展开器
//
// 将 BatchIntent 展开为具体的 ToolCall 列表。
// 每层独立生成 ToolCall，确保失败隔离。
// maxLayersPerTurn 限制单轮展开数，防止 context 溢出。
//
// 来源：跨实现架构审查后的批处理规划策略。
// ================================================================
// English：================================================================ BatchPlanExpander.cs — Batch Operation Planning Expander expands BatchIntent into a specific ToolCall list. Each layer generates ToolCall independently to ensure failure isolation. maxLayersPerTurn limits the number of rounds of expansion to prevent context overflow. Source: Batch Planning Strategy After Implementation Architecture Review. ================================================================
namespace SpatialAgent.Runtime.Batch
{
    /// <summary>
    /// Expands a batch intent into ordered ToolCalls.
    /// Pure logic — no ArcGIS dependency.
    /// </summary>
    /// <remarks>中文：将批处理意图扩展为有序的工具调用。纯逻辑——没有ArcGIS依赖。</remarks>
    public class BatchPlanExpander
    {
        /// <summary>
        /// Expand batch intent into ordered tool calls.
        /// Per-layer pairs (AddField + CalculateField) are grouped when
        /// IsPerLayerPair is true: complete layer A first, then layer B.
        /// </summary>
        /// <param name="intent">Detected batch intent.</param>
        /// <param name="commonParams">
        /// Parameters shared across all layers (e.g. expression, field_type).
        /// The layer-specific parameter (LayerParamName) is set per-layer.
        /// </param>
        /// <param name="maxLayersPerTurn">Safety limit to prevent context overflow.</param>
        /// <remarks>中文：将批处理意图扩展为有序的工具调用。当 IsPerLayer Pair 为真时，每层对（AddField + CalculateField）会被分组：先完成 A 层，然后是 B 层。检测到批处理意图。参数在所有层间共享（例如表达式、field_type）。层特定参数（LayerParamName）是每层设置的。安全限值以防止上下文溢出。</remarks>
        public List<ToolCall> Expand(
            BatchIntent intent,
            Dictionary<string, object?> commonParams,
            int maxLayersPerTurn = 50)
        {
            var calls = new List<ToolCall>();
            int callId = 1;
            int layerCount = Math.Min(intent.TargetLayers.Count, maxLayersPerTurn);

            if (intent.Template.IsPerLayerPair)
            {
                // Per-layer pairing: for each layer, run all tools before next layer.
                // E.g. AddField(layer_A) → CalculateField(layer_A) → AddField(layer_B) → ...
                // 中文：每层配对：每层，先运行所有工具，再进行下一层。例如 AddField（layer_A） → CalculateField（layer_A） → AddField（layer_B） → ...
                foreach (var layer in intent.TargetLayers.Take(layerCount))
                {
                    foreach (var toolName in intent.Template.Tools)
                    {
                        calls.Add(BuildCall(toolName, layer, intent.Template.LayerParamName,
                            commonParams, ref callId));
                    }
                }
            }
            else
            {
                // Tool-first ordering: run tool 1 on all layers, then tool 2 on all layers.
                // E.g. AddField(all_layers) → CalculateField(all_layers)
                // 中文：工具优先排序：先在所有图层上运行工具1，再在所有图层上运行工具2。例如，AddField（all_layers） → CalculateField（all_layers）
                foreach (var toolName in intent.Template.Tools)
                {
                    foreach (var layer in intent.TargetLayers.Take(layerCount))
                    {
                        calls.Add(BuildCall(toolName, layer, intent.Template.LayerParamName,
                            commonParams, ref callId));
                    }
                }
            }

            return calls;
        }

        private static ToolCall BuildCall(
            string toolName, string layerName, string layerParamName,
            Dictionary<string, object?> commonParams, ref int callId)
        {
            var args = new Dictionary<string, object?>(commonParams)
            {
                [layerParamName] = layerName
            };

            return new ToolCall
            {
                Id = $"batch_{callId++}",
                Name = toolName,
                Arguments = args
            };
        }
    }
}
