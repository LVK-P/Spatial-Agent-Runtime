using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Services
{
    /// <summary>
    /// Scans .agent/checkpoints/ to build a session index for the browser dropdown.
    /// Each checkpoint directory is one historical session.
    /// </summary>
    public class SessionIndex : ISessionIndex
    {
        private readonly string _projectFolder;
        private readonly List<SessionInfo> _sessions = new();

        public bool IsLoaded { get; private set; }
        public int Count => _sessions.Count;

        public SessionIndex(string projectFolder)
        {
            _projectFolder = projectFolder;
        }

        /// <summary>
        /// Load session metadata from the checkpoints directory.
        /// Safe to call multiple times — reloads on each call.
        /// </summary>
        public void Load()
        {
            _sessions.Clear();
            IsLoaded = false;

            string checkpointRoot = Path.Combine(_projectFolder, ".agent", "checkpoints");
            if (!Directory.Exists(checkpointRoot))
                return;

            foreach (var dir in Directory.GetDirectories(checkpointRoot))
            {
                try
                {
                    var info = BuildSessionInfo(dir);
                    if (info != null)
                        _sessions.Add(info);
                }
                catch
                {
                    // Skip corrupt checkpoint directories silently
                }
            }

            // Most recent first (SavedAt is UTC; string DateLabel would break across years)
            _sessions.Sort((a, b) => b.SavedAt.CompareTo(a.SavedAt));
            IsLoaded = true;
        }

        public IReadOnlyList<SessionInfo> ListSessions()
        {
            if (!IsLoaded) Load();
            return _sessions.AsReadOnly();
        }

        public SessionInfo? GetSession(string threadId)
        {
            if (!IsLoaded) Load();
            return _sessions.FirstOrDefault(s =>
                s.ThreadId.Equals(threadId, StringComparison.OrdinalIgnoreCase));
        }

        private SessionInfo? BuildSessionInfo(string dirPath)
        {
            string threadId = Path.GetFileName(dirPath);
            if (string.IsNullOrWhiteSpace(threadId))
                return null;

            // Only real checkpoint files. The directory also holds latest.json
            // (a pointer index) — it deserializes into a hollow AgentRunState with
            // the same Turn/Round as the newest checkpoint and would win the tie
            // below, blanking out Summary/tool info.
            var checkpointFiles = Directory.GetFiles(dirPath, "turn_*.json");
            if (checkpointFiles.Length == 0)
                return null;

            // Parse checkpoint files to find the latest state
            AgentRunState? latest = null;
            foreach (var file in checkpointFiles)
            {
                try
                {
                    var state = JsonConvert.DeserializeObject<AgentRunState>(File.ReadAllText(file));
                    if (state == null) continue;

                    if (latest == null ||
                        state.Turn > latest.Turn ||
                        (state.Turn == latest.Turn && state.Round > latest.Round))
                        latest = state;
                }
                catch { }
            }

            if (latest == null) return null;

            // Tools actually invoked (assistant tool_calls) — NOT ToolExposure,
            // which is the ~70-entry list of tools *offered* to the model.
            var calledTools = latest.Messages
                .Where(m => m.ToolCalls != null)
                .SelectMany(m => m.ToolCalls!)
                .Select(c => c.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();

            // Session title: the opening user request reads better than the last one
            string firstUserMessage = latest.Messages
                .FirstOrDefault(m => m.Role == "user")?.Content ?? latest.UserMessage ?? "";

            return new SessionInfo
            {
                ThreadId = string.IsNullOrWhiteSpace(latest.ThreadId) ? threadId : latest.ThreadId,
                SavedAt = latest.SavedAt,
                DateLabel = latest.SavedAt.ToLocalTime().ToString("MM-dd HH:mm"),
                Summary = firstUserMessage,
                TurnCount = latest.Turn,
                ToolCount = calledTools.Count,
                ToolChain = string.Join(" → ",
                    calledTools.Distinct(StringComparer.OrdinalIgnoreCase).Take(6)),
                HasFailures = latest.Status == SessionStatus.Failed
            };
        }
    }
}
