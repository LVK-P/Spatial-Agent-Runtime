using System.Collections.Generic;
using System.Linq;

// ================================================================
// AgentContext.cs — 上下文聚合 DTO
//
// ContextManager.GatherAsync() 的输出，汇总了 Agent 做出决策
// 所需的全部上下文信息。
// ================================================================
namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Full snapshot produced by a single ContextManager.GatherAsync() call.
    /// Aggregates project structure, map state, knowledge chunks, matched skills,
    /// cross-session memory, file change info, and tool relevance filtering.
    /// </summary>
    /// <remarks>中文：单次 ContextManager.GatherAsync() 调用生成的完整快照，汇总项目结构、地图状态、知识片段、匹配技能、跨会话记忆、文件变更信息和工具相关性筛选结果。</remarks>
    public class AgentContext
    {
        // ── 项目结构 ──
        // English：Project structure
        /// <summary>Project directory tree, GDB paths, file inventory (text blob, for LLM prompt).</summary>
        /// <remarks>中文：项目目录树、GDB 路径和文件清单（用于 LLM 提示词的文本块）。</remarks>
        public string ProjectInfo { get; set; } = "";

        /// <summary>Extracted project name (structured, for DomainContext + checkpoint).</summary>
        /// <remarks>中文：提取项目名称（结构化，适用于 DomainContext + 检查点）。</remarks>
        public string ProjectName { get; set; } = "";

        /// <summary>Extracted project home folder path.</summary>
        /// <remarks>中文：提取出的项目主目录路径。</remarks>
        public string ProjectPath { get; set; } = "";

        /// <summary>Default geodatabase path.</summary>
        /// <remarks>中文：默认地理数据库路径。</remarks>
        public string GdbPath { get; set; } = "";

        // ── 地图状态 ──
        // English：Map status
        /// <summary>Layer list, field definitions, spatial reference, current selection (text blob, for LLM prompt).</summary>
        /// <remarks>中文：图层列表、字段定义、空间参考、当前选择（文本块，用于LLM提示）。</remarks>
        public string MapInfo { get; set; } = "";

        /// <summary>Active map name (structured, for DomainContext + checkpoint).</summary>
        /// <remarks>中文：活动地图名称（结构化，用于 DomainContext 和检查点）。</remarks>
        public string MapName { get; set; } = "";

        /// <summary>Typed live layer inventory used by planning and coverage policy.</summary>
        /// <remarks>中文：规划和覆盖策略使用的类型化实时图层清单。</remarks>
        public List<LayerInfo> Layers { get; set; } = new();

        // ── RAG 知识检索 ──
        /// <summary>Relevant document chunks retrieved from the knowledge base.</summary>
        /// <remarks>中文：从知识库检索相关文档块。</remarks>
        public List<string> KnowledgeChunks { get; set; } = new();

        // ── 技能匹配 ──
        // English：Skill matching
        /// <summary>Skills matched to the current user input.</summary>
        /// <remarks>中文：与当前用户输入匹配的技能。</remarks>
        public List<MatchedSkill> MatchedSkills { get; set; } = new();

        /// <summary>Summary of all available skills.</summary>
        /// <remarks>中文：所有可用技能的摘要。</remarks>
        public string SkillsSummary { get; set; } = "";

        // ── 跨会话记忆 ──
        // English：Cross-session memory
        /// <summary>Project memory summary (last 5 sessions).</summary>
        /// <remarks>中文：项目记忆摘要（最近 5 次会话）。</remarks>
        public string MemorySummary { get; set; } = "";

        /// <summary>5W2H project cognition analysis result.</summary>
        /// <remarks>中文：5W2H项目认知分析结果。</remarks>
        public string W5H2Context { get; set; } = "";

        // ── 便利属性 ──
        // English：Convenience attributes
        /// <summary>Concatenated project + map context (suitable for System Prompt injection).</summary>
        /// <remarks>中文：拼接后的项目与地图上下文，适合注入系统提示词。</remarks>
        public string CombinedContext =>
            string.IsNullOrEmpty(MapInfo)
                ? ProjectInfo
                : ProjectInfo + "\n\n【图层真实数据——仅基于以下信息回答，不要编造】\n" + MapInfo;

        /// <summary>Whether this is a new project (first conversation). Triggers proactive questioning.</summary>
        /// <remarks>中文：是否为新项目（首次对话）；为 true 时触发主动提问。</remarks>
        public bool IsNewProject { get; set; }

        // ── 坐标系上下文（P0-4）──
        // English：Coordinate System Context (P0-4)
        /// <summary>Map and per-layer spatial reference context. Injected into system prompt.</summary>
        /// <remarks>中文：地图及各图层的空间参考上下文，将注入系统提示词。</remarks>
        public SpatialRefContext SpatialRef { get; set; } = new();

        // ── 工具筛选 ──
        // English：Tool filtering
        /// <summary>Context-filtered tool names for the LLM Function Calling request.</summary>
        /// <remarks>中文：根据上下文筛选后、用于 LLM Function Calling 请求的工具名称。</remarks>
        public List<string> RelevantTools { get; set; } = new();

        /// <summary>Whether any meaningful context was collected.</summary>
        /// <remarks>中文：是否收集到任何有意义的上下文。</remarks>
        public bool HasContent =>
            !string.IsNullOrEmpty(ProjectInfo)
            || !string.IsNullOrEmpty(MapInfo)
            || KnowledgeChunks.Count > 0
            || MatchedSkills.Count > 0;
    }
}
