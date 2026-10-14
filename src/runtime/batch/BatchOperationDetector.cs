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
// English：================================================================ BatchOperationDetector.cs — Batch Intent Detector Detects batch operation intents from user messages ("Add fields to all layers"). Pure keyword + template matching, no LLM involvement required. After detection, BatchPlanExpander expands the specific execution plan. Template data should be injected from batch_templates.json and not hardcoded in the code. Source: Batch Recognition Strategy After Cross-Implementation Architecture Review. ================================================================
namespace SpatialAgent.Runtime.Batch
{
    /// <summary>
    /// Detects batch operation intent from user messages.
    /// Runtime engine — templates are injected as data, not hardcoded.
    /// </summary>
    /// <remarks>中文：从用户消息中检测批处理操作意图。运行时引擎——模板是以数据形式注入的，而不是硬编码的。</remarks>
    public class BatchOperationDetector
    {
        private readonly List<BatchTemplate> _templates;

        // Batch intent keywords — language-agnostic core set.
        // Extended via BatchTemplate.DetectsKeywords per template.
        // 中文：批处理意图关键词——语言无关的核心集。通过BatchTemplate.Detects每个模板的Keywords扩展。
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
        /// <remarks>中文：从用户消息中检测批量意图。如果没有匹配的批次模式，返回空。</remarks>
        public BatchIntent? Detect(string userMessage, List<string> availableLayers)
        {
            if (availableLayers.Count < 2) return null;

            var lower = userMessage.ToLowerInvariant();

            // Quick reject: no batch keyword → no batch intent
            // 中文：快速拒绝：没有批次关键词→没有批处理意图
            bool hasBatchKeyword = BatchKeywords.Any(k => lower.Contains(k));
            if (!hasBatchKeyword)
            {
                // Also check template-specific keywords
                // 中文：还要检查模板特定的关键词
                bool hasTemplateKeyword = _templates.Any(t =>
                    t.DetectsKeywords.Any(k => lower.Contains(k.ToLowerInvariant())));
                if (!hasTemplateKeyword) return null;
            }

            // Match against templates
            // 中文：对阵模板的比赛
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
    /// <remarks>中文：批处理操作模板——定义批处理模式。启动时从batch_templates.json加载。</remarks>
    public class BatchTemplate
    {
        /// <summary>Unique template name (e.g. "add_calc_field").</summary>
        /// <remarks>中文：唯一的模板名称（例如“add_calc_field”）。</remarks>
        public string Name { get; set; } = "";

        /// <summary>Tool names to apply per layer (in order).</summary>
        /// <remarks>中文：按图层应用的工具名称（按顺序）。</remarks>
        public string[] Tools { get; set; } = Array.Empty<string>();

        /// <summary>Keywords that trigger this template.</summary>
        /// <remarks>中文：触发该模板的关键词。</remarks>
        public string[] DetectsKeywords { get; set; } = Array.Empty<string>();

        /// <summary>Which parameter receives the layer name.</summary>
        /// <remarks>中文：哪个参数接收图层名称。</remarks>
        public string LayerParamName { get; set; } = "";

        /// <summary>
        /// True when tools should be paired per layer (e.g. AddField + CalculateField
        /// for layer A before moving to layer B). False when all layers can be processed
        /// with tool 1 first, then tool 2.
        /// </summary>
        /// <remarks>中文：当工具应按层配对时（例如在进入B层之前，先对A层使用AddField + CalculateField）。当所有图层都可以先用工具1处理，再用工具2处理时，则为假。</remarks>
        public bool IsPerLayerPair { get; set; }

        /// <summary>Human-readable description for audit logs.</summary>
        /// <remarks>中文：对审计日志的人类易读描述。</remarks>
        public string Description { get; set; } = "";
    }

    /// <summary>
    /// Detected batch intent — template matched + target layers resolved.
    /// </summary>
    /// <remarks>中文：检测到批处理意图 — 模板匹配 + 目标图层解析。</remarks>
    public class BatchIntent
    {
        public string TemplateName { get; set; } = "";
        public BatchTemplate Template { get; set; } = new();
        public List<string> TargetLayers { get; set; } = new();
    }
}
