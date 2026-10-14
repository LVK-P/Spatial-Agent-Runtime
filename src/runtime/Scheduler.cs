using System;
using System.Threading;
using Newtonsoft.Json.Linq;
using SpatialAgent.Runtime.Contracts;
using SpatialAgent.Runtime.Validation;
using SpatialAgent.Core;

namespace SpatialAgent.Runtime
{
    /// <summary>
    /// Scheduler — reads a plan, dispatches tools sequentially.
    /// Complexity lives in the modules it calls, not here.
    ///
    /// P0 hardening (2026-07-18):
    /// - Pre-execution parameter validation (ParameterValidator)
    /// - Per-step timeout control (ToolExecutionTimeoutPolicy)
    /// - CancellationToken propagation to IToolExecutor
    /// </summary>
    /// <remarks>中文：调度员——读取计划，顺序派遣工具。复杂性存在于它调用的模块中，而非这里。P0 加固（2026-07-18）：- 预执行参数验证（ParameterValidator）- 每步超时控制（ToolExecutionTimeoutPolicy）- 取消令牌向 IToolExecutor 传播</remarks>
    public class Scheduler : IScheduler
    {
        private readonly ToolRegistry _tools;
        private readonly IEventBus? _eventBus;
        private List<IValidationRule>? _validators;
        private ISafetyGate? _safety;
        private IWorkspaceService? _workspace;

        // ── P0 hardening: pre-execution validation ──
        // 中文：P0 硬化：执行前验证
        private ParameterValidator? _paramValidator;
        private ToolExecutionTimeoutPolicy? _timeoutPolicy;
        private TimeSpan _progressHeartbeatInterval = TimeSpan.FromSeconds(5);

        // ── Data influence quantity gate (ADR 0051) ──
        // 中文：数据影响量门（ADR 0051）
        private IDataInfluenceEstimator? _influenceEstimator;
        private IInvocationEffectResolver _effectResolver = new DefaultInvocationEffectResolver();
        private IInvocationSafetyKernel _safetyKernel = new DeterministicSafetyKernel();
        private DomainContext? _domainSnapshot;
        private IHostStateGuard? _hostStateGuard;

        /// <summary>Approval resumption flag — when true, AwaitingApproval plan steps
        /// are treated as Pending and quantity gate re-check is skipped (ADR 0051).</summary>
        /// <remarks>中文：审批恢复标志——当正确时，等待审批计划步骤被视为待处理，且跳过数量门重新检查（ADR 0051）。</remarks>
        public bool IsApprovalResumption { get; set; }

        /// <summary>
        /// ★ Phase C: tool names hard-blocked this turn (user rejected approval).
        /// Checked before execution; blocked steps get Status=Blocked with a
        /// descriptive reason. Tool-name granularity is transitional —
        /// future phases should refine to BlockedCallSignature (tool+layer+field).
        /// </summary>
        /// <remarks>中文：C阶段：本回合工具名称被硬卡住（用户拒绝批准）。执行前检查过;被阻挡的步骤会得到Status=Blocked，并有描述性原因。工具名称的细度性是过渡性的——未来阶段应细化为BlockedCallSignature（工具+图层+字段）。</remarks>
        public IReadOnlySet<string>? BlockedToolNames { get; set; }
        public IReadOnlySet<string>? BlockedCallSignatures { get; set; }
        public IReadOnlySet<string>? ApprovedTicketIds { get; set; }
        /// <summary>Batch Destructive approval: step IDs covered by the approved ticket.</summary>
        /// <remarks>中文：批次破坏性审批：批准工单涵盖的步骤编号。</remarks>
        public IReadOnlySet<int>? ApprovedStepIds { get; set; }
        /// <summary>
        /// Successfully settled semantic actions in this goal transaction.
        /// A replan may change step and provider call ids but must not execute
        /// the same action twice.
        /// </summary>
        /// <remarks>中文：在该目标事务中已成功完成的语义动作。重新规划可以更改步骤和提供方调用 ID，但不得重复执行同一动作。</remarks>
        public IReadOnlySet<string>? SatisfiedActionIds { get; set; }

        private bool IsStepApproved(PlanStep step, AgentPlan plan)
        {
            var ticket = step.PendingApproval;
            if (ticket == null || step.ToolCall == null)
                return false;

            bool decisionCoversStep =
                ApprovedTicketIds?.Contains(ticket.Id) == true
                || ApprovedStepIds?.Contains(step.Id) == true;
            if (!decisionCoversStep)
                return false;

            string canonical = _tools.ResolveName(step.ToolCall.Name) ?? step.ToolCall.Name;
            string currentSignature = ApprovalProtocol.ComputeCallSignature(canonical, step.ToolCall.Arguments);

            if (ticket.CoveredActionIds.Count > 0
                && !ticket.CoveredActionIds.Contains(
                    step.ActionId,
                    StringComparer.Ordinal))
                return false;

            if (ticket.Scope == ApprovalScope.ExactCall)
                return currentSignature.Equals(ticket.CallSignature, StringComparison.Ordinal);

            if (ticket.Scope != ApprovalScope.ExactBatch
                || !ticket.CoveredStepIds.Contains(step.Id)
                || !ticket.CoveredCallSignatures.Contains(currentSignature, StringComparer.Ordinal))
            {
                return false;
            }

            var currentBatchSignatures = plan.Steps
                .Where(s => ticket.CoveredStepIds.Contains(s.Id) && s.ToolCall != null)
                .Select(s =>
                {
                    string name = _tools.ResolveName(s.ToolCall!.Name) ?? s.ToolCall.Name;
                    return ApprovalProtocol.ComputeCallSignature(name, s.ToolCall.Arguments);
                })
                .ToList();
            return currentBatchSignatures.Count == ticket.CoveredCallSignatures.Count
                && ApprovalProtocol.ComputeBatchSignature(currentBatchSignatures)
                    .Equals(ticket.BatchSignature, StringComparison.Ordinal);
        }

        private static void BindTicketToAction(
            ApprovalTicket? ticket,
            PlanStep step)
        {
            if (ticket == null || string.IsNullOrWhiteSpace(step.ActionId))
                return;
            ticket.CoveredActionIds = new List<string> { step.ActionId };
            ticket.CoveredCalls = new List<ApprovalCoveredCall>
            {
                new()
                {
                    StepId = step.Id,
                    ActionId = step.ActionId,
                    ToolName = step.ToolCall?.Name ?? step.RequestedToolName,
                    CanonicalToolName = step.CanonicalToolName,
                    CallSignature = ticket.CallSignature,
                    Target = ticket.Target,
                    EffectProfile = step.Effect?.Profile.Id ?? "",
                    EffectSummary = step.Effect?.Summary ?? "",
                    Confidence = step.Effect?.Confidence
                        ?? EvidenceConfidence.Unknown
                }
            };
        }

        /// <summary>Current conversation turn — set by AgentRuntime before each turn.</summary>
        /// <remarks>中文：当前对话回合——由AgentRuntime在每回合前设置。</remarks>
        public int CurrentTurn { get; set; }

        /// <summary>Current user-granted permission for this session.</summary>
        /// <remarks>中文：本次会话当前用户授权。</remarks>
        public AgentPermissionLevel CurrentPermissionLevel { get; set; } = AgentPermissionLevel.RequestApproval;

        private readonly HashSet<string> _blockedCallSignaturesThisRun = new(StringComparer.Ordinal);

        public Scheduler(ToolRegistry tools, IEventBus? eventBus = null, List<IValidationRule>? validators = null, ISafetyGate? safety = null)
        {
            _tools = tools;
            _eventBus = eventBus;
            _validators = validators;
            _safety = safety;
        }

        /// <summary>Set safety gate after construction.</summary>
        /// <remarks>中文：施工后设置安全门。</remarks>
        public void SetSafety(ISafetyGate safety) => _safety = safety;

        /// <summary>Set workspace service after construction.</summary>
        /// <remarks>中文：施工后设置工作空间服务。</remarks>
        public void SetWorkspace(IWorkspaceService? workspace) => _workspace = workspace;

        /// <summary>Set pre-execution parameter validator (P0-2).</summary>
        /// <remarks>中文：设置预执行参数验证器（P0-2）。</remarks>
        public void SetParameterValidator(ParameterValidator? validator) => _paramValidator = validator;

        /// <summary>Set per-tool timeout policy (P0-3).</summary>
        /// <remarks>中文：设置每个工具的超时策略（P0-3）。</remarks>
        public void SetTimeoutPolicy(ToolExecutionTimeoutPolicy? policy) => _timeoutPolicy = policy;

        /// <summary>
        /// Controls elapsed-time heartbeats while a tool adapter is awaiting
        /// its host. Public for deterministic tests and host tuning.
        /// </summary>
        /// <remarks>中文：在工具适配器等待主机时控制经过时间的心跳。公开用于确定性测试和主机调优。</remarks>
        public void SetProgressHeartbeatInterval(TimeSpan interval) =>
            _progressHeartbeatInterval = interval;

        /// <summary>Set data influence estimator for quantity-sensitive safety gate (ADR 0051).</summary>
        /// <remarks>中文：设置数量敏感安全门（ADR 0051）的数据影响估计器。</remarks>
        public void SetDataInfluenceEstimator(IDataInfluenceEstimator? estimator) => _influenceEstimator = estimator;
        public void SetEffectResolver(IInvocationEffectResolver resolver) =>
            _effectResolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        public void SetSafetyKernel(IInvocationSafetyKernel kernel) =>
            _safetyKernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        public void SetDomainSnapshot(DomainContext? domain) => _domainSnapshot = domain;
        public void SetHostStateGuard(IHostStateGuard? guard) =>
            _hostStateGuard = guard;

        /// <summary>Invalidate influence estimator cache at turn start (ADR 0051).</summary>
        /// <remarks>中文：在回合开始时失效影响估计缓存（ADR 0051）。</remarks>
        public void InvalidateInfluenceCache() => _influenceEstimator?.InvalidateCache();

        /// <summary>Set validators after construction (for late binding scenarios).</summary>
        /// <remarks>中文：在构建后设置验证者（针对晚期绑定场景）。</remarks>
        public void SetValidators(List<IValidationRule> validators)
        {
            _validators = validators;
            // Propagate current turn to all validators
            // 中文：将当前的回合传播给所有验证者
            foreach (var v in validators)
                v.CurrentTurn = CurrentTurn;
        }

        public async Task<IReadOnlyList<StepExecutionResult>> RunAsync(AgentPlan plan, CancellationToken cancellation = default)
        {
            if (_validators != null)
                foreach (var v in _validators)
                    v.CurrentTurn = CurrentTurn;

            var results = new List<StepExecutionResult>();
            var batchSteps = plan.Steps
                .Where(step =>
                    step.ToolCall != null
                    && (step.Status == PlanStepStatus.Pending
                        || (IsApprovalResumption
                            && step.Status == PlanStepStatus.AwaitingApproval
                            && step.PendingApproval != null
                            && IsStepApproved(step, plan))))
                .ToList();

            foreach (PlanStep settled in batchSteps.Where(step =>
                         !string.IsNullOrWhiteSpace(step.ActionId)
                         && SatisfiedActionIds?.Contains(step.ActionId) == true))
            {
                settled.Status = PlanStepStatus.Completed;
                results.Add(new StepExecutionResult
                {
                    StepId = settled.Id,
                    ActionId = settled.ActionId,
                    ToolCallId = settled.ToolCall?.Id ?? "",
                    ToolName = settled.ToolCall?.Name ?? settled.CanonicalToolName,
                    Status = StepResultStatus.Completed,
                    Cause = StepOutcomeCause.None,
                    TransitionOutcome = StateTransitionOutcome.AlreadySatisfied,
                    ToolResult = new ToolResult
                    {
                        ToolName = settled.ToolCall?.Name ?? settled.CanonicalToolName,
                        Success = true,
                        Output = "semantic action already settled; duplicate execution suppressed"
                    },
                    InputParameters = settled.ToolCall == null
                        ? new Dictionary<string, object?>()
                        : new Dictionary<string, object?>(settled.ToolCall.Arguments),
                    Effect = settled.Effect,
                    ProvidedCapabilities = settled.ProvidedCapabilities.ToList()
                });
                _eventBus?.Publish(new AgentEvent
                {
                    EventType = "execution.action_already_satisfied",
                    Turn = CurrentTurn,
                    Metadata = new()
                    {
                        ["step"] = settled.Id.ToString(),
                        ["action_id"] = settled.ActionId
                    }
                });
            }

            // Resolve schema defaults and deterministic corrections before any
            // approval signature or effect assessment is created. The exact
            // invocation approved by the user must be the invocation executed.
            // 中文：在创建任何批准签名或效果评估之前，先解决模式默认和确定性修正。用户批准的具体调用必须是实际执行的调用。
            foreach (var candidate in plan.Steps.Where(s =>
                         s.ToolCall != null
                         && (s.Status == PlanStepStatus.Pending
                             || s.Status == PlanStepStatus.AwaitingApproval)))
            {
                string canonical = _tools.ResolveName(candidate.ToolCall!.Name)
                    ?? candidate.ToolCall.Name;
                var descriptor = _tools.GetDescriptor(canonical);
                var resolved = new Dictionary<string, object?>(
                    candidate.ToolCall.Arguments,
                    StringComparer.OrdinalIgnoreCase);
                if (descriptor?.DefaultParameters != null)
                {
                    foreach (var parameter in descriptor.DefaultParameters)
                        resolved.TryAdd(parameter.Key, parameter.Value);
                }
                candidate.ToolCall.Arguments = resolved;

                if (_paramValidator == null)
                    continue;
                var validation = _paramValidator.Validate(new ToolCall
                {
                    Id = candidate.ToolCall.Id,
                    Name = canonical,
                    Arguments = resolved
                });
                if (validation.Action == ValidationAction.Rejected)
                {
                    results.Add(new StepExecutionResult
                    {
                        StepId = candidate.Id,
                        ToolCallId = candidate.ToolCall.Id,
                        ToolName = candidate.ToolCall.Name,
                        Status = StepResultStatus.Failed,
                        Cause = StepOutcomeCause.ParameterValidation,
                        InputParameters = resolved,
                        ToolResult = new ToolResult
                        {
                            ToolName = candidate.ToolCall.Name,
                            Success = false,
                            Error = $"Parameter validation failed: {string.Join("; ", validation.Errors)}"
                        }
                    });
                    return FinalizeBatch(
                        batchSteps,
                        results,
                        settleUnreported: true,
                        "parameter validation stopped the batch");
                }
                if (validation.Action == ValidationAction.Corrected)
                    candidate.ToolCall.Arguments = validation.CorrectedArgs;
            }

            // ★ 审批恢复：跳过已完成/失败的步骤（避免重复执行）。
            // AwaitingApproval 步骤在恢复时被视为 Pending（用户已批准）。
            // English：Approval Recovery: Skips completed/failed steps (avoiding repeated execution). The AwaitingApproval step is considered Pending upon recovery.
            var pendingSteps = plan.Steps
                .Where(s => (s.Status == PlanStepStatus.Pending ||
                             (IsApprovalResumption
                              && s.Status == PlanStepStatus.AwaitingApproval
                              && s.PendingApproval != null
                              && IsStepApproved(s, plan)))
                       && s.ToolCall != null)
                .ToList();

            // ★ ADR 0055（2026-07-21）：审批恢复时，AwaitingApproval 步骤已被用户
            // 显式批准——预扫描不应重新评估它们。仅对新 Pending 步骤做安全评估。
            // 这拆除了"批准一个 Destructive → session 临时 FullAccess → 放行所有 Destructive"
            // 的会话级提权链路。每个 Destructive 步骤独立暂停。
            // English：ADR 0055 (2026-07-21): When approval is restored, the AwaitingApproval step has been explicitly approved by the user—pre-scans should not be re-evaluated. Only the safety assessment of the new Pending steps is performed. This removes the session-level privilege escalation chain that "approves a Destructive → session to → release all Destructives" for temporary FullAccess release. Each Destructive step is paused independently.
            var resumedStepIds = IsApprovalResumption
                ? plan.Steps.Where(s => s.Status == PlanStepStatus.AwaitingApproval
                                        && s.PendingApproval != null
                                        && IsStepApproved(s, plan))
                    .Select(s => s.Id).ToHashSet()
                : new HashSet<int>();

            var assessments = pendingSteps
                .Where(s => !resumedStepIds.Contains(s.Id))
                .Where(s =>
                {
                    // ★ 已在当前 Scheduler 实例中因备份失败等原因被阻止的调用签名，
                    // pre-scan 不再为其触发审批——阻止 LLM 反复提交同一危险操作。
                    // English：For call signatures blocked in the current Scheduler instance due to backup failures or other reasons, pre-scan no longer triggers approval—preventing the LLM from repeatedly committing the same dangerous operation.
                    string canon = _tools.ResolveName(s.ToolCall!.Name) ?? s.ToolCall.Name;
                    string sig = ApprovalProtocol.ComputeCallSignature(canon, s.ToolCall.Arguments);
                    return !_blockedCallSignaturesThisRun.Contains(sig)
                        && BlockedCallSignatures?.Contains(sig) != true;
                })
                .Select(s =>
                {
                    string canonical = _tools.ResolveName(s.ToolCall!.Name) ?? s.ToolCall.Name;
                    var descriptor = _tools.GetDescriptor(canonical);
                    var effect = _effectResolver.Resolve(descriptor, s.ToolCall, _domainSnapshot);
                    // Preserve the compiler/domain-pack static profile while
                    // refreshing invocation scope from the final normalized args.
                    // 中文：保留编译器/域包的静态配置文件，同时刷新最终归一化 arg 中的调用范围。
                    if (s.Effect?.Profile != null)
                        effect.Profile = s.Effect.Profile;
                    s.Effect = effect;
                    var catalogDecision = _safety?.Evaluate(canonical, CurrentPermissionLevel)
                        ?? new SafetyDecision
                        {
                            Kind = SafetyDecisionKind.Allow,
                            RiskLevel = ToolRiskLevel.Modify,
                            ToolName = canonical
                        };
                    return new
                    {
                        Step = s,
                        Effect = effect,
                        Decision = _safetyKernel.Evaluate(
                            effect,
                            catalogDecision,
                            CurrentPermissionLevel)
                    };
                })
                .ToList();

            // ★ 批量收集计划中所有 Destructive 步骤，合并为一个审批请求。
            // 用户一次确认全部放行——不再因逐个暂停 + LLM 重规划导致 5+4+3+2+1 的重复审批。
            // English：Batch collect all Destructive steps in the plan and merge them into a single approval request. Users confirm all releases in one go—no more repeated approvals of 5+4+3+2+1 due to individual pauses + LLM replanning.
            var destructiveAssessments = assessments
                .Where(x => x.Decision.Kind == SafetyDecisionKind.RequireApproval)
                .ToList();

            if (destructiveAssessments.Count > 0)
            {
                var coveredIds = destructiveAssessments.Select(a => a.Step.Id).ToList();
                string firstTool = destructiveAssessments[0].Step.ToolCall!.Name;
                string itemizedEffects = string.Join(Environment.NewLine,
                    destructiveAssessments.Select(a =>
                    {
                        string target = a.Effect.Target.Dataset;
                        if (string.IsNullOrWhiteSpace(target))
                            target = a.Effect.Target.Layer;
                        if (string.IsNullOrWhiteSpace(target))
                            target = a.Effect.Target.OutputTarget;
                        if (string.IsNullOrWhiteSpace(target))
                            target = "target unresolved";
                        if (!string.IsNullOrWhiteSpace(a.Effect.Target.Field))
                            target += "." + a.Effect.Target.Field;
                        return $"Step {a.Step.Id} · {a.Step.ToolCall!.Name} · {target} · {a.Effect.Summary}";
                    }));
                string summary = destructiveAssessments.Count == 1
                    ? (destructiveAssessments[0].Decision.Reason
                        ?? $"This step requires a destructive operation: {firstTool}")
                        + Environment.NewLine + itemizedEffects
                    : $"This exact batch contains {destructiveAssessments.Count} destructive operations. "
                        + "Every target will be backed up before execution."
                        + Environment.NewLine + itemizedEffects;

                var ticket = ApprovalProtocol.CreateTicket(
                    destructiveAssessments[0].Step.Id,
                    ApprovalKind.Destructive,
                    AgentPermissionLevel.FullAccess,
                    firstTool,
                    _tools.ResolveName(firstTool) ?? firstTool,
                    destructiveAssessments[0].Step.ToolCall!.Arguments,
                    new ApprovalEvidence
                    {
                        Summary = summary,
                        Source = "DeterministicSafetyKernel",
                        IsReliable = destructiveAssessments.All(a =>
                            a.Effect.Confidence >= EvidenceConfidence.Medium),
                        ReliabilityReason = string.Join("；",
                            destructiveAssessments.Select(a =>
                                $"{a.Step.Id}:{a.Effect.EvidenceSource}"))
                    },
                    target: destructiveAssessments[0].Effect.Target);
                ticket.CoveredStepIds = coveredIds;
                ticket.CoveredActionIds = destructiveAssessments
                    .Select(assessment => assessment.Step.ActionId)
                    .Where(actionId => !string.IsNullOrWhiteSpace(actionId))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                ticket.CoveredCallSignatures = destructiveAssessments
                    .Select(a =>
                    {
                        string canonical = _tools.ResolveName(a.Step.ToolCall!.Name) ?? a.Step.ToolCall.Name;
                        return ApprovalProtocol.ComputeCallSignature(canonical, a.Step.ToolCall.Arguments);
                    })
                    .ToList();
                ticket.CoveredCalls = destructiveAssessments
                    .Select(a =>
                    {
                        string canonical = _tools.ResolveName(a.Step.ToolCall!.Name)
                            ?? a.Step.ToolCall.Name;
                        string signature = ApprovalProtocol.ComputeCallSignature(
                            canonical,
                            a.Step.ToolCall.Arguments);
                        return new ApprovalCoveredCall
                        {
                            StepId = a.Step.Id,
                            ActionId = a.Step.ActionId,
                            ToolName = a.Step.ToolCall.Name,
                            CanonicalToolName = canonical,
                            CallSignature = signature,
                            Target = a.Effect.Target,
                            EffectProfile = a.Effect.Profile.Id,
                            EffectSummary = a.Effect.Summary,
                            Confidence = a.Effect.Confidence
                        };
                    })
                    .ToList();
                ticket.BatchSignature = ApprovalProtocol.ComputeBatchSignature(ticket.CoveredCallSignatures);
                ticket.Scope = destructiveAssessments.Count == 1
                    ? ApprovalScope.ExactCall
                    : ApprovalScope.ExactBatch;

                _eventBus?.Publish(new AgentEvent
                {
                    EventType = "safety.approval_required",
                    Turn = CurrentTurn,
                    Metadata = new()
                    {
                        ["tool"] = firstTool,
                        ["step"] = ticket.StepId.ToString(),
                        ["risk"] = destructiveAssessments[0].Decision.RiskLevel.ToString(),
                        ["required_permission"] = AgentPermissionLevel.FullAccess.ToString(),
                        ["status"] = StepResultStatus.AwaitingApproval.ToString(),
                        ["ticket_id"] = ticket.Id,
                        ["approval_kind"] = ApprovalKind.Destructive.ToString(),
                        ["call_signature"] = ticket.CallSignature ?? "",
                        ["batch_signature"] = ticket.BatchSignature,
                        ["covered_steps"] = string.Join(",", coveredIds)
                    },
                    Data = new()
                    {
                        ["reason"] = summary,
                        ["ticket"] = ticket
                    }
                });

                foreach (var assessment in destructiveAssessments)
                {
                    results.Add(new StepExecutionResult
                    {
                        StepId = assessment.Step.Id,
                        ActionId = assessment.Step.ActionId,
                        ToolName = assessment.Step.ToolCall!.Name,
                        Status = StepResultStatus.AwaitingApproval,
                        Cause = StepOutcomeCause.ApprovalRequired,
                        InputParameters = new Dictionary<string, object?>(assessment.Step.ToolCall.Arguments),
                        ApprovalReason = summary,
                        RequiredPermissionLevel = AgentPermissionLevel.FullAccess,
                        ApprovalKind = Contracts.ApprovalKind.Destructive,
                        ApprovalTicket = ticket,
                        Effect = assessment.Effect
                    });
                }
                return FinalizeBatch(
                    batchSteps,
                    results,
                    settleUnreported: false,
                    "batch is waiting for destructive-operation approval");
            }

            var blockedStep = assessments.FirstOrDefault(x => x.Decision.Kind == SafetyDecisionKind.Block);
            if (blockedStep != null)
            {
                var stepResult = new StepExecutionResult
                {
                    StepId = blockedStep.Step.Id,
                    ToolCallId = blockedStep.Step.ToolCall!.Id,
                    ToolName = blockedStep.Step.ToolCall!.Name,
                    Status = StepResultStatus.Blocked,
                    Cause = StepOutcomeCause.SafetyPolicy,
                    InputParameters = new Dictionary<string, object?>(blockedStep.Step.ToolCall.Arguments),
                    BlockReason = blockedStep.Decision.Reason,
                    Effect = blockedStep.Effect
                };

                _eventBus?.Publish(new AgentEvent
                {
                    EventType = "safety.blocked",
                    Turn = CurrentTurn,
                    Metadata = new()
                    {
                        ["tool"] = stepResult.ToolName,
                        ["step"] = stepResult.StepId.ToString(),
                        ["risk"] = blockedStep.Decision.RiskLevel.ToString(),
                        ["status"] = stepResult.Status.ToString()
                    },
                    Data = new()
                    {
                        ["args"] = blockedStep.Step.ToolCall.Arguments,
                        ["block_reason"] = stepResult.BlockReason
                    }
                });

                results.Add(stepResult);
                return FinalizeBatch(
                    batchSteps,
                    results,
                    settleUnreported: true,
                    stepResult.BlockReason ?? "safety policy stopped the batch");
            }

            foreach (var step in plan.Steps)
            {
                cancellation.ThrowIfCancellationRequested();
                if (step.ToolCall == null) continue;
                bool executableNow = step.Status == PlanStepStatus.Pending
                    || (IsApprovalResumption
                        && step.Status == PlanStepStatus.AwaitingApproval
                        && step.PendingApproval != null
                        && IsStepApproved(step, plan));
                if (!executableNow)
                    continue;

                string toolName = step.ToolCall.Name;
                string canonicalToolName = _tools.ResolveName(toolName) ?? toolName;
                var stepResult = new StepExecutionResult
                {
                    StepId = step.Id,
                    ToolCallId = step.ToolCall.Id,
                    ToolName = toolName,
                    InputParameters = new Dictionary<string, object?>(step.ToolCall.Arguments),
                    ToolResult = new ToolResult { ToolName = toolName, Success = false },
                    UserApproved = step.Status == PlanStepStatus.AwaitingApproval
                        && IsStepApproved(step, plan)
                        ? true
                        : null,
                    Effect = step.Effect
                };

                var unsatisfiedDependencies =
                    (step.RequiresSuccessfulSteps ?? Enumerable.Empty<int>())
                    .Distinct()
                    .Where(dependencyId =>
                    {
                        StepExecutionResult? dependencyResult =
                            results.LastOrDefault(result =>
                                result.StepId == dependencyId);
                        if (dependencyResult != null)
                            return dependencyResult.Status
                                != StepResultStatus.Completed;

                        PlanStep? dependencyStep = plan.Steps.FirstOrDefault(
                            candidate => candidate.Id == dependencyId);
                        return dependencyStep?.Status
                            != PlanStepStatus.Completed;
                    })
                    .ToList();
                if (unsatisfiedDependencies.Count > 0)
                {
                    string reason =
                        "not executed because prerequisite steps did not " +
                        $"complete: {string.Join(", ", unsatisfiedDependencies)}";
                    stepResult.Status = StepResultStatus.Skipped;
                    stepResult.Cause =
                        StepOutcomeCause.DependencyNotSatisfied;
                    stepResult.BlockReason = reason;
                    stepResult.ToolResult = new ToolResult
                    {
                        ToolName = toolName,
                        Success = false,
                        Error = reason
                    };
                    results.Add(stepResult);
                    continue;
                }

                // ── Safety: classify risk ──
                // 中文：安全：风险分类
                var riskLevel = _safety?.Classify(toolName) ?? ToolRiskLevel.Modify;
                var descriptor = _tools.GetDescriptor(canonicalToolName);
                var governance = descriptor is IToolGovernanceDescriptor governanceDescriptor
                    ? governanceDescriptor.Governance
                    : null;

                // ── ★ Phase B3: ResolveArgs — merge LLM raw args with tool DefaultParameters ──
                // Source labels distinguish LLM-provided values from tool defaults for auditability.
                // 中文：B3阶段：ResolveArgs — 将LLM原始Args与工具合并。DefaultParameters的来源标签用于区分LLM提供的值与工具默认值，以便可审计。
                var argSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var resolvedArgs = new Dictionary<string, object?>(step.ToolCall.Arguments, StringComparer.OrdinalIgnoreCase);
                foreach (var key in step.ToolCall.Arguments.Keys)
                    argSources[key] = "llm";

                if (descriptor?.DefaultParameters != null)
                {
                    foreach (var (key, value) in descriptor.DefaultParameters)
                    {
                        if (!resolvedArgs.ContainsKey(key))
                        {
                            resolvedArgs[key] = value;
                            argSources[key] = "tool_default";
                        }
                    }
                }
                step.ToolCall.Arguments = resolvedArgs;
                stepResult.InputParameters = new Dictionary<string, object?>(resolvedArgs);
                string currentCallSignature = ApprovalProtocol.ComputeCallSignature(canonicalToolName, resolvedArgs);

                // ── ★ Phase C: BlockedTools hard intercept ──
                // User rejected this tool earlier in the turn → block at Scheduler level.
                // This is a HARD block: the tool won't execute regardless of permission level.
                // Tool-name granularity is transitional (see ADR 0055 §9.2).
                // 中文：C阶段：BlockedTools 硬拦截 用户在调度器层面的阻挡→回合早期拒绝了该工具。这是一个硬性阻断：无论权限等级如何，工具都无法执行。工具名称的粒度是过渡性（参见ADR 0055 §9.2）。
                if (BlockedToolNames?.Contains(canonicalToolName) == true)
                {
                    stepResult.Status = StepResultStatus.Blocked;
                    stepResult.Cause = StepOutcomeCause.BlockedByToolPolicy;
                    stepResult.BlockReason = $"工具 \"{canonicalToolName}\" 已被本轮策略阻止，跳过执行。";
                    _eventBus?.Publish(new AgentEvent
                    {
                        EventType = "safety.blocked_by_user",
                        Turn = CurrentTurn,
                        Metadata = new()
                        {
                            ["tool"] = toolName,
                            ["canonical"] = canonicalToolName,
                            ["step"] = step.Id.ToString(),
                            ["status"] = stepResult.Status.ToString(),
                            ["call_signature"] = currentCallSignature,
                            ["cause"] = stepResult.Cause.ToString()
                        },
                        Data = new()
                        {
                            ["block_reason"] = stepResult.BlockReason
                        }
                    });
                    results.Add(stepResult);
                    continue;
                }

                if (BlockedCallSignatures?.Contains(currentCallSignature) == true
                    || _blockedCallSignaturesThisRun.Contains(currentCallSignature))
                {
                    stepResult.Status = StepResultStatus.Blocked;
                    stepResult.Cause = StepOutcomeCause.BlockedByCallSignature;
                    stepResult.BlockReason = $"调用签名已被阻止：{canonicalToolName}";
                    _eventBus?.Publish(new AgentEvent
                    {
                        EventType = "safety.blocked_by_user",
                        Turn = CurrentTurn,
                        Metadata = new()
                        {
                            ["tool"] = toolName,
                            ["canonical"] = canonicalToolName,
                            ["step"] = step.Id.ToString(),
                            ["status"] = stepResult.Status.ToString(),
                            ["call_signature"] = currentCallSignature
                        },
                        Data = new()
                        {
                            ["block_reason"] = stepResult.BlockReason
                        }
                    });
                    results.Add(stepResult);
                    continue;
                }

                // ★ P0-3 加强（2026-07-20）：工具名解析前后风险比对。
                // 场景：LLM 调用了 "conversion_Erase"（编造的），ToolNameResolver 通过
                // shortName 回退解析到了 "management_Erase"（真实的 Destructive 工具）。
                // SafetyGate 用原始名称分类 → 未知 → Destructive（修复后），但这里需要
                // 确认解析后的名称不会导致风险升级。
                // English：P0-3 Enhancement (2026-07-20): Risk comparison before and after tool name analysis. Scenario: The LLM calls "conversion_Erase" (fabricated), and ToolNameResolver parses "management_Erase" (the real Destructive tool) through shortName rollback. SafetyGate classifies by original names→ Unknown → Destructive (after fix), but here it is necessary to confirm that the resolved names do not cause risk escalation.
                if (!canonicalToolName.Equals(toolName, StringComparison.OrdinalIgnoreCase)
                    && !canonicalToolName.EndsWith(toolName, StringComparison.OrdinalIgnoreCase))
                {
                    var resolvedRisk = _safety?.Classify(canonicalToolName) ?? ToolRiskLevel.Modify;
                    if (resolvedRisk > riskLevel)
                    {
                        _eventBus?.Publish(new AgentEvent
                        {
                            EventType = "safety.risk_upgraded",
                            Turn = CurrentTurn,
                            Metadata = new()
                            {
                                ["original_tool"] = toolName,
                                ["resolved_tool"] = canonicalToolName,
                                ["original_risk"] = riskLevel.ToString(),
                                ["upgraded_risk"] = resolvedRisk.ToString()
                            }
                        });
                        riskLevel = resolvedRisk;
                    }
                }

                // ── P0-2: Pre-execution parameter validation ──
                // ★ ADR 0055（2026-07-21）：参数校验在数量门**之前**运行。
                // 若校验纠正了参数（如 field_name 大小写/alias），数量门必须消费
                // 修正后的参数——否则审批提示中的 layer/field 可能不等于最终执行参数，
                // 造成"批准依据"与"执行事实"脱节。
                // English：P0-2: Pre-execution parameter validation ADR 0055 (2026-07-21): Parameter validation runs **before the quantity gate**. If the validation corrects parameters (such as field_name case/alias), the quantity gate must consume the corrected parameters—otherwise, the layer/field in the approval prompt may not be the final execution parameter, causing a disconnect between the "approval basis" and the "execution fact."
                if (_paramValidator != null)
                {
                    var validation = _paramValidator.Validate(new ToolCall
                    {
                        Id = step.ToolCall.Id,
                        Name = canonicalToolName,
                        Arguments = step.ToolCall.Arguments
                    });
                    if (validation.Action == ValidationAction.Rejected)
                    {
                        stepResult.Status = StepResultStatus.Failed;
                        stepResult.ToolResult = new ToolResult
                        {
                            ToolName = toolName,
                            Success = false,
                            Error = $"参数校验失败: {string.Join("; ", validation.Errors)}"
                        };
                        _eventBus?.Publish(new AgentEvent
                        {
                            EventType = "tool.param_validation_rejected",
                            Turn = CurrentTurn,
                            Metadata = new()
                            {
                                ["tool"] = toolName,
                                ["step"] = step.Id.ToString(),
                                ["error_count"] = validation.Errors.Count.ToString()
                            },
                            Data = new()
                            {
                                ["errors"] = validation.Errors,
                                ["args"] = step.ToolCall.Arguments
                            }
                        });
                        results.Add(stepResult);
                        continue;
                    }

                    if (validation.Action == ValidationAction.Corrected)
                    {
                        // Apply corrected args — subsequent quantity gate sees corrected values
                        // 中文：应用修正 args ——后续的数量门看到修正值
                        step.ToolCall.Arguments = validation.CorrectedArgs;
                        stepResult.InputParameters = new Dictionary<string, object?>(validation.CorrectedArgs);
                        foreach (var correction in validation.Corrections)
                            stepResult.ToolResult!.AutofixApplied.Add(correction);

                        _eventBus?.Publish(new AgentEvent
                        {
                            EventType = "tool.param_autocorrected",
                            Turn = CurrentTurn,
                            Metadata = new()
                            {
                                ["tool"] = toolName,
                                ["step"] = step.Id.ToString(),
                                ["correction_count"] = validation.Corrections.Count.ToString()
                            },
                            Data = new()
                            {
                                ["corrections"] = validation.Corrections
                            }
                        });
                    }
                }

                // ── Quantity-sensitive safety gate (ADR 0051) ──
                // ★ 审批恢复（2026-07-21）：步骤状态为 AwaitingApproval → 用户已批准，
                // 跳过数量门二次检查——安全门+数量门是 AND 关系，审批是暂停继续而非重试。
                // ★ ADR 0055：参数校验已在前置运行——此处的 step.ToolCall.Arguments
                // 是 CorrectedArgs（如有纠正），评估对象 = 执行对象。
                // English：Quantity-sensitive safety gate (ADR 0051) approval restoration (2026-07-21): Step status is AwaitingApproval → User has approved, skipped the second check of the quantity gate — safety gate + quantity gate is an AND relationship, approval is paused to continue, not retested. ADR 0055: Parameter validation is running pre-running—step here. ToolCall.Arguments is a CorrectedArgs (if corrected), Evaluation Object = Execution Object.
                bool wasApproved = step.Status == PlanStepStatus.AwaitingApproval
                    && IsStepApproved(step, plan);

                if (!wasApproved
                    && (step.Effect?.Profile.RequiresInfluenceProbe == true
                        || ToolSafetySemantics.ShouldEvaluateInfluenceGate(descriptor, riskLevel))
                    && CurrentPermissionLevel == AgentPermissionLevel.RequestApproval)
                {
                    // ★ 设计原则（2026-07-24）：RequestApproval 模式下，数量门无法评估 Modify 影响
                    // → 交由用户审批（AwaitingApproval），不硬阻止（Blocked），不静默放行。
                    // _influenceEstimator 未注入与 EstimateAsync 返回 IsReliable=false 走同一条审批路径。
                    // English：Design Principles (2026-07-24): In the RequestApproval mode, the quantity gate cannot assess the impact of Modify→ is submitted to user approval (AwaitingApproval), not blocked by force, and not released silently. _influenceEstimator Not injected, but follows the same approval path as EstimateAsync returns IsReliable=false.
                    if (_influenceEstimator == null)
                    {
                        AgentDiagnostics.Warn("Scheduler.QuantityGate",
                            $"数量门已触发（工具={step.ToolCall.Name}），但 DataInfluenceEstimator 未注入——" +
                            "步骤暂停等待用户审批（fail-closed → AwaitingApproval）。");

                        string reason =
                            $"无法评估「{toolName}」的数据影响范围——影响估算器未初始化。\n" +
                            "系统无法自动判断此操作将覆写多少条记录。请人工确认操作的目标图层和字段后决定是否继续。";

                        stepResult.Status = StepResultStatus.AwaitingApproval;
                        stepResult.Cause = StepOutcomeCause.ApprovalRequired;
                        stepResult.ApprovalReason = reason;
                        stepResult.ApprovalKind = Contracts.ApprovalKind.DataInfluence;
                        stepResult.ApprovalTicket = ApprovalProtocol.CreateTicket(
                            step.Id,
                            ApprovalKind.DataInfluence,
                            null,
                            toolName,
                            canonicalToolName,
                            step.ToolCall.Arguments,
                            new ApprovalEvidence
                            {
                                Summary = reason,
                                Source = "EstimatorMissing",
                                IsReliable = false,
                                ReliabilityReason = "DataInfluenceEstimator 未注入——AgentRuntime.SetDataInfluenceEstimator() 未被调用或初始化失败。"
                            },
                            target: step.Effect?.Target);
                        BindTicketToAction(stepResult.ApprovalTicket, step);

                        _eventBus?.Publish(new AgentEvent
                        {
                            EventType = "safety.influence_approval_required",
                            Turn = CurrentTurn,
                            Metadata = new()
                            {
                                ["tool"] = toolName,
                                ["step"] = step.Id.ToString(),
                                ["risk"] = riskLevel.ToString(),
                                ["status"] = stepResult.Status.ToString(),
                                ["reason"] = "estimator_missing",
                                ["ticket_id"] = stepResult.ApprovalTicket?.Id ?? "",
                                ["approval_kind"] = stepResult.ApprovalKind?.ToString() ?? "",
                                ["call_signature"] = stepResult.ApprovalTicket?.CallSignature ?? "",
                                ["target_signature"] = stepResult.ApprovalTicket?.Target?.Signature ?? ""
                            },
                            Data = new()
                            {
                                ["args"] = step.ToolCall.Arguments,
                                ["reason"] = reason,
                                ["ticket"] = stepResult.ApprovalTicket
                            }
                        });
                        _eventBus?.Publish(new AgentEvent
                        {
                            EventType = "tool.progress",
                            Turn = CurrentTurn,
                            Metadata = new()
                            {
                                ["tool"] = toolName,
                                ["step"] = step.Id.ToString(),
                                ["phase"] = "approval"
                            },
                            Data = new()
                            {
                                ["message"] = "等待授权"
                            }
                        });

                        results.Add(stepResult);
                        return FinalizeBatch(
                            batchSteps,
                            results,
                            settleUnreported: false,
                            "batch is waiting for data-influence approval");
                    }
                    else
                    {
                        var estimate = await _influenceEstimator.EstimateAsync(
                            step.ToolCall,
                            new DataInfluenceContext
                            {
                                ToolName = toolName,
                                CanonicalToolName = canonicalToolName,
                                DisplayName = canonicalToolName,
                                SafetyProfile = descriptor is IToolSafetyDescriptor safetyDescriptor
                                    ? safetyDescriptor.SafetyProfile
                                    : null,
                                Governance = governance
                            });
                        ApplyInfluenceEvidence(step.Effect, estimate);

                    // ★ fail-closed（ADR 0051，2026-07-21）：评估不可靠 → 要求审批。
                    // 安全机制不能依赖不稳定来源——describe 失败/缓存缺失/解析异常时，
                    // 不得静默放行。默认假设最坏情况。
                    // English：fail-closed (ADR 0051, 2026-07-21): Unreliable assessment → Approval required. Security mechanisms cannot rely on sources of instability—when described, cache is missing, or parsed exceptions, silent release is not allowed. The default assumption is the worst-case scenario.
                    bool requiresApproval = !estimate.IsReliable
                        || estimate.EffectiveCount > RuntimePolicy.DataInfluenceThreshold;

                    if (estimate.FeatureCount >= 0 && requiresApproval
                        || !estimate.IsReliable)
                    {
                        string reason = estimate.IsReliable
                            ? $"{estimate}。\n超过安全阈值（{RuntimePolicy.DataInfluenceThreshold} 条），请确认后继续。"
                            : $"无法评估「{estimate.LayerName}」的数据影响——describe 数据不可用或图层不存在。\n为安全起见，请确认后继续。";

                        stepResult.Status = StepResultStatus.AwaitingApproval;
                        stepResult.Cause = StepOutcomeCause.ApprovalRequired;
                        stepResult.ApprovalReason = reason;
                        stepResult.ApprovalKind = Contracts.ApprovalKind.DataInfluence;
                        stepResult.ApprovalTicket = ApprovalProtocol.CreateTicket(
                            step.Id,
                            ApprovalKind.DataInfluence,
                            null,
                            toolName,
                            canonicalToolName,
                            step.ToolCall.Arguments,
                            new ApprovalEvidence
                            {
                                Summary = reason,
                                Source = estimate.Source.ToString(),
                                IsReliable = estimate.IsReliable,
                                ReliabilityReason = estimate.ReliabilityReason,
                                InfluenceCount = estimate.IsReliable ? estimate.EffectiveCount : null,
                                ScopeObjectCount = estimate.ScopeObjectCount >= 0 ? estimate.ScopeObjectCount : null,
                                TargetFieldCount = estimate.TargetFieldCount,
                                ScopeValueCount = estimate.ScopeValueCount >= 0 ? estimate.ScopeValueCount : null,
                                ExistingNonEmptyTargetCount = estimate.ExistingNonEmptyTargetCount >= 0
                                    ? estimate.ExistingNonEmptyTargetCount
                                    : null,
                                NewValueFillCount = estimate.NewValueFillCount >= 0 ? estimate.NewValueFillCount : null,
                                SchemaMutationCount = estimate.SchemaMutationCount >= 0 ? estimate.SchemaMutationCount : null,
                                UsesSelectionScope = estimate.UsesSelectionScope,
                                UsesFilteredScope = estimate.UsesFilteredScope,
                                ModifySubtype = estimate.ModifySubtype
                            },
                            target: step.Effect?.Target);
                        BindTicketToAction(stepResult.ApprovalTicket, step);
                        _eventBus?.Publish(new AgentEvent
                        {
                            EventType = "safety.influence_approval_required",
                            Turn = CurrentTurn,
                            Metadata = new()
                            {
                                ["tool"] = toolName,
                                ["step"] = step.Id.ToString(),
                                ["risk"] = riskLevel.ToString(),
                                ["status"] = stepResult.Status.ToString(),
                                ["influence_count"] = estimate.IsReliable ? estimate.EffectiveCount.ToString() : "?",
                                ["layer"] = estimate.LayerName,
                                ["field"] = estimate.FieldName ?? "?",
                                ["reliable"] = estimate.IsReliable.ToString().ToLowerInvariant(),
                                ["scope_object_count"] = estimate.ScopeObjectCount >= 0 ? estimate.ScopeObjectCount.ToString() : "?",
                                ["target_field_count"] = estimate.TargetFieldCount.ToString(),
                                ["scope_value_count"] = estimate.ScopeValueCount >= 0 ? estimate.ScopeValueCount.ToString() : "?",
                                ["existing_nonempty_target_count"] = estimate.ExistingNonEmptyTargetCount >= 0
                                    ? estimate.ExistingNonEmptyTargetCount.ToString()
                                    : "?",
                                ["new_value_fill_count"] = estimate.NewValueFillCount >= 0 ? estimate.NewValueFillCount.ToString() : "?",
                                ["schema_mutation_count"] = estimate.SchemaMutationCount >= 0 ? estimate.SchemaMutationCount.ToString() : "?",
                                ["uses_selection_scope"] = estimate.UsesSelectionScope.ToString().ToLowerInvariant(),
                                ["uses_filtered_scope"] = estimate.UsesFilteredScope.ToString().ToLowerInvariant(),
                                ["source"] = estimate.Source.ToString(),
                                ["modify_subtype"] = estimate.ModifySubtype.ToString(),
                                ["ticket_id"] = stepResult.ApprovalTicket?.Id ?? "",
                                ["approval_kind"] = stepResult.ApprovalKind?.ToString() ?? "",
                                ["call_signature"] = stepResult.ApprovalTicket?.CallSignature ?? "",
                                ["target_signature"] = stepResult.ApprovalTicket?.Target?.Signature ?? ""
                            },
                            Data = new()
                            {
                                ["args"] = step.ToolCall.Arguments,
                                ["reason"] = reason,
                                ["estimate"] = estimate.ToString(),
                                ["estimate_details"] = new
                                {
                                    estimate.ScopeObjectCount,
                                    estimate.TargetFieldCount,
                                    estimate.ScopeValueCount,
                                    estimate.ExistingNonEmptyTargetCount,
                                    estimate.NewValueFillCount,
                                    estimate.SchemaMutationCount,
                                    estimate.UsesSelectionScope,
                                    estimate.UsesFilteredScope,
                                    estimate.Source,
                                    estimate.ModifySubtype,
                                    estimate.ReliabilityReason
                                },
                                ["ticket"] = stepResult.ApprovalTicket
                            }
                        });
                        _eventBus?.Publish(new AgentEvent
                        {
                            EventType = "tool.progress",
                            Turn = CurrentTurn,
                            Metadata = new()
                            {
                                ["tool"] = toolName,
                                ["step"] = step.Id.ToString(),
                                ["phase"] = "approval"
                            },
                            Data = new()
                            {
                                ["message"] = "等待授权"
                            }
                        });
                        results.Add(stepResult);
                        return FinalizeBatch(
                            batchSteps,
                            results,
                            settleUnreported: false,
                            "batch is waiting for data-influence approval");
                    }
                    } // 否则 _influenceEstimator != null，进入数量门控评估。 / Otherwise evaluate the quantity gate.
                }

                // ── Execute ──
                // 中文：执行
                if (_hostStateGuard != null
                    && plan.DataRevision != null
                    && MutatesHost(step))
                {
                    HostStateGuardResult freshness =
                        await ValidateHostForStepAsync(
                            plan,
                            cancellation);
                    if (freshness.Decision is HostRevisionDecision.SelfCommitted
                        or HostRevisionDecision.Current)
                    {
                        // Adopt the live baseline so subsequent steps see the
                        // plan's own writes as expected host state.
                        // 中文：采用实时基线，以便后续步骤将计划自身写入视为预期的宿主状态。
                        plan.DataRevision = freshness.Current;
                        plan.ProjectRevision = freshness.Current.Fingerprint;
                    }
                    else if (!freshness.IsCurrent)
                    {
                        ApplyHostStateBlock(
                            step,
                            stepResult,
                            freshness,
                            plan.DataRevision.Fingerprint);
                        results.Add(stepResult);
                        // External / mixed / unreliable: stop the batch; do not
                        // treat this as a user rejection at settlement.
                        // 中文：外部/混合/不可靠：停止批量;不要把这当作结算时的用户拒绝。
                        return FinalizeBatch(
                            batchSteps,
                            results,
                            settleUnreported: true,
                            freshness.Reason);
                    }
                }

                // The optimistic-concurrency preflight above is the final
                // read-only boundary. Backup is deliberately after it because
                // backup GP may refresh project/catalog state or touch FileGDB
                // storage metadata. Such Runtime-owned infrastructure effects
                // must never be mistaken for a user edit made after planning.
                // 中文：上述乐观并发预检点是最终的只读边界。备份是刻意在它之后，因为备份GP可能会刷新项目/目录状态或触碰FileGDB存储元数据。此类运行时拥有的基础设施效应绝不能误认为是用户在规划后进行的编辑。
                if (riskLevel == ToolRiskLevel.Destructive)
                {
                    bool backupReady = await PrepareDestructiveBackupAsync(
                        step,
                        stepResult,
                        descriptor,
                        canonicalToolName,
                        riskLevel,
                        currentCallSignature,
                        cancellation);
                    if (!backupReady)
                    {
                        results.Add(stepResult);
                        continue;
                    }

                    if (_hostStateGuard != null
                        && stepResult.BackupPaths.Count > 0)
                    {
                        DataRevisionSnapshot beforeInfrastructure =
                            plan.DataRevision ?? new DataRevisionSnapshot();
                        HostStateGuardResult afterInfrastructure =
                            await _hostStateGuard.ValidateForExecutionAsync(
                                beforeInfrastructure,
                                plan.ExpectedMutations,
                                plan.HasCommittedMutation,
                                cancellation);
                        // Backup may touch FileGDB storage metadata shared by
                        // several datasets. After this transaction has already
                        // committed a write, a content-only SelfCommitted diff
                        // is causally inside the mutation budget. Project/map/
                        // layer/selection changes remain external and block.
                        // 中文：备份可能触及多个数据集共享的 FileGDB 存储元数据。事务提交写入后，只有内容层面的 SelfCommitted 差异在变更预算的因果范围内；项目、地图、图层和选择变化仍属于外部变化并会阻止继续。
                        bool safeInfrastructureRevision =
                            afterInfrastructure.Decision ==
                                HostRevisionDecision.Current
                            || (afterInfrastructure.Decision ==
                                    HostRevisionDecision.SelfCommitted
                                && plan.HasCommittedMutation
                                && afterInfrastructure.ChangedComponents.Count > 0
                                && afterInfrastructure.ChangedComponents.All(
                                    component => component == "content"));
                        if (!safeInfrastructureRevision)
                        {
                            ApplyHostStateBlock(
                                step,
                                stepResult,
                                afterInfrastructure,
                                beforeInfrastructure.Fingerprint);
                            results.Add(stepResult);
                            return FinalizeBatch(
                                batchSteps,
                                results,
                                settleUnreported: true,
                                afterInfrastructure.Reason);
                        }

                        plan.DataRevision = afterInfrastructure.Current;
                        plan.ProjectRevision =
                            afterInfrastructure.Current.Fingerprint;
                        _eventBus?.Publish(new AgentEvent
                        {
                            EventType = "execution.infrastructure_baseline_adopted",
                            Turn = CurrentTurn,
                            Metadata = new()
                            {
                                ["tool"] = toolName,
                                ["step"] = step.Id.ToString(),
                                ["before_revision"] = beforeInfrastructure.Fingerprint,
                                ["after_revision"] = afterInfrastructure.Current.Fingerprint,
                                ["backup_count"] = stepResult.BackupPaths.Count.ToString()
                            },
                            Data = new()
                            {
                                ["reason"] = "backup completed and protected host revision remained current"
                            }
                        });
                    }
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();
                _eventBus?.Publish(new AgentEvent
                {
                    EventType = "tool.started",
                    Turn = CurrentTurn,
                    Metadata = new() { ["tool"] = toolName, ["step"] = step.Id.ToString(), ["risk"] = riskLevel.ToString() },
                    Data = new() { ["args"] = step.ToolCall.Arguments }
                });
                _eventBus?.Publish(new AgentEvent
                {
                    EventType = "tool.progress",
                    Turn = CurrentTurn,
                    Metadata = new()
                    {
                        ["tool"] = toolName,
                        ["step"] = step.Id.ToString(),
                        ["phase"] = "execute"
                    },
                    Data = new()
                    {
                        ["message"] = "正在执行"
                    }
                });

                ToolResult result = new() { ToolName = toolName, Success = false };
                int maxAttempts = ShouldRetry(canonicalToolName, riskLevel) ? 2 : 1;
                int attemptCount = 0;

                // ── P0-3: Per-step timeout control ──
                // 中文：P0-3：每步超时控制
                var stepTimeout = _timeoutPolicy?.ForRiskLevel(riskLevel) ?? TimeSpan.FromMinutes(5);
                using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                stepCts.CancelAfter(stepTimeout);
                int progressOpen = 1;
                using var progressScope = ToolExecutionProgressContext.Begin(
                    update =>
                    {
                        if (Volatile.Read(ref progressOpen) == 1)
                            PublishToolProgress(step, toolName, update, sw.Elapsed);
                    });

                try
                {
                    for (int attempt = 0; attempt < maxAttempts; attempt++)
                    {
                        attemptCount = attempt + 1;
                        try
                        {
                            result = await ExecuteWithHeartbeatAsync(
                                step.ToolCall,
                                stepCts.Token,
                                sw);
                            if (result.Success) break;
                        }
                        catch (OperationCanceledException)
                            when (stepCts.IsCancellationRequested && !cancellation.IsCancellationRequested)
                        {
                            // The linked token is now passed into the ArcGIS GP
                            // call itself. A timeout requests host cancellation;
                            // do not claim that only local waiting was stopped.
                            // 中文：关联的取消令牌现已传入 ArcGIS GP 调用本身。超时时会请求宿主取消；不要声称只停止了本地等待。
                            result = new ToolResult
                            {
                                ToolName = toolName,
                                Success = false,
                                Error = $"工具执行超时（{stepTimeout.TotalSeconds:F0}s），" +
                                        "已向宿主发送取消请求。请确认 ArcGIS 地理处理状态后再重试。"
                            };
                            break; // 超时后不重试。 / Do not retry after a timeout.
                        }
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref progressOpen, 0);
                }
                sw.Stop();
                // Merge auto-corrections from parameter validation
                // 中文：合并参数验证中的自动更正
                if (stepResult.ToolResult!.AutofixApplied.Count > 0)
                {
                    foreach (var c in stepResult.ToolResult.AutofixApplied)
                        result.AutofixApplied.Add(c);
                }
                stepResult.ToolResult = result;
                stepResult.ToolResult.Attempts = attemptCount;
                stepResult.ElapsedMs = sw.ElapsedMilliseconds;

                // ── Observe ──
                // 中文：观察
                var obs = ObservationFactory.Create(toolName, step.ToolCall.Arguments, result, sw.Elapsed);
                if (obs is Observation concrete)
                {
                    concrete.StepId = step.Id;
                    concrete.InputParameters = stepResult.InputParameters;
                    concrete.ToolVerification = _tools.GetDescriptor(toolName)?.Verification;
                    concrete.ProofRequirements = step.ProofRequirements.ToList();
                }
                stepResult.Observation = obs;

                // ── Validate ──
                // 中文：验证
                if (result.Success && _validators?.Count > 0)
                {
                    _eventBus?.Publish(new AgentEvent
                    {
                        EventType = "tool.progress",
                        Turn = CurrentTurn,
                        Metadata = new()
                        {
                            ["tool"] = toolName,
                            ["step"] = step.Id.ToString(),
                            ["phase"] = "verification"
                        },
                        Data = new()
                        {
                            ["message"] = "执行完成，正在验证结果"
                        }
                    });

                    foreach (var v in _validators)
                    {
                        if (v.AppliesTo(toolName, obs))
                        {
                            var verdict = await v.ValidateAsync(obs);
                            stepResult.Validations.Add(verdict);

                            if (verdict.Verdict == ValidationVerdict.FAIL)
                            {
                                if (obs is Observation concreteObs)
                                    concreteObs.AddWarning($"验证失败[{v.Name}]: {verdict.Detail}");
                                _eventBus?.Publish(new AgentEvent
                                {
                                    EventType = "validation.failed",
                                    Turn = CurrentTurn,
                                    Metadata = new()
                                    {
                                        ["tool"] = toolName,
                                        ["step"] = step.Id.ToString(),
                                        ["detail"] = verdict.Detail,
                                        ["rule"] = v.Name
                                    }
                                });
                            }
                            else if (verdict.Verdict == ValidationVerdict.WARN)
                            {
                                if (obs is Observation concreteObs)
                                    concreteObs.AddWarning($"警告[{v.Name}]: {verdict.Detail}");
                                _eventBus?.Publish(new AgentEvent
                                {
                                    EventType = "validation.warned",
                                    Turn = CurrentTurn,
                                    Metadata = new()
                                    {
                                        ["tool"] = toolName,
                                        ["step"] = step.Id.ToString(),
                                        ["detail"] = verdict.Detail,
                                        ["rule"] = v.Name
                                    }
                                });
                            }
                        }
                    }
                }

                // ── Determine step status ──
                // 中文：确定阶级状态
                bool hasFailedValidation = stepResult.Validations.Any(v => v.Verdict == ValidationVerdict.FAIL);
                stepResult.Status = result.Success && !hasFailedValidation
                    ? StepResultStatus.Completed
                    : StepResultStatus.Failed;

                _eventBus?.Publish(new AgentEvent
                {
                    EventType = stepResult.Status == StepResultStatus.Completed ? "tool.completed" : "tool.failed",
                    Turn = CurrentTurn,
                    Metadata = new()
                    {
                        ["tool"] = toolName, ["step"] = step.Id.ToString(),
                        ["risk"] = riskLevel.ToString(),
                        ["success"] = result.Success.ToString(),
                        ["elapsed_ms"] = sw.ElapsedMilliseconds.ToString(),
                        ["retry"] = Math.Max(0, attemptCount - 1).ToString(),
                        ["status"] = stepResult.Status.ToString()
                    },
                    Data = new()
                    {
                        ["output"] = result.Output,
                        ["error"] = result.Error
                    }
                });
                _eventBus?.Publish(new AgentEvent
                {
                    EventType = "tool.progress",
                    Turn = CurrentTurn,
                    Metadata = new()
                    {
                        ["tool"] = toolName,
                        ["step"] = step.Id.ToString(),
                        ["phase"] = stepResult.Status == StepResultStatus.Completed ? "completed" : "failed"
                    },
                    Data = new()
                    {
                        ["message"] = stepResult.Status == StepResultStatus.Completed
                            ? "执行与验证完成"
                            : "执行失败"
                    }
                });

                if (_hostStateGuard != null
                    && stepResult.Status == StepResultStatus.Completed)
                {
                    // The agent's own write is now the expected baseline for
                    // the next step. External changes after this capture are
                    // rejected by the next optimistic-concurrency check.
                    // 中文：代理自身的写入现在是下一步的预期基线。捕获后对外部变化被下一次乐观并发检查拒绝。
                    plan.DataRevision =
                        await _hostStateGuard.CaptureAsync(cancellation);
                    plan.ProjectRevision = plan.DataRevision.Fingerprint;
                    if (MutatesHost(step))
                        plan.HasCommittedMutation = true;
                }

                results.Add(stepResult);
            }

            return FinalizeBatch(
                batchSteps,
                results,
                settleUnreported: true,
                "scheduler completed without receiving an execution outcome");
        }

        private async Task<bool> PrepareDestructiveBackupAsync(
            PlanStep step,
            StepExecutionResult stepResult,
            IToolDescriptor? descriptor,
            string canonicalToolName,
            ToolRiskLevel riskLevel,
            string currentCallSignature,
            CancellationToken cancellation)
        {
            string toolName = step.ToolCall!.Name;
            if (_workspace == null)
            {
                stepResult.Status = StepResultStatus.Blocked;
                stepResult.Cause = StepOutcomeCause.SafetyPolicy;
                stepResult.BlockReason =
                    "安全基础设施未就绪：缺少工作空间服务，" +
                    "无法为破坏性操作执行强制备份。请检查宿主初始化配置。";
                PublishBackupBlock(
                    step,
                    stepResult,
                    riskLevel,
                    currentCallSignature);
                return false;
            }

            var backupTargets = ResolveBackupTargets(
                step.ToolCall.Arguments,
                descriptor);
            if (backupTargets.Count == 0)
            {
                stepResult.Status = StepResultStatus.Blocked;
                stepResult.Cause = StepOutcomeCause.SafetyPolicy;
                stepResult.BlockReason =
                    $"自动备份失败：无法识别 {toolName} 的备份目标";
                PublishBackupBlock(
                    step,
                    stepResult,
                    riskLevel,
                    currentCallSignature);
                return false;
            }

            var backupSw = System.Diagnostics.Stopwatch.StartNew();
            using var progressScope = ToolExecutionProgressContext.Begin(
                update => PublishToolProgress(
                    step,
                    toolName,
                    update,
                    backupSw.Elapsed));
            for (int index = 0; index < backupTargets.Count; index++)
            {
                cancellation.ThrowIfCancellationRequested();
                string backupTarget = backupTargets[index];
                ToolExecutionProgressContext.Report(
                    "backup",
                    $"正在创建安全备份（{index + 1}/{backupTargets.Count}）：{backupTarget}");
                string? backupPath = await _workspace.BackupAsync(
                    backupTarget,
                    canonicalToolName,
                    cancellation);
                if (string.IsNullOrWhiteSpace(backupPath))
                {
                    stepResult.Status = StepResultStatus.Blocked;
                    stepResult.Cause = StepOutcomeCause.SafetyPolicy;
                    stepResult.BlockReason = $"自动备份失败：{backupTarget}";
                    PublishBackupBlock(
                        step,
                        stepResult,
                        riskLevel,
                        currentCallSignature,
                        backupTarget);
                    return false;
                }

                stepResult.BackupPaths.Add(backupPath);
                stepResult.BackupPath ??= backupPath;
                _eventBus?.Publish(new AgentEvent
                {
                    EventType = "workspace.backup_created",
                    Turn = CurrentTurn,
                    Metadata = new()
                    {
                        ["tool"] = toolName,
                        ["step"] = step.Id.ToString(),
                        ["path"] = backupPath,
                        ["target"] = backupTarget,
                        ["backup_index"] = stepResult.BackupPaths.Count.ToString(),
                        ["backup_count"] = backupTargets.Count.ToString()
                    }
                });

                // This is part of the transaction, not best effort. The next
                // baseline must never be captured while a cleanup operation is
                // still queued in ArcGIS Pro.
                // 中文：这是交易的一部分，不是尽力而为。在清理操作仍在ArcGIS Pro排队期间，下一个基线绝不能被捕获。
                await _workspace.RemoveFromMapAsync(backupPath, cancellation);
            }

            ToolExecutionProgressContext.Report(
                "backup_complete",
                $"安全备份完成，共 {backupTargets.Count} 个目标",
                100);
            return true;
        }

        private void PublishBackupBlock(
            PlanStep step,
            StepExecutionResult result,
            ToolRiskLevel riskLevel,
            string currentCallSignature,
            string? backupTarget = null)
        {
            _eventBus?.Publish(new AgentEvent
            {
                EventType = "safety.blocked",
                Turn = CurrentTurn,
                Metadata = new()
                {
                    ["tool"] = step.ToolCall!.Name,
                    ["step"] = step.Id.ToString(),
                    ["risk"] = riskLevel.ToString(),
                    ["status"] = result.Status.ToString(),
                    ["call_signature"] = currentCallSignature
                },
                Data = new()
                {
                    ["block_reason"] = result.BlockReason,
                    ["backup_target"] = backupTarget,
                    ["completed_backups"] = result.BackupPaths.ToArray()
                }
            });
            _blockedCallSignaturesThisRun.Add(currentCallSignature);
        }

        private void ApplyHostStateBlock(
            PlanStep step,
            StepExecutionResult result,
            HostStateGuardResult freshness,
            string plannedRevision)
        {
            string toolName = step.ToolCall!.Name;
            result.Status = StepResultStatus.Blocked;
            result.Cause = freshness.Decision switch
            {
                HostRevisionDecision.ProbeUnreliable =>
                    StepOutcomeCause.HostProbeUnreliable,
                HostRevisionDecision.SelfCommitted =>
                    StepOutcomeCause.HostSelfWriteRace,
                _ => StepOutcomeCause.HostStateInvalidated
            };
            result.BlockReason = freshness.Reason;
            result.ToolResult = new ToolResult
            {
                ToolName = toolName,
                Success = false,
                Error = freshness.Reason
            };
            _eventBus?.Publish(new AgentEvent
            {
                EventType = "execution.host_state_changed",
                Turn = CurrentTurn,
                Metadata = new()
                {
                    ["tool"] = toolName,
                    ["step"] = step.Id.ToString(),
                    ["decision"] = freshness.Decision.ToString(),
                    ["planned_revision"] = plannedRevision,
                    ["current_revision"] =
                        freshness.Current.Fingerprint,
                    ["changed"] = string.Join(
                        ",",
                        freshness.ChangedComponents)
                },
                Data = new()
                {
                    ["reason"] = freshness.Reason
                }
            });
        }

        private async Task<ToolResult> ExecuteWithHeartbeatAsync(
            ToolCall call,
            CancellationToken cancellation,
            System.Diagnostics.Stopwatch elapsed)
        {
            Task<ToolResult> execution = _tools.ExecuteAsync(call, cancellation);
            if (_progressHeartbeatInterval <= TimeSpan.Zero)
                return await execution;

            while (!execution.IsCompleted)
            {
                Task delay = Task.Delay(
                    _progressHeartbeatInterval,
                    cancellation);
                Task completed = await Task.WhenAny(execution, delay);
                if (completed == execution)
                    return await execution;

                await delay;
                string message = elapsed.Elapsed >= TimeSpan.FromSeconds(30)
                    ? $"宿主仍在执行，已等待 {elapsed.Elapsed.TotalSeconds:F0} 秒"
                    : $"正在执行，已等待 {elapsed.Elapsed.TotalSeconds:F0} 秒";
                ToolExecutionProgressContext.Report(
                    "execute_heartbeat",
                    message);
            }

            return await execution;
        }

        private void PublishToolProgress(
            PlanStep step,
            string toolName,
            ToolExecutionProgressUpdate update,
            TimeSpan elapsed)
        {
            var metadata = new Dictionary<string, string>
            {
                ["tool"] = toolName,
                ["step"] = step.Id.ToString(),
                ["phase"] = update.Phase,
                ["elapsed_ms"] = ((long)elapsed.TotalMilliseconds).ToString(),
                ["stalled"] = (elapsed >= TimeSpan.FromSeconds(30))
                    .ToString()
                    .ToLowerInvariant()
            };
            if (update.Percent.HasValue)
                metadata["progress_pct"] = update.Percent.Value.ToString();

            _eventBus?.Publish(new AgentEvent
            {
                EventType = "tool.progress",
                Turn = CurrentTurn,
                Metadata = metadata,
                Data = new()
                {
                    ["message"] = update.Message
                }
            });
        }

        private async Task<HostStateGuardResult> ValidateHostForStepAsync(
            AgentPlan plan,
            CancellationToken cancellation)
        {
            const int maxProbeRetries = 1;
            HostStateGuardResult freshness = new();
            for (int attempt = 0; attempt <= maxProbeRetries; attempt++)
            {
                if (_hostStateGuard == null || plan.DataRevision == null)
                {
                    return new HostStateGuardResult
                    {
                        IsCurrent = true,
                        Decision = HostRevisionDecision.Current
                    };
                }

                freshness = await _hostStateGuard.ValidateForExecutionAsync(
                    plan.DataRevision,
                    plan.ExpectedMutations,
                    plan.HasCommittedMutation,
                    cancellation);
                if (freshness.Decision != HostRevisionDecision.ProbeUnreliable
                    || attempt == maxProbeRetries)
                    return freshness;

                // One recapture before treating probe noise as terminal.
                // 中文：在将探针噪声视为终端前进行一次重捕获。
                plan.DataRevision =
                    await _hostStateGuard.CaptureAsync(cancellation);
                plan.ProjectRevision = plan.DataRevision.Fingerprint;
            }

            return freshness;
        }

        private static IReadOnlyList<StepExecutionResult> FinalizeBatch(
            IReadOnlyList<PlanStep> batchSteps,
            List<StepExecutionResult> results,
            bool settleUnreported,
            string reason)
        {
            var stepById = batchSteps.ToDictionary(step => step.Id);
            foreach (StepExecutionResult result in results)
            {
                if (!stepById.TryGetValue(result.StepId, out PlanStep? step)
                    || step.ToolCall == null)
                {
                    continue;
                }

                result.ToolCallId = step.ToolCall.Id;
                if (string.IsNullOrWhiteSpace(result.ToolName))
                    result.ToolName = step.ToolCall.Name;
                result.ProvidedCapabilities =
                    step.ProvidedCapabilities.ToList();
                result.Purpose = step.Purpose;
            }

            if (settleUnreported)
            {
                var reported = results
                    .Select(result => result.StepId)
                    .ToHashSet();
                foreach (PlanStep step in batchSteps.Where(
                             step => !reported.Contains(step.Id)))
                {
                    string toolName = step.ToolCall!.Name;
                    string skippedReason =
                        $"not executed because the batch stopped: {reason}";
                    results.Add(new StepExecutionResult
                    {
                        StepId = step.Id,
                        ToolCallId = step.ToolCall.Id,
                        ToolName = toolName,
                        Status = StepResultStatus.Skipped,
                        Cause = StepOutcomeCause.BatchStopped,
                        InputParameters =
                            new Dictionary<string, object?>(
                                step.ToolCall.Arguments),
                        ToolResult = new ToolResult
                        {
                            ToolName = toolName,
                            Success = false,
                            Error = skippedReason
                        },
                        BlockReason = skippedReason,
                        Effect = step.Effect,
                        ProvidedCapabilities =
                            step.ProvidedCapabilities.ToList(),
                        Purpose = step.Purpose
                    });
                }
            }

            var order = batchSteps
                .Select((step, index) => new { step.Id, index })
                .ToDictionary(item => item.Id, item => item.index);
            return results
                .OrderBy(result => order.TryGetValue(
                    result.StepId,
                    out int index)
                    ? index
                    : int.MaxValue)
                .ToList()
                .AsReadOnly();
        }

        private static bool MutatesHost(PlanStep step)
        {
            if (step.WriteSet.Count > 0)
                return true;
            var effects = step.Effect?.Profile.Effects ?? DataEffectKind.None;
            return (effects & ~DataEffectKind.ReadData) != DataEffectKind.None;
        }

        private static void ApplyInfluenceEvidence(
            InvocationEffect? effect,
            DataInfluenceEstimate estimate)
        {
            if (effect == null)
                return;

            effect.EstimatedObjectCount = estimate.ScopeObjectCount >= 0
                ? estimate.ScopeObjectCount
                : estimate.FeatureCount >= 0 ? estimate.FeatureCount : null;
            effect.ExistingNonEmptyTargetCount =
                estimate.ExistingNonEmptyTargetCount >= 0
                    ? estimate.ExistingNonEmptyTargetCount
                    : null;
            effect.SchemaMutationCount = estimate.SchemaMutationCount >= 0
                ? estimate.SchemaMutationCount
                : null;
            effect.UsesSelection = estimate.UsesSelectionScope;
            effect.UsesWhereClause = estimate.UsesFilteredScope;
            if (estimate.UsesSelectionScope)
                effect.Scope = EffectTargetScope.Selection;
            else if (estimate.UsesFilteredScope)
                effect.Scope = EffectTargetScope.Filtered;
            if (estimate.SchemaMutationCount > 0)
                effect.Profile.Effects |= DataEffectKind.AlterSchema;
            effect.Confidence = estimate.IsReliable
                ? EvidenceConfidence.High
                : EvidenceConfidence.Low;
            effect.EvidenceSource = $"live-preflight:{estimate.Source}";
            effect.Summary = $"{effect.Summary}; " +
                $"scope={effect.EstimatedObjectCount?.ToString() ?? "unknown"}, " +
                $"existing_values={effect.ExistingNonEmptyTargetCount?.ToString() ?? "unknown"}, " +
                $"schema_mutations={effect.SchemaMutationCount?.ToString() ?? "unknown"}";
        }

        private bool ShouldRetry(string toolName, ToolRiskLevel riskLevel)
        {
            if (riskLevel != ToolRiskLevel.ReadOnly)
                return false;

            var descriptor = _tools.GetDescriptor(toolName);
            return descriptor?.Annotation is ToolAnnotation.ReadOnly or ToolAnnotation.Idempotent;
        }

        /// <summary>
        private static IReadOnlyList<string> ResolveBackupTargets(
            Dictionary<string, object?> args,
            IToolDescriptor? descriptor)
        {
            var targets = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddCandidates(string key)
            {
                if (!IsBackupDataParameter(key, descriptor)
                    || !TryGetArgumentValue(args, key, out var value))
                    return;

                foreach (string candidate in ExtractBackupCandidateValues(value))
                {
                    if (seen.Add(candidate))
                        targets.Add(candidate);
                }
            }

            if (descriptor is IToolDataflowDescriptor dataflow)
            {
                var outputKeys = new HashSet<string>(
                    dataflow.OutputParameters.Where(p => !string.IsNullOrWhiteSpace(p)),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var key in dataflow.InputParameters.Where(p => !string.IsNullOrWhiteSpace(p)))
                {
                    if (outputKeys.Contains(key))
                        continue;
                    AddCandidates(key);
                }
            }

            foreach (var key in BackupSourceKeys)
                AddCandidates(key);

            if (descriptor != null)
            {
                foreach (var key in descriptor.RequiredParameters.Concat(descriptor.Parameters.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!ParameterNameClassifier.LooksLikeInputDataSource(key))
                        continue;
                    AddCandidates(key);
                }
            }

            foreach (var kv in args)
            {
                if (!ParameterNameClassifier.LooksLikeInputDataSource(kv.Key))
                    continue;
                AddCandidates(kv.Key);
            }

            return targets.AsReadOnly();
        }

        private static bool IsBackupDataParameter(string key, IToolDescriptor? descriptor)
        {
            if (ParameterNameClassifier.IsOutputParameter(key))
                return false;

            ParameterProperty? property = null;
            if (descriptor != null)
            {
                if (!descriptor.Parameters.TryGetValue(key, out property))
                {
                    property = descriptor.Parameters
                        .FirstOrDefault(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                        .Value;
                }
            }

            if (property == null)
                return ParameterNameClassifier.LooksLikeInputDataSource(key);

            string role = property.SemanticRole?.Trim().ToLowerInvariant() ?? "";
            if (role.StartsWith("output_", StringComparison.Ordinal))
                return false;
            if (role is "input_dataset" or "input_raster" or "workspace")
                return true;
            if (!string.IsNullOrWhiteSpace(role))
                return false;

            if (!string.IsNullOrWhiteSpace(property.DataType))
                return IsBackupCapableDataType(property.DataType);

            return ParameterNameClassifier.LooksLikeInputDataSource(key);
        }

        private static bool IsBackupCapableDataType(string dataType)
        {
            string normalized = dataType.ToLowerInvariant();
            string[] accepted =
            {
                "feature", "layer", "table", "dataset", "raster", "workspace",
                "geodatabase", "mosaic", "terrain", "tin", "network",
                "topology", "parcel", "record set"
            };
            return accepted.Any(normalized.Contains);
        }

        private static readonly string[] BackupSourceKeys =
        {
            "in_features",
            "in_feature_class",
            "in_feature_dataset",
            "in_layer",
            "in_table",
            "in_dataset",
            "in_layer_or_view",
            "in_raster",
            "in_workspace",
            "in_fc",
            "input",
            "input_features",
            "input_feature_class",
            "input_layer",
            "input_table",
            "input_dataset",
            "input_raster",
            "input_workspace",
            "layer_name",
            "inputs",
            "source_features",
            "source_table",
            "source_dataset",
            "source_layer",
            "target_features",
            "target_table",
            "source",
            "src",
            "table"
        };

        private static bool TryGetArgumentValue(Dictionary<string, object?> args, string key, out object? value)
        {
            if (args.TryGetValue(key, out value))
                return true;

            foreach (var kv in args)
            {
                if (kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    value = kv.Value;
                    return true;
                }
            }

            value = null;
            return false;
        }

        private static IReadOnlyList<string> ExtractBackupCandidateValues(object? value)
        {
            var values = new List<string>();
            CollectStringValues(value, values);
            return values
                .SelectMany(candidate => candidate.Split(
                    ';',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Select(candidate => candidate.Trim())
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
                .AsReadOnly();
        }

        private static void CollectStringValues(object? value, List<string> values)
        {
            if (value == null)
                return;

            if (value is string s)
            {
                if (!string.IsNullOrWhiteSpace(s))
                    values.Add(s.Trim());
                return;
            }

            if (value is JToken token)
            {
                if (token is JArray array)
                {
                    foreach (var child in array.Children())
                        CollectStringValues(child, values);
                    return;
                }

                string tokenValue = token.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(tokenValue))
                    values.Add(tokenValue);
                return;
            }

            if (value is System.Collections.IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                    CollectStringValues(item, values);
                return;
            }

            string scalar = value.ToString()?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(scalar))
                values.Add(scalar);
        }

    }
}
