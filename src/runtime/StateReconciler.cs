using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SpatialAgent.Core;

namespace SpatialAgent.Runtime
{
    /// <summary>
    /// Compares a past session's projected state against current project reality.
    /// Reads transcript JSONL → extracts layer references + tool chain →
    /// produces a human-readable delta report for injection into system prompt.
    ///
    /// ★ ADR 0056 / Phase 3：纯逻辑（ParseLayerNames / ExtractReferencedLayers /
    /// ExtractToolChain / diff 计算 / ReconciliationReport）已迁入 Core。
    /// StateReconciler 仅保留文件 I/O、路径定位和宿主集成。
    ///
    /// Truthfulness rule: a diff claim ("已删除"/"新增") is only produced when
    /// BOTH sides of the comparison have data. Missing transcript / empty map
    /// info degrade to "no claim", never to a fabricated delta.
    /// </summary>
    public class StateReconciler
    {
        /// <summary>
        /// Locate the transcript file that contains a given thread's session_start.
        /// </summary>
        public static string? FindTranscriptForThread(string logDir, string threadId)
        {
            if (string.IsNullOrWhiteSpace(logDir) ||
                string.IsNullOrWhiteSpace(threadId) ||
                !Directory.Exists(logDir))
                return null;

            var files = Directory.GetFiles(logDir, "transcript_*.jsonl")
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTimeUtc);

            foreach (var file in files)
            {
                try
                {
                    foreach (var line in File.ReadLines(file.FullName))
                    {
                        if (line.Contains("session_start", StringComparison.Ordinal) &&
                            line.Contains(threadId, StringComparison.OrdinalIgnoreCase))
                            return file.FullName;
                    }
                }
                catch { /* Unreadable file — keep scanning older ones */ }
            }

            return null;
        }

        /// <summary>
        /// Return only the JSONL lines belonging to one session inside a shared
        /// transcript file: from its session_start up to the next session_start.
        /// </summary>
        public static List<string> ReadSessionLines(string transcriptPath, string threadId = "")
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(transcriptPath) || !File.Exists(transcriptPath))
                return result;

            bool inSession = string.IsNullOrEmpty(threadId);
            foreach (var line in File.ReadLines(transcriptPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                if (line.Contains("session_start", StringComparison.Ordinal))
                {
                    try
                    {
                        var obj = JObject.Parse(line);
                        if (obj["type"]?.ToString() == "session_start")
                        {
                            string sid = obj["session"]?.ToString() ?? "";
                            inSession = string.IsNullOrEmpty(threadId) ||
                                string.Equals(sid, threadId, StringComparison.OrdinalIgnoreCase);
                            continue;
                        }
                    }
                    catch (Newtonsoft.Json.JsonException)
                    {
                        // corrupt session_start line — skip, keep scanning
                    }
                }

                if (inSession)
                    result.Add(line);
            }

            return result;
        }

        // ★ ADR 0056 / Phase 3：以下方法委托到 Core——纯内存 JSONL 解析

        /// <summary>Extract layer names from in-memory transcript lines → Core.</summary>
        public static List<string> ExtractReferencedLayers(IEnumerable<string> transcriptLines)
            => CoreReconciliationRules.ExtractReferencedLayers(transcriptLines);

        /// <summary>Path-based overload: reads file then delegates to Core.</summary>
        public static List<string> ExtractReferencedLayers(string transcriptPath)
            => File.Exists(transcriptPath)
                ? CoreReconciliationRules.ExtractReferencedLayers(File.ReadLines(transcriptPath))
                : new List<string>();

        /// <summary>Extract tool chain from in-memory transcript lines → Core.</summary>
        public static List<string> ExtractToolChain(IEnumerable<string> transcriptLines)
            => CoreReconciliationRules.ExtractToolChain(transcriptLines);

        /// <summary>Path-based overload: reads file then delegates to Core.</summary>
        public static List<string> ExtractToolChain(string transcriptPath)
            => File.Exists(transcriptPath)
                ? CoreReconciliationRules.ExtractToolChain(File.ReadLines(transcriptPath))
                : new List<string>();

        /// <summary>
        /// Produce a reconciliation report comparing past session state
        /// against current project state.
        /// ★ Phase 3：diff 计算委托到 Core；本方法仅负责文件 I/O 编排。
        /// </summary>
        public static ReconciliationReport Reconcile(
            string? transcriptPath,
            string currentMapInfo,
            string threadId = "",
            IReadOnlyList<string>? pastLayers = null,
            IReadOnlyList<string>? pastTools = null)
        {
            List<string>? sessionLines = null;
            List<string> SessionLines() =>
                sessionLines ??= transcriptPath != null
                    ? ReadSessionLines(transcriptPath, threadId)
                    : new List<string>();

            var pastLayersList = pastLayers?.ToList()
                ?? CoreReconciliationRules.ExtractReferencedLayers(SessionLines());
            var pastToolsList = pastTools?.ToList()
                ?? CoreReconciliationRules.ExtractToolChain(SessionLines());

            var currentLayers = new HashSet<string>(
                CoreReconciliationRules.ParseLayerNames(currentMapInfo),
                StringComparer.OrdinalIgnoreCase);

            var (stillPresent, missing, newLayers, needsAttention) =
                CoreReconciliationRules.ComputeDiff(pastLayersList, currentLayers);

            return new ReconciliationReport
            {
                PastThreadId = !string.IsNullOrWhiteSpace(threadId)
                    ? threadId
                    : Path.GetFileNameWithoutExtension(transcriptPath ?? "")?.Replace("transcript_", "") ?? "",
                PastToolChain = string.Join(" → ", pastToolsList.Distinct(StringComparer.OrdinalIgnoreCase)),
                PastLayerCount = pastLayersList.Count,
                LayersStillPresent = stillPresent,
                LayersMissing = missing,
                LayersNew = newLayers,
                NeedsAttention = needsAttention
            };
        }
    }
}
