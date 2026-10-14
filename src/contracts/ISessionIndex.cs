using System;
using System.Collections.Generic;

namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Index of historical agent sessions for a project.
    /// Used by the session browser UI to list and select past sessions,
    /// enabling "continue from where I left off" with state reconciliation.
    /// </summary>
    /// <remarks>中文：项目的历史 Agent 会话索引。会话浏览器 UI 用它列出并选择过去的会话，以支持“从上次中断处继续”和状态协调。</remarks>
    public interface ISessionIndex
    {
        /// <summary>List all past sessions for the project, most recent first.</summary>
        /// <remarks>中文：列出项目的全部历史会话，按最近时间优先排序。</remarks>
        IReadOnlyList<SessionInfo> ListSessions();

        /// <summary>Get metadata for a specific session by thread ID.</summary>
        /// <remarks>中文：通过线程ID获取特定会话的元数据。</remarks>
        SessionInfo? GetSession(string threadId);

        /// <summary>Whether the index has been loaded.</summary>
        /// <remarks>中文：索引是否已加载。</remarks>
        bool IsLoaded { get; }

        /// <summary>Total number of historical sessions found.</summary>
        /// <remarks>中文：总共发现的历史会话数量。</remarks>
        int Count { get; }
    }

    /// <summary>
    /// Lightweight session metadata for the session browser dropdown.
    /// </summary>
    /// <remarks>中文：用于会话浏览器下拉列表的轻量级会话元数据。</remarks>
    public class SessionInfo
    {
        /// <summary>Thread ID (e.g. "pro-20260718-161432-036").</summary>
        /// <remarks>中文：线程ID（例如“pro-20260718-161432-036”）。</remarks>
        public string ThreadId { get; set; } = "";

        /// <summary>Last checkpoint save time (UTC) — sort key for "most recent first".</summary>
        /// <remarks>中文：最后检查点保存时间（UTC）——排序键为“最近”优先。</remarks>
        public DateTime SavedAt { get; set; }

        /// <summary>Human-readable date label.</summary>
        /// <remarks>中文：易读的日期标签。</remarks>
        public string DateLabel { get; set; } = "";

        /// <summary>First line of session summary (what was done).</summary>
        /// <remarks>中文：会话摘要的第一行（说明完成了什么）。</remarks>
        public string Summary { get; set; } = "";

        /// <summary>Number of turns in the session.</summary>
        /// <remarks>中文：会话中的轮次数。</remarks>
        public int TurnCount { get; set; }

        /// <summary>Number of tool calls executed.</summary>
        /// <remarks>中文：执行的工具调用次数。</remarks>
        public int ToolCount { get; set; }

        /// <summary>Tool names used (distinct, for quick reference).</summary>
        /// <remarks>中文：使用过的工具名称（去重，便于快速查看）。</remarks>
        public string ToolChain { get; set; } = "";

        /// <summary>Whether this session had verification failures.</summary>
        /// <remarks>中文：这次会话是否存在验证失败。</remarks>
        public bool HasFailures { get; set; }

        /// <summary>Display label for the dropdown (date + summary preview).</summary>
        /// <remarks>中文：下拉列表的显示标签（日期 + 摘要预览）。</remarks>
        public string DisplayLabel =>
            string.IsNullOrWhiteSpace(Summary)
                ? $"{DateLabel} · {TurnCount}轮 · {ToolCount}工具"
                : $"{DateLabel} · {Truncate(Summary, 40)}";

        private static string Truncate(string value, int max)
            => (value?.Length ?? 0) <= max ? (value ?? "") : value![..max] + "…";
    }
}
