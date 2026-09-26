using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using SpatialAgent.Runtime.Contracts;
using SpatialAgent.Core;

namespace SpatialAgent.Services
{
    /// <summary>
    /// Local project memory store backed by <project>/.agent/memory.json.
    /// Implements IProjectMemoryService for consumption by the Runtime layer.
    /// </summary>
    public class ProjectMemoryStore : IProjectMemoryService, IProjectLineageStore
    {
        private readonly object _lock = new();
        private readonly string _projectFolder;
        private readonly string _memoryFolder;
        private readonly string _memoryPath;
        private ProjectMemory _memory;
        private string? _lastLoadWarning;

        public string MemoryPath => _memoryPath;
        public string? LastLoadWarning => _lastLoadWarning;

        public ProjectMemoryStore(string projectFolder)
        {
            _projectFolder = projectFolder;
            _memoryFolder = Path.Combine(projectFolder, ".agent");
            _memoryPath = Path.Combine(_memoryFolder, "memory.json");
            _memory = Load();
        }

        public string GetSummary()
        {
            lock (_lock)
            {
                var lines = new List<string>();

                if (!string.IsNullOrWhiteSpace(_lastLoadWarning))
                {
                    lines.Add("记忆状态：");
                    lines.Add(_lastLoadWarning);
                }

                var w5h2 = BuildW5H2Summary(_memory.W5H2);
                if (!string.IsNullOrWhiteSpace(w5h2))
                {
                    lines.Add("项目记忆：");
                    lines.Add(w5h2);
                }

                if (_memory.Preferences.Count > 0)
                {
                    lines.Add("用户偏好：");
                    lines.AddRange(_memory.Preferences
                        .OrderBy(kv => kv.Key)
                        .Select(kv => $"- {kv.Key}: {kv.Value}"));
                }

                if (_memory.LearnedFacts.Count > 0)
                {
                    lines.Add("已学事实：");
                    lines.AddRange(_memory.LearnedFacts.TakeLast(5).Select(f => $"- {f}"));
                }

                if (_memory.Corrections.Count > 0)
                {
                    lines.Add("历史纠正：");
                    lines.AddRange(_memory.Corrections.TakeLast(3).Select(c =>
                        $"- {c.Context}: {c.Wrong} -> {c.Correct}"));
                }

                if (_memory.SessionSummaries.Count > 0)
                {
                    lines.Add("最近会话：");
                    lines.AddRange(_memory.SessionSummaries.TakeLast(5).Select(s =>
                    {
                        var issues = s.PendingIssues.Count > 0
                            ? $"；待办：{string.Join("；", s.PendingIssues.Take(3))}"
                            : "";
                        return $"- {s.Date}: {s.WhatWasDone}{issues}";
                    }));
                }

                return string.Join("\n", lines.Where(line => !string.IsNullOrWhiteSpace(line)));
            }
        }

        public void RecordSessionSummary(SessionSummary summary)
        {
            if (summary == null) return;

            lock (_lock)
            {
                summary.Date = string.IsNullOrWhiteSpace(summary.Date)
                    ? DateTime.Now.ToString("yyyy-MM-dd")
                    : summary.Date;

                if (summary.KeyDecisions == null)
                    summary.KeyDecisions = new List<string>();
                if (summary.PendingIssues == null)
                    summary.PendingIssues = new List<string>();

                summary.WhatWasDone = NormalizeProgressSummary(summary.WhatWasDone);
                int sessionIndex = !string.IsNullOrWhiteSpace(summary.ThreadId)
                    ? _memory.SessionSummaries.FindLastIndex(existing =>
                        existing.ThreadId.Equals(
                            summary.ThreadId,
                            StringComparison.OrdinalIgnoreCase))
                    : IsProgressSummary(summary.WhatWasDone)
                        ? _memory.SessionSummaries.FindLastIndex(existing =>
                            string.Equals(existing.User, summary.User, StringComparison.OrdinalIgnoreCase)
                            && IsProgressSummary(NormalizeProgressSummary(existing.WhatWasDone)))
                        : -1;
                if (sessionIndex >= 0)
                {
                    var existing = _memory.SessionSummaries[sessionIndex];
                    if (existing.IsTerminal && !summary.IsTerminal)
                        return;
                    if (summary.Revision < existing.Revision)
                        return;
                    _memory.SessionSummaries[sessionIndex] = summary;
                }
                else
                    _memory.SessionSummaries.Add(summary);
                while (_memory.SessionSummaries.Count > 5)
                    _memory.SessionSummaries.RemoveAt(0);

                _memory.UpdatedAt = DateTime.Now.ToString("o");
                SaveLocked();
            }
        }

        public bool HasHistory =>
            _memory.SessionSummaries.Count > 0
            || !string.IsNullOrWhiteSpace(BuildW5H2Summary(_memory.W5H2));

        public string GetW5H2Context() => BuildW5H2Summary(_memory.W5H2);

        public IReadOnlyList<DataLineageRecord> LoadLineage()
        {
            lock (_lock)
            {
                return _memory.DataLineage
                    .Select(item => new DataLineageRecord
                    {
                        Output = item.Output,
                        Parent = item.Parent,
                        Inputs = item.Inputs.Select(input =>
                            new DataLineageInput
                            {
                                Resource = input.Resource,
                                Role = input.Role
                            }).ToList()
                    })
                    .ToList()
                    .AsReadOnly();
            }
        }

        public void UpsertLineage(DataLineageRecord lineage)
        {
            UpsertLineage(new[] { lineage });
        }

        public void UpsertLineage(IEnumerable<DataLineageRecord> lineage)
        {
            if (lineage == null)
                return;
            lock (_lock)
            {
                bool changed = false;
                foreach (var item in lineage.Where(item =>
                             item != null
                             && !string.IsNullOrWhiteSpace(item.Output)
                             && !string.IsNullOrWhiteSpace(item.Parent)))
                {
                    int index = _memory.DataLineage.FindIndex(existing =>
                        existing.Output.Equals(
                            item.Output,
                            StringComparison.OrdinalIgnoreCase));
                    if (index >= 0)
                        _memory.DataLineage[index] = item;
                    else
                        _memory.DataLineage.Add(item);
                    changed = true;
                }
                if (!changed)
                    return;
                _memory.UpdatedAt = DateTime.Now.ToString("o");
                SaveLocked();
            }
        }

        public void SetW5H2Context(string w5h2)
        {
            lock (_lock)
            {
                _memory.W5H2 ??= new W5H2Confirmed();
                _memory.W5H2.What = new ConfirmedFact
                {
                    Value = w5h2,
                    ConfirmedAt = DateTime.Now.ToString("o"),
                    Source = "agent"
                };
                _memory.UpdatedAt = DateTime.Now.ToString("o");
                SaveLocked();
            }
        }

        private ProjectMemory Load()
        {
            try
            {
                Directory.CreateDirectory(_memoryFolder);
                if (!File.Exists(_memoryPath))
                    return CreateDefaultMemory();

                var memory = JsonConvert.DeserializeObject<ProjectMemory>(File.ReadAllText(_memoryPath));
                if (memory == null)
                    throw new JsonException("memory.json is empty or invalid");

                return NormalizeMemory(memory);
            }
            catch (Exception ex)
            {
                AgentDiagnostics.Warn("ProjectMemoryStore.Load", "Project memory load failed", ex);
                string? quarantinePath = QuarantineCorruptMemory(ex);
                var recovered = TryLoadBackupMemory();
                if (recovered != null)
                {
                    _lastLoadWarning = quarantinePath == null
                        ? "memory.json 损坏，已从备份恢复。"
                        : $"memory.json 损坏，已隔离到 {Path.GetFileName(quarantinePath)}，并从备份恢复。";
                    Save(recovered);
                    return recovered;
                }

                _lastLoadWarning = quarantinePath == null
                    ? "memory.json 损坏，未找到可恢复备份，已创建新的项目记忆。"
                    : $"memory.json 损坏，已隔离到 {Path.GetFileName(quarantinePath)}，未找到可恢复备份，已创建新的项目记忆。";
                return CreateDefaultMemory();
            }
        }

        private ProjectMemory NormalizeMemory(ProjectMemory memory)
        {
            memory.ProjectPath = string.IsNullOrWhiteSpace(memory.ProjectPath) ? _projectFolder : memory.ProjectPath;
            memory.ProjectId = string.IsNullOrWhiteSpace(memory.ProjectId) ? BuildProjectId(_projectFolder) : memory.ProjectId;
            memory.CreatedAt = string.IsNullOrWhiteSpace(memory.CreatedAt) ? DateTime.Now.ToString("o") : memory.CreatedAt;
            memory.UpdatedAt = string.IsNullOrWhiteSpace(memory.UpdatedAt) ? memory.CreatedAt : memory.UpdatedAt;
            memory.W5H2 ??= new W5H2Confirmed();
            memory.Corrections ??= new List<CorrectionEntry>();
            memory.Preferences ??= new Dictionary<string, string>();
            memory.LearnedFacts ??= new List<string>();
            memory.SessionSummaries ??= new List<SessionSummary>();
            memory.DataLineage ??= new List<DataLineageRecord>();
            foreach (var lineage in memory.DataLineage)
            {
                lineage.Inputs ??= new List<DataLineageInput>();
                if (lineage.Inputs.Count == 0
                    && !string.IsNullOrWhiteSpace(lineage.Parent))
                {
                    lineage.Inputs.Add(new DataLineageInput
                    {
                        Resource = lineage.Parent,
                        Role = DataLineageInputRole.Primary
                    });
                }
            }
            foreach (var summary in memory.SessionSummaries)
            {
                summary.WhatWasDone = NormalizeProgressSummary(summary.WhatWasDone);
                summary.KeyDecisions ??= new List<string>();
                summary.PendingIssues ??= new List<string>();
                summary.TerminalStatus ??= "";
                summary.TerminationReason ??= "";
                summary.SessionLeaseId ??= "";
            }
            memory.SessionSummaries = CollapseProgressSnapshots(
                memory.SessionSummaries);
            return memory;
        }

        private static List<SessionSummary> CollapseProgressSnapshots(
            IReadOnlyList<SessionSummary> summaries)
        {
            var result = new List<SessionSummary>();
            foreach (var summary in summaries)
            {
                if (!IsProgressSummary(summary.WhatWasDone))
                {
                    result.Add(summary);
                    continue;
                }

                int existing = result.FindLastIndex(item =>
                    (!string.IsNullOrWhiteSpace(summary.ThreadId)
                        ? item.ThreadId.Equals(
                            summary.ThreadId,
                            StringComparison.OrdinalIgnoreCase)
                        : string.Equals(
                            item.User,
                            summary.User,
                            StringComparison.OrdinalIgnoreCase))
                    && IsProgressSummary(item.WhatWasDone));
                if (existing >= 0)
                    result[existing] = summary;
                else
                    result.Add(summary);
            }
            return result;
        }

        private static bool IsProgressSummary(string? text) =>
            !string.IsNullOrWhiteSpace(text)
            && text.StartsWith("进行中：", StringComparison.Ordinal);

        private static string NormalizeProgressSummary(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";
            if (IsProgressSummary(text))
                return text;

            // Repair legacy snapshots written with a mojibake literal. The
            // stable suffix and two counters let us recover without depending
            // on the exact corrupted byte sequence.
            if (text.Contains("次工具调用", StringComparison.Ordinal))
            {
                var numbers = Regex.Matches(text, @"\d+")
                    .Select(match => match.Value)
                    .ToList();
                if (numbers.Count >= 2)
                    return $"进行中：{numbers[0]} 轮交互，{numbers[1]} 次工具调用";
            }

            return text;
        }

        private ProjectMemory? TryLoadBackupMemory()
        {
            string backupPath = _memoryPath + ".bak";
            if (!File.Exists(backupPath))
                return null;

            try
            {
                var memory = JsonConvert.DeserializeObject<ProjectMemory>(File.ReadAllText(backupPath));
                return memory == null ? null : NormalizeMemory(memory);
            }
            catch
            {
                return null;
            }
        }

        private string? QuarantineCorruptMemory(Exception ex)
        {
            try
            {
                if (!File.Exists(_memoryPath))
                    return null;

                string stamp = $"{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}"[..24];
                string corruptPath = Path.Combine(_memoryFolder, $"memory.corrupt_{stamp}.json");
                try
                {
                    File.Move(_memoryPath, corruptPath);
                }
                catch
                {
                    File.Copy(_memoryPath, corruptPath, overwrite: false);
                    File.Delete(_memoryPath);
                }

                File.WriteAllText(
                    Path.Combine(_memoryFolder, $"memory.corrupt_{stamp}.txt"),
                    $"memory.json load failed at {DateTime.Now:o}\n{ex.GetType().Name}: {ex.Message}",
                    Encoding.UTF8);
                return corruptPath;
            }
            catch
            {
                return null;
            }
        }

        private ProjectMemory CreateDefaultMemory()
        {
            var now = DateTime.Now.ToString("o");
            var memory = new ProjectMemory
            {
                ProjectId = BuildProjectId(_projectFolder),
                ProjectPath = _projectFolder,
                CreatedAt = now,
                UpdatedAt = now
            };

            Save(memory);
            return memory;
        }

        private void SaveLocked() => Save(_memory);

        private void Save(ProjectMemory memory)
        {
            Directory.CreateDirectory(_memoryFolder);
            string tempPath = Path.Combine(_memoryFolder, $"memory.{Guid.NewGuid():N}.tmp");
            string backupPath = _memoryPath + ".bak";

            try
            {
                string json = JsonConvert.SerializeObject(memory, Formatting.Indented);
                File.WriteAllText(tempPath, json, Encoding.UTF8);

                if (File.Exists(_memoryPath))
                    File.Replace(tempPath, _memoryPath, backupPath, ignoreMetadataErrors: true);
                else
                    File.Move(tempPath, _memoryPath);
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch { }
            }
        }

        private static string BuildProjectId(string projectFolder)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(projectFolder ?? ""));
            return Convert.ToHexString(hash)[..12].ToLowerInvariant();
        }

        private static string BuildW5H2Summary(W5H2Confirmed w5h2)
        {
            var parts = new List<string>();

            void AddFact(string label, ConfirmedFact? fact)
            {
                if (fact == null || string.IsNullOrWhiteSpace(fact.Value))
                    return;

                parts.Add($"- {label}: {fact.Value}");
            }

            AddFact("为什么", w5h2.Why);
            AddFact("做什么", w5h2.What);
            AddFact("谁-行政层级", w5h2.WhoAdminLevel);

            if (!string.IsNullOrWhiteSpace(w5h2.WhoOrganization))
                parts.Add($"- 谁-组织: {w5h2.WhoOrganization}");
            if (!string.IsNullOrWhiteSpace(w5h2.WhereProvince))
                parts.Add($"- 省份: {w5h2.WhereProvince}");
            if (!string.IsNullOrWhiteSpace(w5h2.WhereCity))
                parts.Add($"- 城市: {w5h2.WhereCity}");

            AddFact("基准年", w5h2.WhenBaseYear);
            AddFact("目标年", w5h2.WhenTargetYear);
            AddFact("人口", w5h2.HowMuchPopulation);

            if (w5h2.HowMuchIndicators != null && w5h2.HowMuchIndicators.Count > 0)
            {
                parts.Add("- 指标：");
                parts.AddRange(w5h2.HowMuchIndicators
                    .OrderBy(kv => kv.Key)
                    .Select(kv => $"  - {kv.Key}: {kv.Value}"));
            }

            return string.Join("\n", parts);
        }
    }
}
