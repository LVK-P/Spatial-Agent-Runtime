using System;
using System.Collections.Generic;
using System.Linq;

// ================================================================
// BatchOperationDetector.cs — 批量操作意图检测器
//
// 从用户消息中检测批量操作意图（"给所有图层加字段"）。
// 纯关键词 + 模板匹配，不需要 LLM 参与。检测到后由
// BatchPlanExpander 展开为具体执行计划。
//
// 模板数据应从 batch_templates.json 注入，不在代码中硬编码。
//
// 来源：跨实现架构审查后的批处理识别策略。
// ================================================================
namespace SpatialAgent.Runtime.Batch
{
    /// <summary>
    /// Detects batch operation intent from user messages.
    /// Runtime engine — templates are injected as data, not hardcoded.
    /// </summary>
    public class BatchOperationDetector
    {
        private readonly List<BatchTemplate> _templates;

        // Batch intent keywords — language-agnostic core set.
        // Extended via BatchTemplate.DetectsKeywords per template.
        private static readonly string[] BatchKeywords =
        {
            "所有图层", "每个图层", "全部图层", "各图层",
            "批量", "逐个", "每个", "所有", "全部",
            "all layers", "each layer", "every layer", "batch"
        };

        public BatchOperationDetector(List<BatchTemplate> templates)
        {
            _templates = templates ?? new List<BatchTemplate>();
        }

        /// <summary>
        /// Detect batch intent from user message.
        /// Returns null if no batch pattern matches.
        /// </summary>
        public BatchIntent? Detect(string userMessage, List<string> availableLayers)
        {
            if (availableLayers.Count < 2) return null;

            var lower = userMessage.ToLowerInvariant();

            // Quick reject: no batch keyword → no batch intent
            bool hasBatchKeyword = BatchKeywords.Any(k => lower.Contains(k));
            if (!hasBatchKeyword)
            {
                // Also check template-specific keywords
                bool hasTemplateKeyword = _templates.Any(t =>
                    t.DetectsKeywords.Any(k => lower.Contains(k.ToLowerInvariant())));
                if (!hasTemplateKeyword) return null;
            }

            // Match against templates
            foreach (var template in _templates)
            {
                if (!template.DetectsKeywords.Any(k =>
                    lower.Contains(k.ToLowerInvariant())))
                    continue;

                return new BatchIntent
                {
                    TemplateName = template.Name,
                    Template = template,
                    TargetLayers = availableLayers,
                };
            }

            return null;
        }
    }

    /// <summary>
    /// Batch operation template — defines a batch pattern.
    /// Loaded from batch_templates.json at startup.
    /// </summary>
    public class BatchTemplate
    {
        /// <summary>Unique template name (e.g. "add_calc_field").</summary>
        public string Name { get; set; } = "";

        /// <summary>Tool names to apply per layer (in order).</summary>
        public string[] Tools { get; set; } = Array.Empty<string>();

        /// <summary>Keywords that trigger this template.</summary>
        public string[] DetectsKeywords { get; set; } = Array.Empty<string>();

        /// <summary>Which parameter receives the layer name.</summary>
        public string LayerParamName { get; set; } = "";

        /// <summary>
        /// True when tools should be paired per layer (e.g. AddField + CalculateField
        /// for layer A before moving to layer B). False when all layers can be processed
        /// with tool 1 first, then tool 2.
        /// </summary>
        public bool IsPerLayerPair { get; set; }

        /// <summary>Human-readable description for audit logs.</summary>
        public string Description { get; set; } = "";
    }

    /// <summary>
    /// Detected batch intent — template matched + target layers resolved.
    /// </summary>
    public class BatchIntent
    {
        public string TemplateName { get; set; } = "";
        public BatchTemplate Template { get; set; } = new();
        public List<string> TargetLayers { get; set; } = new();
    }
}
