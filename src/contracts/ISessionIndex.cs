using System;
using System.Collections.Generic;

namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Index of historical agent sessions for a project.
    /// Used by the session browser UI to list and select past sessions,
    /// enabling "continue from where I left off" with state reconciliation.
    /// </summary>
    public interface ISessionIndex
    {
        /// <summary>List all past sessions for the project, most recent first.</summary>
        IReadOnlyList<SessionInfo> ListSessions();

        /// <summary>Get metadata for a specific session by thread ID.</summary>
        SessionInfo? GetSession(string threadId);

        /// <summary>Whether the index has been loaded.</summary>
        bool IsLoaded { get; }

        /// <summary>Total number of historical sessions found.</summary>
        int Count { get; }
    }

    /// <summary>
    /// Lightweight session metadata for the session browser dropdown.
    /// </summary>
    public class SessionInfo
    {
        /// <summary>Thread ID (e.g. "pro-20260718-161432-036").</summary>
        public string ThreadId { get; set; } = "";

        /// <summary>Last checkpoint save time (UTC) — sort key for "most recent first".</summary>
        public DateTime SavedAt { get; set; }

        /// <summary>Human-readable date label.</summary>
        public string DateLabel { get; set; } = "";

        /// <summary>First line of session summary (what was done).</summary>
        public string Summary { get; set; } = "";

        /// <summary>Number of turns in the session.</summary>
        public int TurnCount { get; set; }

        /// <summary>Number of tool calls executed.</summary>
        public int ToolCount { get; set; }

        /// <summary>Tool names used (distinct, for quick reference).</summary>
        public string ToolChain { get; set; } = "";

        /// <summary>Whether this session had verification failures.</summary>
        public bool HasFailures { get; set; }

        /// <summary>Display label for the dropdown (date + summary preview).</summary>
        public string DisplayLabel =>
            string.IsNullOrWhiteSpace(Summary)
                ? $"{DateLabel} · {TurnCount}轮 · {ToolCount}工具"
                : $"{DateLabel} · {Truncate(Summary, 40)}";

        private static string Truncate(string value, int max)
            => (value?.Length ?? 0) <= max ? (value ?? "") : value![..max] + "…";
    }
}
