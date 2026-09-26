using SpatialAgent.Runtime.Contracts;
using Newtonsoft.Json;

namespace SpatialAgent.Runtime.Planning
{
    /// <summary>
    /// Runs analysis-only specialists within explicit concurrency and timeout
    /// limits. Specialists receive snapshots and plans, never ToolRegistry or an
    /// ArcGIS executor, so they cannot mutate the project or bypass approval.
    /// </summary>
    public sealed class BoundedSubagentCoordinator : ISubagentCoordinator
    {
        private readonly IReadOnlyDictionary<SubagentRole, IReadOnlySubagent> _agents;

        public BoundedSubagentCoordinator(IEnumerable<IReadOnlySubagent>? agents = null)
        {
            _agents = (agents ?? CreateDefaults())
                .GroupBy(agent => agent.Role)
                .ToDictionary(group => group.Key, group => group.First());
        }

        public async Task<IReadOnlyList<SubagentResult>> RunAsync(
            IEnumerable<SubagentTask> tasks,
            int maxConcurrency,
            CancellationToken cancellation = default)
        {
            int concurrency = Math.Clamp(maxConcurrency, 1, 2);
            using var gate = new SemaphoreSlim(concurrency, concurrency);
            var work = tasks.Select(async task =>
            {
                await gate.WaitAsync(cancellation);
                try
                {
                    if (!_agents.TryGetValue(task.Role, out var agent))
                        return Failed(task, "No specialist is registered for this role.");

                    var isolatedTask = CloneForIsolation(task);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                    timeout.CancelAfter(task.Timeout);
                    try
                    {
                        return await agent.AnalyzeAsync(isolatedTask, timeout.Token);
                    }
                    catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                    {
                        return Failed(task, "Specialist timed out.");
                    }
                    catch (Exception ex)
                    {
                        return Failed(task, ex.Message);
                    }
                }
                finally
                {
                    gate.Release();
                }
            });

            return await Task.WhenAll(work);
        }

        private static SubagentTask CloneForIsolation(SubagentTask task)
        {
            // Reviewers receive detached data. Even a future third-party
            // reviewer that mutates its DTO cannot change the executable plan
            // or live DomainContext held by AgentRuntime.
            AgentPlan? plan = task.Plan == null
                ? null
                : JsonConvert.DeserializeObject<AgentPlan>(
                    JsonConvert.SerializeObject(task.Plan));
            DomainContext snapshot =
                JsonConvert.DeserializeObject<DomainContext>(
                    JsonConvert.SerializeObject(task.Snapshot))
                ?? new DomainContext();
            return new SubagentTask
            {
                Id = task.Id,
                Role = task.Role,
                Goal = task.Goal,
                Plan = plan,
                Snapshot = snapshot,
                TokenBudget = task.TokenBudget,
                Timeout = task.Timeout
            };
        }

        private static IEnumerable<IReadOnlySubagent> CreateDefaults() =>
            new IReadOnlySubagent[]
            {
                new SafetyReviewSubagent(),
                new VerificationReviewSubagent(),
                new ContextAnalysisSubagent()
            };

        private static SubagentResult Failed(SubagentTask task, string message) => new()
        {
            TaskId = task.Id,
            Role = task.Role,
            Success = false,
            Summary = message,
            Confidence = EvidenceConfidence.Low,
            Findings = new() { message },
            StructuredFindings = new()
            {
                Finding(
                    "reviewer_failed",
                    ReviewFindingSeverity.Critical,
                    ReviewDisposition.Block,
                    message)
            }
        };

        internal static ReviewFinding Finding(
            string code,
            ReviewFindingSeverity severity,
            ReviewDisposition disposition,
            string message,
            int? stepId = null,
            string evidence = "") => new()
        {
            Code = code,
            Severity = severity,
            Disposition = disposition,
            StepId = stepId,
            Message = message,
            Evidence = evidence
        };
    }

    internal sealed class SafetyReviewSubagent : IReadOnlySubagent
    {
        public SubagentRole Role => SubagentRole.SafetyReview;

        public Task<SubagentResult> AnalyzeAsync(
            SubagentTask task,
            CancellationToken cancellation = default)
        {
            cancellation.ThrowIfCancellationRequested();
            var findings = new List<string>();
            var structured = new List<ReviewFinding>();
            foreach (var step in task.Plan?.Steps ?? new List<PlanStep>())
            {
                if (step.Effect == null)
                {
                    findings.Add($"step:{step.Id}:missing_effect");
                    structured.Add(BoundedSubagentCoordinator.Finding(
                        "missing_effect",
                        ReviewFindingSeverity.Critical,
                        ReviewDisposition.Block,
                        "计划步骤缺少调用级影响描述。",
                        step.Id));
                    continue;
                }
                if (step.Effect.Confidence < EvidenceConfidence.Medium)
                {
                    findings.Add($"step:{step.Id}:low_effect_confidence");
                    structured.Add(BoundedSubagentCoordinator.Finding(
                        "low_effect_confidence",
                        ReviewFindingSeverity.Warning,
                        ReviewDisposition.LogOnly,
                        "调用级影响证据置信度较低；安全内核仍按较高风险执行。",
                        step.Id,
                        step.Effect.EvidenceSource));
                }
                if (step.Effect.Scope == EffectTargetScope.EntireDataset
                    && (step.Effect.Profile.Effects.HasFlag(DataEffectKind.UpdateRows)
                        || step.Effect.Profile.Effects.HasFlag(DataEffectKind.DeleteRows)))
                {
                    findings.Add($"step:{step.Id}:whole_dataset_write");
                    structured.Add(BoundedSubagentCoordinator.Finding(
                        "whole_dataset_write",
                        ReviewFindingSeverity.Warning,
                        ReviewDisposition.LogOnly,
                        "该步骤将写入整个数据集，需由确定性影响门和审批协议处理。",
                        step.Id,
                        step.Effect.Summary));
                }
                if (step.Effect.Profile.RequiresBackup
                    && !step.Preconditions.Contains("backup_before_write"))
                {
                    findings.Add($"step:{step.Id}:backup_precondition_missing");
                    structured.Add(BoundedSubagentCoordinator.Finding(
                        "backup_precondition_missing",
                        ReviewFindingSeverity.Critical,
                        ReviewDisposition.Block,
                        "需要备份的步骤没有声明执行前备份条件。",
                        step.Id));
                }
            }

            return Task.FromResult(new SubagentResult
            {
                TaskId = task.Id,
                Role = Role,
                Success = true,
                Summary = findings.Count == 0
                    ? "Safety review found no structural gaps."
                    : $"Safety review produced {findings.Count} finding(s).",
                Confidence = EvidenceConfidence.High,
                Findings = findings,
                StructuredFindings = structured
            });
        }
    }

    internal sealed class VerificationReviewSubagent : IReadOnlySubagent
    {
        public SubagentRole Role => SubagentRole.Verification;

        public Task<SubagentResult> AnalyzeAsync(
            SubagentTask task,
            CancellationToken cancellation = default)
        {
            cancellation.ThrowIfCancellationRequested();
            var affected = (task.Plan?.Steps ?? new List<PlanStep>())
                .Where(step => step.WriteSet.Count > 0 && step.Postconditions.Count == 0)
                .ToList();
            var findings = affected
                .Select(step => $"step:{step.Id}:write_without_postcondition")
                .ToList();
            var structured = affected
                .Select(step => BoundedSubagentCoordinator.Finding(
                    "write_without_postcondition",
                    ReviewFindingSeverity.Error,
                    ReviewDisposition.Recompile,
                    "写入步骤缺少可验证的执行后条件。",
                    step.Id))
                .ToList();
            return Task.FromResult(new SubagentResult
            {
                TaskId = task.Id,
                Role = Role,
                Success = true,
                Summary = findings.Count == 0
                    ? "All write steps have explicit postconditions."
                    : $"{findings.Count} write step(s) need verification evidence.",
                Confidence = EvidenceConfidence.High,
                Findings = findings,
                StructuredFindings = structured
            });
        }
    }

    internal sealed class ContextAnalysisSubagent : IReadOnlySubagent
    {
        public SubagentRole Role => SubagentRole.ContextAnalysis;

        public Task<SubagentResult> AnalyzeAsync(
            SubagentTask task,
            CancellationToken cancellation = default)
        {
            cancellation.ThrowIfCancellationRequested();
            var findings = new List<string>();
            var structured = new List<ReviewFinding>();
            if (string.IsNullOrWhiteSpace(task.Snapshot.Project.Path))
            {
                findings.Add("project_path_unknown");
                structured.Add(BoundedSubagentCoordinator.Finding(
                    "project_path_unknown",
                    ReviewFindingSeverity.Warning,
                    ReviewDisposition.LogOnly,
                    "当前项目路径未知。"));
            }
            if (string.IsNullOrWhiteSpace(task.Snapshot.Map.Name))
            {
                findings.Add("active_map_unknown");
                structured.Add(BoundedSubagentCoordinator.Finding(
                    "active_map_unknown",
                    ReviewFindingSeverity.Warning,
                    ReviewDisposition.LogOnly,
                    "当前活动地图未知。"));
            }
            if ((task.Plan?.Steps.Any(step => step.Effect?.UsesSelection == true) ?? false)
                && task.Snapshot.Selection.Count <= 0)
            {
                findings.Add("selection_scope_not_observed");
                structured.Add(BoundedSubagentCoordinator.Finding(
                    "selection_scope_not_observed",
                    ReviewFindingSeverity.Warning,
                    ReviewDisposition.LogOnly,
                    "计划依赖选择集，但快照中未观察到选择范围。"));
            }

            return Task.FromResult(new SubagentResult
            {
                TaskId = task.Id,
                Role = Role,
                Success = true,
                Summary = findings.Count == 0
                    ? "Required project context is present."
                    : $"{findings.Count} context gap(s) detected.",
                Confidence = EvidenceConfidence.Medium,
                Findings = findings,
                StructuredFindings = structured
            });
        }
    }
}
