using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using SpatialAgent.Runtime.Contracts;
using SpatialAgent.Core;

namespace SpatialAgent.Runtime
{
    /// <summary>
    /// Runtime-level policy loaded from runtime_config.json with hardcoded defaults.
    /// </summary>
    /// <remarks>中文：运行时级策略从runtime_config.json加载，带有硬编码默认值。</remarks>
    public static class RuntimePolicy
    {
        private static readonly Lazy<RuntimeConfig> _config = new(LoadConfig);

        /// <summary>Tier 1 full-schema budget beyond the read-only probe kernel.
        /// Capability tools are considered before skill and chain candidates.</summary>
        /// <remarks>中文：一级全模式预算，超出只读探针内核。技能和链条候选人之前会考虑能力工具。</remarks>
        private const int MaxTier1Additional = 8;

        // ── Config-backed properties ──
        // 中文：配置支持的属性
        public static int MaxReactRounds => _config.Value.MaxReactRounds;
        public static int LargeBatchThreshold => _config.Value.LargeBatchThreshold;
        public static int DataInfluenceThreshold => _config.Value.DataInfluenceThreshold;
        public static IReadOnlySet<string> AlwaysOnToolNames => _config.Value.AlwaysOnToolNamesSet;

        public static int ToolIndexGeomMatchScore => _config.Value.ToolIndex?.GeomMatchScore ?? 10;
        public static int ToolIndexOpMatchScore => _config.Value.ToolIndex?.OpMatchScore ?? 5;
        public static int ToolIndexToolboxBoost => _config.Value.ToolIndex?.ToolboxBoostScore ?? 2;
        public static int ToolIndexTopN => _config.Value.ToolIndex?.TopN ?? 20;
        public static int MaxTier2Tools => _config.Value.MaxTier2Tools;

        public static int ContextMaxAugmentedChars => _config.Value.Context?.MaxAugmentedChars ?? 12000;
        public static int ContextMaxProjectChars => _config.Value.Context?.ProjectChars ?? 3200;
        public static int ContextMaxMapChars => _config.Value.Context?.MapChars ?? 4200;
        public static int ContextMaxKnowledgeChars => _config.Value.Context?.KnowledgeChars ?? 2600;
        public static int ContextMaxMemoryChars => _config.Value.Context?.MemoryChars ?? 1800;
        public static int ContextMaxSkillChars => _config.Value.Context?.SkillChars ?? 1200;
        public static int ContextMaxFileChangeChars => _config.Value.Context?.FileChangeChars ?? 800;
        public static int ContextMaxKnowledgeChunks => _config.Value.Context?.KnowledgeChunks ?? 3;
        public static int ContextMaxMatchedSkills => _config.Value.Context?.MatchedSkills ?? 3;

        public static IReadOnlyList<IToolDescriptor> SelectExposedTools(
            IReadOnlyList<IToolDescriptor> allTools,
            IEnumerable<string> relevantTools)
        {
            var relevantNames = new HashSet<string>(relevantTools, StringComparer.OrdinalIgnoreCase);
            relevantNames.UnionWith(AlwaysOnToolNames);
            return allTools.Where(t => relevantNames.Contains(t.Name)).ToList().AsReadOnly();
        }

        /// <summary>
        /// Two-tier tool exposure (Path D, 2026-07-19)：
        /// Tier 1 （完整） = always-on + skill 声明的 RequiredTools，
        ///   模型拿到完整参数描述，确保高频/安全关键工具调用正确。
        /// Tier 2 （轻量） = context top-N 中不在 Tier 1 的剩余工具，
        ///   用 LightweightToolDescriptor 剥离参描/枚举/标题，仅保留 name+type+required，
        ///   每工具约 0.3K token（vs 完整版约 1.8K）。
        ///
        /// Current hard budget: 6 always-on probes + at most 8 promoted full
        /// descriptors + at most 20 lightweight discovery descriptors.
        /// </summary>
        /// <returns>(tier1FullDetail, tier2Lightweight)</returns>
        /// <remarks>中文：两层工具暴露（路径D，2026-07-19）：Tier 1 （完整） = always-on + skill 声明的 RequiredTools， 模型拿到完整参数描述，确保高频/安全关键工具调用正确。Tier 2 （轻量） = context top-N 中不在 Tier 1 的剩余工具， 用 LightweightToolDescriptor 剥离参描/枚举/标题，仅保留 name+type+required， 每工具约 0.3K token（vs 完整版约 1.8K）。当前硬预算：6个始终在线探测器 + 最多8个晋升完整描述符 + 最多20个轻量级发现描述符。（tier1FullDetail，tier2Lightweight）</remarks>
        public static (IReadOnlyList<IToolDescriptor> Tier1Full, IReadOnlyList<IToolDescriptor> Tier2Light)
            SelectExposedToolsTiered(
                IReadOnlyList<IToolDescriptor> allTools,
                IEnumerable<string> relevantTools,
                IReadOnlyList<string>? skillRequiredTools = null,
                IReadOnlyList<string>? predictedTools = null)
        {
            var tier1Names = new HashSet<string>(AlwaysOnToolNames, StringComparer.OrdinalIgnoreCase);

            // ★ Q3（Phase 4）：附加上限——技能工具 + 链预测 ≤ MaxTier1Additional
            // English：Q3 (Phase 4): Upper limit — skill tools + chain prediction ≤ MaxTier1 Additional
            int additionalSlots = MaxTier1Additional;

            // 技能声明的所需工具 = 模型这一轮大概率要调用的 → 进完整暴露
            // English：The tool needed for skill declarations = the → most likely to be called in this round of the model is fully exposed
            if (skillRequiredTools != null)
            {
                foreach (string name in skillRequiredTools)
                {
                    if (!string.IsNullOrWhiteSpace(name) && additionalSlots > 0)
                    {
                        if (tier1Names.Add(name))
                            additionalSlots--;
                    }
                }
            }

            // ★ 操作链预测：上回合调用触发的后继工具 → 进完整暴露
            // English：Chain of Operation Prediction: The successor tool triggered by the last call → the full exposure
            if (predictedTools != null)
            {
                foreach (string name in predictedTools)
                {
                    if (additionalSlots <= 0) break;
                    if (tier1Names.Add(name))
                        additionalSlots--;
                }
            }

            // Tier 2 is a bounded discovery surface. Preserve the relevance
            // order before turning it into a lookup set.
            // 中文：第二层是一个有界发现曲面。在将其转换为查找集之前，先保留相关性顺序。
            var tier2Names = relevantTools
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Where(name => !tier1Names.Contains(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxTier2Tools)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var tier1 = new List<IToolDescriptor>();
            var tier2 = new List<IToolDescriptor>();

            foreach (var tool in allTools)
            {
                if (tier1Names.Contains(tool.Name))
                    tier1.Add(tool);                         // 完整描述，原样保留。 / Keep the full descriptor unchanged.
                else if (tier2Names.Contains(tool.Name))
                    tier2.Add(new LightweightToolDescriptor(tool));  // 轻量包装，移除参数描述。 / Lightweight wrapper with parameter descriptions removed.
            }

            return (tier1.AsReadOnly(), tier2.AsReadOnly());
        }

        // ── GIS 操作链预测（Path D 增强，2026-07-19）──
        //
        // 规划项目中的工具调用有极高的序列规律性——"描述→计算→统计""投影→裁剪→擦除"。
        // 当模型调用链条起点工具时，预提升后继工具到 Tier1（完整参描），避免下一轮
        // 模型需要它们时还在 Tier2（轻量参描）中。确定性静态表，不赌 LLM。
        //
        // 触发时机：SelectExposedToolsTiered 检查上一回合的工具调用记录，
        // 命中起点 → 后继进 Tier1。零新依赖，纯 RuntimePolicy 内部逻辑。
        // English：GIS Chain of Operation Prediction (Path D Enhancement, 2026-07-19) Tool calls in planning projects have extremely high sequence regularity—"describe→compute→ statistical," "projected → clipped→erase". When the model calls the chain origin tool, it pre-elevates the successor tool to Tier 1 (full parameter), avoiding being in Tier 2 (light parameter) when the next round of the model needs it. Deterministic static tables, no gambling on LLMs. Trigger timing: SelectExposedToolsTiered checks the previous round's tool call record, hits the starting point →, then proceeds to Tier 1. Zero new dependencies, pure RuntimePolicy internal logic.

        // ★ ADR 0056 / Phase 3：操作链预测迁入 Core
        // English：ADR 0056 / Phase 3: Operation chain prediction migration into Core
        public static IReadOnlyList<string> PredictNextTools(
            IReadOnlyList<string>? lastTurnToolCalls)
            => CoreRuntimeRules.PredictNextTools(lastTurnToolCalls);

        // ★ ADR 0056 / Phase 3：选择集收尾策略迁入 Core
        // English：ADR 0056 / Phase 3: Select the set closing strategy to migrate into the core
        public static IReadOnlyList<string> ResolveSelectionCleanups(
            IReadOnlyList<(string ToolName, IReadOnlyDictionary<string, object?>? Args)> executedSteps,
            string userMessage = "")
            => CoreRuntimeRules.ResolveSelectionCleanups(executedSteps, userMessage);

        public static ToolCall BuildSelectionClearCall(string layer)
            => CoreRuntimeRules.BuildSelectionClearCall(layer);

        private static readonly Lazy<string> _continuationTemplate = new(LoadContinuationTemplate);

        public static string BuildContinuationPrompt(
            int round,
            IReadOnlyList<StepExecutionResult> steps,
            IReadOnlyCollection<string> blockedToolsThisTurn,
            string originalUserMessage)
        {
            int remaining = MaxReactRounds - round - 1;
            string executedSummary = string.Join("、", steps.Select(sr => $"{StepStatusText(sr.Status)}{sr.ToolName}"));
            bool hadIssues = steps.Any(sr =>
                sr.Status != StepResultStatus.Completed ||
                sr.Validations.Any(v => v.Verdict == ValidationVerdict.FAIL));
            string warnNote = hadIssues ? "\n注意：部分步骤未成功（被阻止/失败/验证未通过），需要检查。" : "";
            string blockedNote = blockedToolsThisTurn.Count > 0
                ? $"\n本轮已被安全策略阻止的工具不要再次调用：{string.Join("、", blockedToolsThisTurn)}。如果没有替代工具，请直接说明无法完成。"
                : "";

            // ★ 冗余执行防护：列出已成功的工具，明确告诉 LLM 不要重做
            // English：Redundant execution protection: List successful tools and clearly tell LLMs not to redo them
            var completedStepNames = steps
                .Where(sr => sr.Status == StepResultStatus.Completed)
                .Select(sr => sr.ToolName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            string completedToolsNote = completedStepNames.Count > 0
                ? $"\n已成功完成无需重做：{string.Join("、", completedStepNames)}。"
                : "";

            string template = _continuationTemplate.Value;
            return template
                .Replace("{round}", (round + 1).ToString())
                .Replace("{maxRounds}", MaxReactRounds.ToString())
                .Replace("{remaining}", remaining.ToString())
                .Replace("{executedSummary}", executedSummary)
                .Replace("{warnNote}", warnNote)
                .Replace("{blockedNote}", blockedNote)
                .Replace("{completedToolsNote}", completedToolsNote)
                .Replace("{originalUserMessage}", originalUserMessage);
        }

        private static string LoadContinuationTemplate()
        {
            try
            {
                string? mdPath = ResourcePaths.Find("system_prompt.md");
                if (mdPath != null)
                {
                    string md = File.ReadAllText(mdPath, System.Text.Encoding.UTF8);
                    // Extract the # 续推指令 block — between the heading and EOF.
                    // 中文：提取#续推指令块——位于标题和EOF之间。
                    int start = md.IndexOf("# 续推指令");
                    if (start >= 0)
                    {
                        string block = md[start..];
                        // Remove the heading line and the metadata comment.
                        // 中文：删除标题行和元数据评论。
                        int nl = block.IndexOf('\n');
                        if (nl >= 0) block = block[(nl + 1)..];
                        nl = block.IndexOf('\n');
                        if (nl >= 0 && block.StartsWith(">")) block = block[(nl + 1)..];
                        if (!string.IsNullOrWhiteSpace(block))
                            return block.Trim();
                    }
                }
            }
            catch (Exception ex)
            {
                AgentDiagnostics.Warn("RuntimePolicy.ContinuationPrompt",
                    "续推指令外部模板加载失败，使用内置兜底", ex);
            }

            return CoreRuntimeRules.DefaultContinuationTemplate;
        }

        // ★ ADR 0056 / Phase 3：状态文本映射迁入 Core
        // English：ADR 0056 / Phase 3: Migration of state text mapping into the core
        public static string StepStatusText(StepResultStatus st)
            => CoreRuntimeRules.StepStatusText(st);

        private static RuntimeConfig LoadConfig()
        {
            try
            {
                string? configPath = ResourcePaths.Find("runtime_config.json");
                if (configPath != null)
                {
                    string json = File.ReadAllText(configPath);
                    var config = JsonConvert.DeserializeObject<RuntimeConfig>(json);
                    if (config != null)
                        return config;
                }
            }
            catch (Exception ex)
            {
                // Fall through to defaults — config file is optional.
                // 中文：切换到默认设置——配置文件是可选的。
                AgentDiagnostics.Warn("RuntimePolicy.LoadConfig",
                    "runtime_config.json 解析失败，使用内置默认策略", ex);
            }

            return RuntimeConfig.Default;
        }

        private class RuntimeConfig
        {
            [JsonProperty("maxReactRounds", DefaultValueHandling = DefaultValueHandling.Populate)]
            public int MaxReactRounds { get; set; } = 5;

            [JsonProperty("largeBatchThreshold", DefaultValueHandling = DefaultValueHandling.Populate)]
            public int LargeBatchThreshold { get; set; } = 1000;

            [JsonProperty("dataInfluenceThreshold", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public int DataInfluenceThreshold { get; set; } = 100;

            [JsonProperty("alwaysOnTools")]
            public List<string>? AlwaysOnTools { get; set; }

            [JsonProperty("maxTier2Tools", DefaultValueHandling = DefaultValueHandling.Populate)]
            public int MaxTier2Tools { get; set; } = 20;

            [JsonProperty("toolIndex")]
            public ToolIndexConfig? ToolIndex { get; set; }

            [JsonProperty("context")]
            public ContextConfig? Context { get; set; }

            [JsonIgnore]
            public IReadOnlySet<string> AlwaysOnToolNamesSet =>
                new HashSet<string>(
                    AlwaysOnTools ?? DefaultAlwaysOnTools,
                    StringComparer.OrdinalIgnoreCase);

            public static RuntimeConfig Default => new();

            private static readonly List<string> DefaultAlwaysOnTools = new()
            {
                "agent_get_map_info", "agent_list_layers", "agent_describe_layer",
                "agent_get_selection", "agent_get_project_info",
                "cc_read_rows"
            };
        }

        private class ToolIndexConfig
        {
            [JsonProperty("geomMatchScore", DefaultValueHandling = DefaultValueHandling.Populate)]
            public int GeomMatchScore { get; set; } = 10;

            [JsonProperty("opMatchScore", DefaultValueHandling = DefaultValueHandling.Populate)]
            public int OpMatchScore { get; set; } = 5;

            [JsonProperty("toolboxBoostScore", DefaultValueHandling = DefaultValueHandling.Populate)]
            public int ToolboxBoostScore { get; set; } = 2;

            [JsonProperty("topN", DefaultValueHandling = DefaultValueHandling.Populate)]
            public int TopN { get; set; } = 20;
        }

        private class ContextConfig
        {
            [JsonProperty("maxAugmentedChars", DefaultValueHandling = DefaultValueHandling.Populate)]
            public int MaxAugmentedChars { get; set; } = 12000;

            [JsonProperty("projectChars", DefaultValueHandling = DefaultValueHandling.Populate)]
            public int ProjectChars { get; set; } = 3200;

            [JsonProperty("mapChars", DefaultValueHandling = DefaultValueHandling.Populate)]
            public int MapChars { get; set; } = 4200;

            [JsonProperty("knowledgeChars", DefaultValueHandling = DefaultValueHandling.Populate)]
            public int KnowledgeChars { get; set; } = 2600;

            [JsonProperty("memoryChars", DefaultValueHandling = DefaultValueHandling.Populate)]
            public int MemoryChars { get; set; } = 1800;

            [JsonProperty("skillChars", DefaultValueHandling = DefaultValueHandling.Populate)]
            public int SkillChars { get; set; } = 1200;

            [JsonProperty("fileChangeChars", DefaultValueHandling = DefaultValueHandling.Populate)]
            public int FileChangeChars { get; set; } = 800;

            [JsonProperty("knowledgeChunks", DefaultValueHandling = DefaultValueHandling.Populate)]
            public int KnowledgeChunks { get; set; } = 3;

            [JsonProperty("matchedSkills", DefaultValueHandling = DefaultValueHandling.Populate)]
            public int MatchedSkills { get; set; } = 3;
        }
    }
}
