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
    public class AgentContext
    {
        // ── 项目结构 ──
        /// <summary>Project directory tree, GDB paths, file inventory (text blob, for LLM prompt).</summary>
        public string ProjectInfo { get; set; } = "";

        /// <summary>Extracted project name (structured, for DomainContext + checkpoint).</summary>
        public string ProjectName { get; set; } = "";

        /// <summary>Extracted project home folder path.</summary>
        public string ProjectPath { get; set; } = "";

        /// <summary>Default geodatabase path.</summary>
        public string GdbPath { get; set; } = "";

        // ── 地图状态 ──
        /// <summary>Layer list, field definitions, spatial reference, current selection (text blob, for LLM prompt).</summary>
        public string MapInfo { get; set; } = "";

        /// <summary>Active map name (structured, for DomainContext + checkpoint).</summary>
        public string MapName { get; set; } = "";

        /// <summary>Typed live layer inventory used by planning and coverage policy.</summary>
        public List<LayerInfo> Layers { get; set; } = new();

        // ── RAG 知识检索 ──
        /// <summary>Relevant document chunks retrieved from the knowledge base.</summary>
        public List<string> KnowledgeChunks { get; set; } = new();

        // ── 技能匹配 ──
        /// <summary>Skills matched to the current user input.</summary>
        public List<MatchedSkill> MatchedSkills { get; set; } = new();

        /// <summary>Summary of all available skills.</summary>
        public string SkillsSummary { get; set; } = "";

        // ── 跨会话记忆 ──
        /// <summary>Project memory summary (last 5 sessions).</summary>
        public string MemorySummary { get; set; } = "";

        /// <summary>5W2H project cognition analysis result.</summary>
        public string W5H2Context { get; set; } = "";

        // ── 便利属性 ──
        /// <summary>Concatenated project + map context (suitable for System Prompt injection).</summary>
        public string CombinedContext =>
            string.IsNullOrEmpty(MapInfo)
                ? ProjectInfo
                : ProjectInfo + "\n\n【图层真实数据——仅基于以下信息回答，不要编造】\n" + MapInfo;

        /// <summary>Whether this is a new project (first conversation). Triggers proactive questioning.</summary>
        public bool IsNewProject { get; set; }

        // ── 坐标系上下文（P0-4）──
        /// <summary>Map and per-layer spatial reference context. Injected into system prompt.</summary>
        public SpatialRefContext SpatialRef { get; set; } = new();

        // ── 工具筛选 ──
        /// <summary>Context-filtered tool names for the LLM Function Calling request.</summary>
        public List<string> RelevantTools { get; set; } = new();

        /// <summary>Whether any meaningful context was collected.</summary>
        public bool HasContent =>
            !string.IsNullOrEmpty(ProjectInfo)
            || !string.IsNullOrEmpty(MapInfo)
            || KnowledgeChunks.Count > 0
            || MatchedSkills.Count > 0;
    }
}
