using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Services
{
    public sealed class CheckpointRetentionPolicy
    {
        public int MaxCheckpointsPerThread { get; init; } = 100;
        public TimeSpan MaxAge { get; init; } = TimeSpan.FromDays(60);
        public long MaxTotalBytes { get; init; } = 250L * 1024 * 1024;
        public TimeSpan OrphanArtifactMaxAge { get; init; } = TimeSpan.FromDays(7);
    }

    public sealed class FileRunCheckpointStore :
        IRunCheckpointStore,
        ICriticalRunCheckpointStore
    {
        private readonly object _writeLock = new();
        private readonly string _checkpointRoot;
        private readonly CheckpointRetentionPolicy _retention;
        private readonly IEventBus? _eventBus;
        private readonly JsonSerializerSettings _settings = new()
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore
        };

        public FileRunCheckpointStore(
            string projectFolder,
            CheckpointRetentionPolicy? retention = null,
            IEventBus? eventBus = null)
        {
            _checkpointRoot = Path.Combine(projectFolder, ".agent", "checkpoints");
            _retention = retention ?? new CheckpointRetentionPolicy();
            _eventBus = eventBus;
        }

        public Task SaveAsync(
            AgentRunState state,
            CancellationToken cancellation = default)
        {
            if (state == null || string.IsNullOrWhiteSpace(state.ThreadId))
                return Task.CompletedTask;
            cancellation.ThrowIfCancellationRequested();
            SaveCore(state, runRetention: true);
            return Task.CompletedTask;
        }

        public void SaveCritical(AgentRunState state)
        {
            if (state == null || string.IsNullOrWhiteSpace(state.ThreadId))
                return;
            SaveCore(state, runRetention: false);
        }

        private void SaveCore(AgentRunState state, bool runRetention)
        {
            string threadDir = Path.Combine(
                _checkpointRoot,
                Sanitize(state.ThreadId));
            string turnName =
                $"turn_{state.Turn:0000}_round_{state.Round:00}.json";
            string finalPath = Path.Combine(threadDir, turnName);
            state.SavedAt = DateTime.UtcNow;
            string checkpointJson = JsonConvert.SerializeObject(state, _settings);
            string latestJson = JsonConvert.SerializeObject(new
            {
                threadId = state.ThreadId,
                turn = state.Turn,
                round = state.Round,
                path = finalPath,
                savedAt = state.SavedAt
            }, _settings);

            lock (_writeLock)
            {
                Directory.CreateDirectory(threadDir);
                AtomicWrite(finalPath, checkpointJson);
                AtomicWrite(Path.Combine(threadDir, "latest.json"), latestJson);
                if (runRetention)
                    Prune();
            }
        }

        private static void AtomicWrite(string finalPath, string content)
        {
            string tempPath = finalPath + $".{Guid.NewGuid():N}.tmp";
            string backupPath = finalPath + ".bak";
            try
            {
                File.WriteAllText(tempPath, content, Encoding.UTF8);
                if (File.Exists(finalPath))
                    File.Replace(
                        tempPath,
                        finalPath,
                        backupPath,
                        ignoreMetadataErrors: true);
                else
                    File.Move(tempPath, finalPath);
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch
                {
                }
            }
        }

        public async Task<AgentRunState?> LoadLatestAsync(string threadId, CancellationToken cancellation = default)
        {
            if (string.IsNullOrWhiteSpace(threadId))
                return null;

            string threadDir = Path.Combine(_checkpointRoot, Sanitize(threadId));
            if (!Directory.Exists(threadDir))
                return null;

            string? attemptedIndexedPath = null;
            string indexPath = Path.Combine(threadDir, "latest.json");
            if (File.Exists(indexPath))
            {
                try
                {
                    var index = JObject.Parse(
                        await File.ReadAllTextAsync(indexPath, cancellation));
                    string? indexedPath = (string?)index["path"];
                    if (!string.IsNullOrWhiteSpace(indexedPath)
                        && File.Exists(indexedPath))
                    {
                        attemptedIndexedPath = indexedPath;
                        var indexedState = await LoadFromPathAsync(
                            indexedPath,
                            cancellation);
                        if (indexedState != null)
                            return indexedState;
                    }
                }
                catch
                {
                    // Fall back to checkpoint scan. A damaged index must not
                    // make otherwise valid atomic checkpoints unreachable.
                    // 中文：退回检查点扫描。损坏的索引不应使本应有效的原子检查点无法到达。
                }
            }

            var files = Directory.GetFiles(threadDir, "turn_*.json", SearchOption.TopDirectoryOnly)
                .Where(path => !path.Equals(
                    attemptedIndexedPath,
                    StringComparison.OrdinalIgnoreCase))
                .Select(path => new FileInfo(path))
                .OrderBy(info => info.LastWriteTimeUtc)
                .Select(info => info.FullName)
                .ToList();

            if (files.Count == 0)
                return null;

            for (int index = files.Count - 1; index >= 0; index--)
            {
                var state = await LoadFromPathAsync(files[index], cancellation);
                if (state != null)
                    return state;
            }
            return null;
        }

        public async Task<AgentRunState?> LoadMostRecentAsync(CancellationToken cancellation = default)
        {
            if (!Directory.Exists(_checkpointRoot))
                return null;

            var files = Directory.GetFiles(_checkpointRoot, "turn_*.json", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .ToList();

            if (files.Count == 0)
                return null;

            foreach (var file in files)
            {
                var state = await LoadFromPathAsync(file.FullName, cancellation);
                if (state != null)
                    return state;
            }
            return null;
        }

        public async Task<AgentRunState?> LoadAsync(string threadId, int turn, CancellationToken cancellation = default)
        {
            if (string.IsNullOrWhiteSpace(threadId))
                return null;

            string threadDir = Path.Combine(_checkpointRoot, Sanitize(threadId));
            if (!Directory.Exists(threadDir))
                return null;

            var matches = Directory.GetFiles(threadDir, $"turn_{turn:0000}_round_*.json", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .OrderBy(info => info.LastWriteTimeUtc)
                .Select(info => info.FullName)
                .ToList();

            if (matches.Count == 0)
                return null;

            for (int index = matches.Count - 1; index >= 0; index--)
            {
                var state = await LoadFromPathAsync(matches[index], cancellation);
                if (state != null)
                    return state;
            }
            return null;
        }

        public Task DeleteAsync(string threadId, CancellationToken cancellation = default)
        {
            if (string.IsNullOrWhiteSpace(threadId))
                return Task.CompletedTask;

            string threadDir = Path.Combine(_checkpointRoot, Sanitize(threadId));
            if (Directory.Exists(threadDir))
                Directory.Delete(threadDir, recursive: true);

            return Task.CompletedTask;
        }

        private async Task<AgentRunState?> LoadFromPathAsync(
            string path,
            CancellationToken cancellation)
        {
            try
            {
                string json = await File.ReadAllTextAsync(path, cancellation);
                var root = JObject.Parse(json);
                int schemaVersion = (int?)root["SchemaVersion"]
                    ?? (int?)root["schemaVersion"]
                    ?? 0;
                if (schemaVersion > AgentRunState.CurrentSchemaVersion)
                {
                    PublishDiagnostic(
                        "checkpoint.unsupported_version",
                        path,
                        $"Checkpoint schema {schemaVersion} is newer than supported "
                        + $"{AgentRunState.CurrentSchemaVersion}.");
                    return null;
                }

                var state = root.ToObject<AgentRunState>();
                if (state == null)
                {
                    PublishDiagnostic(
                        "checkpoint.load_failed",
                        path,
                        "Checkpoint deserialized to null.");
                    return null;
                }

                NormalizePlan(state);
                if (schemaVersion < AgentRunState.CurrentSchemaVersion)
                {
                    Migrate(state, schemaVersion);
                    PublishDiagnostic(
                        "checkpoint.migrated",
                        path,
                        $"{schemaVersion}→{AgentRunState.CurrentSchemaVersion}");
                }
                return state;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                PublishDiagnostic(
                    "checkpoint.load_failed",
                    path,
                    $"{ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private static void Migrate(AgentRunState state, int sourceVersion)
        {
            state.SchemaVersion = AgentRunState.CurrentSchemaVersion;
            if (sourceVersion == 0)
                state.RuntimeVersion = "legacy";
            else if (string.IsNullOrWhiteSpace(state.RuntimeVersion))
                state.RuntimeVersion = "3.0";
            state.Messages ??= new List<Message>();
            state.StepResults ??= new List<StepExecutionRecord>();
            foreach (StepExecutionRecord result in state.StepResults)
            {
                result.ProvidedCapabilities ??= new List<string>();
                if (!string.IsNullOrWhiteSpace(result.ToolCallId))
                    continue;
                result.ToolCallId = state.Plan?.Steps
                    .FirstOrDefault(step => step.Id == result.StepId)
                    ?.ToolCall?.Id ?? "";
            }
            state.Observations ??= new List<ObservationRecord>();
            state.PendingTools ??= new List<string>();
            state.BlockedTools ??= new List<string>();
            state.BlockedCallSignatures ??= new List<string>();
            state.ToolExposure ??= new List<string>();
            state.Lineage ??= new List<DataLineageRecord>();
            state.Artifacts ??= new List<DomainArtifactRecord>();
            foreach (var lineage in state.Lineage)
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
            state.Ledger ??= new PlanLedger();
            if (state.GoalScope == null && state.Plan?.ResolvedScope != null)
                state.GoalScope = state.Plan.ResolvedScope;
            NormalizeGoalScope(state.GoalScope);
            NormalizeGoalScope(state.PreviousGoalScope);
            if (sourceVersion < 7 && state.PreviousGoalScope == null)
                state.PreviousGoalScope = state.GoalScope;
            if (state.GoalCoverage == null && state.Plan?.Coverage != null)
                state.GoalCoverage = state.Plan.Coverage;
            if (state.Plan?.DataRevision == null && state.DataRevision != null)
                state.Plan!.DataRevision = state.DataRevision;
        }

        private static void NormalizePlan(AgentRunState state)
        {
            if (state.Plan == null)
                return;

            state.Plan.Steps ??= new List<PlanStep>();
            foreach (PlanStep step in state.Plan.Steps)
            {
                step.DependsOn ??= new List<int>();
                step.RequiresSuccessfulSteps ??= new List<int>();
                step.ReadSet ??= new List<string>();
                step.WriteSet ??= new List<string>();
                step.Preconditions ??= new List<string>();
                step.Postconditions ??= new List<string>();
                step.ProvidedCapabilities ??= new List<string>();
                step.ProofRequirements ??=
                    new List<ParameterScopedProofRequirement>();
            }
            NormalizeGoalScope(state.Plan.ResolvedScope);
        }

        private static void NormalizeGoalScope(ResolvedGoalScope? scope)
        {
            if (scope == null)
                return;
            scope.DomainCategories ??= new List<string>();
            scope.ExplicitTargets ??= new List<string>();
            scope.ReferenceTargets ??= new List<string>();
            scope.UnresolvedTargets ??= new List<string>();
            scope.SatisfiedTargets ??= new List<string>();
            scope.SatisfiedProofObligations ??= new List<string>();
            scope.Requirements ??= new List<GoalRequirement>();
            scope.UnresolvedRequirements ??= new List<string>();
            scope.FieldPredicates ??= new List<GoalFieldPredicate>();
            scope.RequiredCapabilities ??= new List<string>();
            scope.Deliverables ??= new List<GoalDeliverable>();
            scope.ProofObligations ??= new List<GoalProofObligation>();
            scope.ResourceBindings ??= new List<GoalResourceBinding>();
            foreach (GoalResourceBinding binding in scope.ResourceBindings)
            {
                binding.ResolvedTargets ??= new List<string>();
                binding.Candidates ??= new List<GoalResourceCandidate>();
                binding.EvidenceBackedCandidateIds ??= new List<string>();
                foreach (GoalResourceCandidate candidate in binding.Candidates)
                    candidate.Fields ??= new List<string>();
            }
            scope.ResourceHandles ??= new List<GoalResourceHandle>();
        }

        private void Prune()
        {
            if (!Directory.Exists(_checkpointRoot))
                return;

            try
            {
                int removed = 0;
                DateTime now = DateTime.UtcNow;
                foreach (string artifact in Directory.EnumerateFiles(
                             _checkpointRoot,
                             "*",
                             SearchOption.AllDirectories)
                         .Where(path => path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                             || path.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)))
                {
                    var info = new FileInfo(artifact);
                    if (now - info.LastWriteTimeUtc > _retention.OrphanArtifactMaxAge)
                    {
                        File.Delete(artifact);
                        removed++;
                    }
                }

                var checkpoints = Directory.EnumerateFiles(
                        _checkpointRoot,
                        "turn_*.json",
                        SearchOption.AllDirectories)
                    .Select(path => new FileInfo(path))
                    .ToList();
                var protectedLatest = checkpoints
                    .GroupBy(info => info.DirectoryName ?? "", StringComparer.OrdinalIgnoreCase)
                    .Select(group => group
                        .OrderByDescending(info => info.LastWriteTimeUtc)
                        .First().FullName)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var group in checkpoints.GroupBy(
                             info => info.DirectoryName ?? "",
                             StringComparer.OrdinalIgnoreCase))
                {
                    var ordered = group
                        .OrderByDescending(info => info.LastWriteTimeUtc)
                        .ToList();
                    foreach (var candidate in ordered.Skip(
                                 Math.Max(1, _retention.MaxCheckpointsPerThread)))
                    {
                        removed += DeleteCheckpoint(candidate, protectedLatest);
                    }
                }

                checkpoints = ExistingCheckpointFiles();
                foreach (var candidate in checkpoints
                             .Where(info => !protectedLatest.Contains(info.FullName))
                             .Where(info => now - info.LastWriteTimeUtc > _retention.MaxAge)
                             .OrderBy(info => info.LastWriteTimeUtc))
                {
                    removed += DeleteCheckpoint(candidate, protectedLatest);
                }

                checkpoints = ExistingCheckpointFiles();
                long totalBytes = checkpoints.Sum(info => info.Length);
                if (_retention.MaxTotalBytes > 0 && totalBytes > _retention.MaxTotalBytes)
                {
                    foreach (var candidate in checkpoints
                                 .Where(info => !protectedLatest.Contains(info.FullName))
                                 .OrderBy(info => info.LastWriteTimeUtc))
                    {
                        if (totalBytes <= _retention.MaxTotalBytes)
                            break;
                        long length = candidate.Length;
                        if (DeleteCheckpoint(candidate, protectedLatest) > 0)
                        {
                            totalBytes -= length;
                            removed++;
                        }
                    }
                }

                if (removed > 0)
                {
                    _eventBus?.Publish(new AgentEvent
                    {
                        EventType = "checkpoint.pruned",
                        Metadata = new()
                        {
                            ["removed"] = removed.ToString()
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                PublishDiagnostic(
                    "checkpoint.prune_failed",
                    _checkpointRoot,
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        private List<FileInfo> ExistingCheckpointFiles() =>
            Directory.EnumerateFiles(
                    _checkpointRoot,
                    "turn_*.json",
                    SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .Where(info => info.Exists)
                .ToList();

        private static int DeleteCheckpoint(
            FileInfo candidate,
            IReadOnlySet<string> protectedLatest)
        {
            if (!candidate.Exists || protectedLatest.Contains(candidate.FullName))
                return 0;
            candidate.Delete();
            string backup = candidate.FullName + ".bak";
            if (File.Exists(backup))
                File.Delete(backup);
            return 1;
        }

        private void PublishDiagnostic(string eventType, string path, string reason)
        {
            _eventBus?.Publish(new AgentEvent
            {
                EventType = eventType,
                Metadata = new()
                {
                    ["path"] = path,
                    ["reason"] = reason
                }
            });
        }

        private static string Sanitize(string value)
        {
            var chars = value.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_').ToArray();
            return new string(chars);
        }
    }
}
