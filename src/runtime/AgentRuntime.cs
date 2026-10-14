using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using SpatialAgent.Runtime.Contracts;
using SpatialAgent.Core;
using SpatialAgent.Runtime.Planning;

namespace SpatialAgent.Runtime
{
    public class AgentRuntime
    {
        private readonly IAgentSession _session;
        private readonly ILLMProvider _llm;
        private readonly ToolRegistry _tools;
        private readonly ContextManager _contextManager;
        private readonly Scheduler _scheduler;
        private readonly IContextBudgeter _contextBudgeter;
        private readonly IObservationCompressor _observationCompressor;
        private readonly IEventBus? _eventBus;
        private IComplexityRouter _complexityRouter;
        private IPlanCompiler _planCompiler;
        private ISubagentCoordinator _subagentCoordinator;
        private IPlanValidator _planValidator;
        private IPlanCoverageValidator _coverageValidator;
        private IGoalScopeResolver _goalScopeResolver;
        private ICapabilityCatalog _capabilityCatalog;
        private ICapabilityClosureValidator _capabilityClosureValidator;
        private IPlanAdmissibilityEvaluator _planAdmissibilityEvaluator;
        private IGoalProgressTracker _goalProgressTracker;
        private IGoalClosureEvaluator _goalClosureEvaluator;
        private IResourceBindingEvidencePromoter _resourceBindingEvidencePromoter;
        private IRunCheckpointStore? _checkpointStore;
        private IDataInfluenceEstimator? _dataInfluenceEstimator;
        private IProjectLineageStore? _projectLineageStore;
        private SessionLease? _sessionLease;
        private int _hostShutdownRequested;
        private string? _hostShutdownReason;
        private string? _hostShutdownObservedReason;

        private ISafetyGate? _safety;
        private IAuditLogger? _audit;
        private ITranscriptLifecycle? _transcriptLifecycle;
        private ITranscriptSessionWriter? _transcriptSession;
        private ITranscriptTurnWriter? _transcriptTurn;
        private ITranscriptToolWriter? _transcriptTool;
        private IVerificationLedger? _ledger;
        private IContextSnapshotter? _snapshotter;
        private ILoggingHealthMonitor? _healthMonitor;
        private IMcpService? _mcp;

        private int _turnCount;
        private long _sessionTotalMs;

        public event EventHandler<string>? OnProgress;

        public AgentRuntime(IAgentSession s, ILLMProvider llm, ToolRegistry tools,
            ContextManager ctx, IEventBus? bus = null)
        {
            _session = s; _llm = llm; _tools = tools;
            _contextManager = ctx;
            _contextManager.SetEventBus(bus);
            _scheduler = new Scheduler(tools, bus, validators: null);
            _contextBudgeter = new DefaultContextBudgeter();
            _observationCompressor = new DefaultObservationCompressor();
            _eventBus = bus;
            var effectResolver = new DefaultInvocationEffectResolver();
            var domainPacks = new DomainPackRegistry();
            _capabilityCatalog = new DefaultCapabilityCatalog();
            _complexityRouter = new AdaptiveComplexityRouter();
            _planCompiler = new DefaultPlanCompiler(
                tools,
                domainPacks,
                effectResolver,
                _capabilityCatalog);
            _subagentCoordinator = new BoundedSubagentCoordinator();
            _planValidator = new GisPlanValidator(tools);
            _coverageValidator = new DefaultPlanCoverageValidator();
            _goalScopeResolver = new DefaultGoalScopeResolver();
            _capabilityClosureValidator =
                new DefaultCapabilityClosureValidator();
            _planAdmissibilityEvaluator =
                new DefaultPlanAdmissibilityEvaluator();
            _goalProgressTracker = new DefaultGoalProgressTracker();
            _goalClosureEvaluator = new DefaultGoalClosureEvaluator();
            _resourceBindingEvidencePromoter =
                new DefaultResourceBindingEvidencePromoter();
            _scheduler.SetEffectResolver(effectResolver);
            _scheduler.SetHostStateGuard(_contextManager);
        }

        public void SetSafety(ISafetyGate g) { _safety = g; _scheduler.SetSafety(g); }
        public void SetAudit(IAuditLogger a) => _audit = a;
        public void SetTranscriptWriter(
            ITranscriptLifecycle? lifecycle,
            ITranscriptSessionWriter? sessionWriter,
            ITranscriptTurnWriter? turnWriter,
            ITranscriptToolWriter? toolWriter)
        {
            _transcriptLifecycle = lifecycle;
            _transcriptSession = sessionWriter;
            _transcriptTurn = turnWriter;
            _transcriptTool = toolWriter;
        }
        public void SetLedger(IVerificationLedger? ledger) => _ledger = ledger;
        public void SetSnapshotter(IContextSnapshotter? snapshotter) => _snapshotter = snapshotter;
        public void SetLoggingHealthMonitor(ILoggingHealthMonitor? hm) => _healthMonitor = hm;
        public void SetCheckpointStore(IRunCheckpointStore? store) => _checkpointStore = store;
        public void SetProjectLineageStore(IProjectLineageStore? store)
        {
            _projectLineageStore = store;
            if (store == null)
                return;
            foreach (var lineage in store.LoadLineage())
            {
                var normalized = NormalizeLineage(lineage);
                if (normalized == null)
                    continue;
                _lineageParents[normalized.Output] = normalized.Parent;
                _lineageRecords[normalized.Output] = normalized;
            }
        }
        public void SetMcpService(IMcpService? mcp) => _mcp = mcp;
        public void SetSessionLease(SessionLease? lease) =>
            _sessionLease = lease;

        /// <summary>统一日志初始化——四层日志 + EventBus 订阅 + 健康监控。</summary>
        /// <remarks>English：Unified log initialization—four-layer logs + EventBus subscription + health monitoring.</remarks>
        public void InitializeLogging(LoggingConfig config)
        {
            if (_eventBus == null) return;
            string logDir = config.LogDir;
            _transcriptLifecycle?.Initialize(logDir);
            _transcriptLifecycle?.SubscribeTo(_eventBus);
            _transcriptLifecycle?.SetLoggingHealthMonitor(_healthMonitor);
            _ledger?.Initialize(logDir);
            _ledger?.SetLoggingHealthMonitor(_healthMonitor);
            _snapshotter?.Initialize(logDir);
            _snapshotter?.SetLoggingHealthMonitor(_healthMonitor);
            if (_snapshotter != null)
            {
                _eventBus.Subscribe("turn.start", e => _ = _snapshotter.SnapshotAsync("turn.start", e.Turn ?? 0));
                _eventBus.Subscribe("llm.calling", e => _ = _snapshotter.SnapshotAsync("llm.calling", e.Turn ?? 0, $"round:{e.Metadata?.GetValueOrDefault("round", "?")}"));
                _eventBus.Subscribe("verification.failed", e => _ = _snapshotter.SnapshotAsync("verification.failed", e.Turn ?? 0, $"tool:{e.Metadata?.GetValueOrDefault("tool", "?")}"));
            }
        }
        public void SetWorkspace(IWorkspaceService? workspace)
        {
            _scheduler.SetWorkspace(workspace);
        }
        public void SetValidators(List<IValidationRule> validators) => _scheduler.SetValidators(validators);
        public void SetParameterValidator(Validation.ParameterValidator? v) => _scheduler.SetParameterValidator(v);
        public void SetTimeoutPolicy(ToolExecutionTimeoutPolicy? p) => _scheduler.SetTimeoutPolicy(p);
        public void SetDataInfluenceEstimator(IDataInfluenceEstimator? e)
        {
            _dataInfluenceEstimator = e;
            _scheduler.SetDataInfluenceEstimator(e);
        }
        public void SetPlanningServices(
            IComplexityRouter? router = null,
            IPlanCompiler? compiler = null,
            ISubagentCoordinator? subagents = null,
            IPlanValidator? validator = null,
            IPlanCoverageValidator? coverageValidator = null,
            IGoalScopeResolver? goalScopeResolver = null,
            ICapabilityCatalog? capabilityCatalog = null,
            ICapabilityClosureValidator? capabilityClosureValidator = null,
            IPlanAdmissibilityEvaluator? planAdmissibilityEvaluator = null,
            IGoalProgressTracker? goalProgressTracker = null,
            IGoalClosureEvaluator? goalClosureEvaluator = null,
            IResourceBindingEvidencePromoter? resourceBindingEvidencePromoter = null)
        {
            if (router != null) _complexityRouter = router;
            if (compiler != null) _planCompiler = compiler;
            if (subagents != null) _subagentCoordinator = subagents;
            if (validator != null) _planValidator = validator;
            if (coverageValidator != null) _coverageValidator = coverageValidator;
            if (goalScopeResolver != null) _goalScopeResolver = goalScopeResolver;
            if (capabilityCatalog != null) _capabilityCatalog = capabilityCatalog;
            if (capabilityClosureValidator != null)
                _capabilityClosureValidator = capabilityClosureValidator;
            if (planAdmissibilityEvaluator != null)
                _planAdmissibilityEvaluator = planAdmissibilityEvaluator;
            if (goalProgressTracker != null)
                _goalProgressTracker = goalProgressTracker;
            if (goalClosureEvaluator != null)
                _goalClosureEvaluator = goalClosureEvaluator;
            if (resourceBindingEvidencePromoter != null)
                _resourceBindingEvidencePromoter =
                    resourceBindingEvidencePromoter;
        }
        public void SetTurn(int turn) => _scheduler.CurrentTurn = turn;
        public int TurnCount => _turnCount;
        public IAgentSession Session => _session;

        /// <summary>本轮（最近一次 LLM 调用）的 token 用量，单轮展示用。</summary>
        /// <remarks>English：The token usage for this round (the most recent LLM call) is for single-round demonstration.</remarks>
        public TokenUsage LastTurnTokens { get; } = new();

        /// <summary>上回合调用的工具名——操作链预测输入（Path D 增强）。</summary>
        /// <remarks>English：The tool called last round is called — Chain of Operation Prediction Input (Path D Enhancement).</remarks>
        private List<string> _lastTurnToolCalls = new();

        // ── 审批恢复状态（ADR 0051）──
        private List<StepExecutionResult> _allStepResultsThisTurn = new();
        private List<IObservation> _allObsThisTurn = new();
        private HashSet<string> _blockedToolsThisTurn = new(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _blockedCallSignaturesThisTurn = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _lineageParents =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DataLineageRecord> _lineageRecords =
            new(StringComparer.OrdinalIgnoreCase);
        private ResolvedGoalScope? _lastCompletedGoalScope;
        private ResolvedGoalScope? _currentGoalScope;
        private TargetCoverageResult? _currentGoalCoverage;
        private readonly HashSet<string> _settledStepResults =
            new(StringComparer.Ordinal);
        private readonly HashSet<string> _completedActionIds =
            new(StringComparer.Ordinal);
        private readonly DefaultDomainArtifactLedger _artifactLedger = new();
        private readonly DefaultGoalControlPlane _goalControlPlane = new();
        private readonly object _turnStateLock = new();

        public void ResetConversation(string newThreadId)
        {
            _turnCount = 0;
            _sessionTotalMs = 0;
            _lastTurnToolCalls.Clear();   // ★ F1：新会话不应携带旧会话的预测链。 / A new session must not retain the prior session's prediction chain.
            lock (_turnStateLock)
            {
                _allStepResultsThisTurn.Clear();
                _allObsThisTurn.Clear();
            }
            _blockedToolsThisTurn.Clear();
            _blockedCallSignaturesThisTurn.Clear();
            _lastCompletedGoalScope = null;
            _currentGoalScope = null;
            _currentGoalCoverage = null;
            _settledStepResults.Clear();
            _completedActionIds.Clear();
            _session.ResetConversation(newThreadId);
            _scheduler.CurrentTurn = 0;
        }

        public async Task<AgentRunState?> RestoreLatestCheckpointAsync(CancellationToken cancellation = default)
        {
            if (_checkpointStore == null)
                return null;

            AgentRunState? state = await _checkpointStore.LoadMostRecentAsync(cancellation);
            if (state == null)
                return null;

            ApplyCheckpointState(state);
            _eventBus?.Publish(new AgentEvent
            {
                EventType = "checkpoint.restored",
                SessionId = state.ThreadId,
                Turn = state.Turn,
                Metadata = new()
                {
                    ["thread"] = state.ThreadId,
                    ["turn"] = state.Turn.ToString(),
                    ["round"] = state.Round.ToString(),
                    ["status"] = state.Status.ToString()
                }
            });
            return state;
        }

        public async Task<AgentRunState?> RestoreCheckpointAsync(string threadId, CancellationToken cancellation = default)
        {
            if (_checkpointStore == null || string.IsNullOrWhiteSpace(threadId))
                return null;

            AgentRunState? state = await _checkpointStore.LoadLatestAsync(threadId, cancellation);
            if (state == null)
                return null;

            ApplyCheckpointState(state);
            _eventBus?.Publish(new AgentEvent
            {
                EventType = "checkpoint.restored",
                SessionId = state.ThreadId,
                Turn = state.Turn,
                Metadata = new()
                {
                    ["thread"] = state.ThreadId,
                    ["turn"] = state.Turn.ToString(),
                    ["round"] = state.Round.ToString(),
                    ["status"] = state.Status.ToString()
                }
            });
            return state;
        }

        public void RejectPendingApproval(ApprovalTicket? ticket = null, string reason = "UserRejected")
        {
            if (_session.Status != SessionStatus.WaitingForApproval)
                return;

            var rejectedSteps = _session.Plan?.Steps
                .Where(s => s.Status == PlanStepStatus.AwaitingApproval && s.ToolCall != null)
                .Where(s => ticket == null || s.PendingApproval?.Id == ticket.Id)
                .ToList() ?? new List<PlanStep>();

            var pendingTools = rejectedSteps
                .Select(s => s.ToolCall!.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // ★ Phase C: rejected tools → blocked for this turn
            // Normalize to canonical name so Scheduler's canonical-name check always matches.
            // 中文：C阶段：拒绝工具→本回合被封锁，归一化为规范名称，以便调度器的规范名称检查始终匹配。
            foreach (var step in rejectedSteps)
            {
                AddRejectedCallSignature(step);
            }

            _session.Plan = null;
            _session.SetStatus(SessionStatus.Idle);
            _eventBus?.Publish(new AgentEvent
            {
                EventType = "safety.approval_rejected",
                Turn = _turnCount,
                Metadata = new()
                {
                    ["reason"] = reason,
                    ["tools"] = string.Join(",", pendingTools),
                    ["ticket_id"] = ticket?.Id ?? ""
                },
                Data = new()
                {
                    ["rejected_steps"] = rejectedSteps.Select(s => new
                    {
                        step = s.Id,
                        ticket_id = s.PendingApproval?.Id ?? "",
                        approval_kind = s.PendingApproval?.Kind.ToString() ?? "",
                        call_signature = s.PendingApproval?.CoveredCalls
                            .FirstOrDefault(call => call.StepId == s.Id)?.CallSignature
                            ?? s.PendingApproval?.CallSignature
                            ?? ""
                    }).ToList()
                }
            });
        }

        /// <summary>
        /// LLM → Execute → feed results back → LLM decides continue/stop.
        /// Max 5 rounds per user turn. Stops when LLM returns no tool_calls.
        /// </summary>
        /// <remarks>中文：LLM → 执行→反馈结果→LLM决定继续/停止。每回合最多5发子弹。当LLM无tool_calls返回时停止。</remarks>
        public async Task<AgentResponse> ProcessAsync(TaskRequest req)
        {
            // ★ P0-1：安全门缺失时硬阻断
            // English：P0-1: Hard blocking when the safety door is missing
            if (_safety == null)
            {
                _session.SetStatus(SessionStatus.Failed);
                _eventBus?.Publish(new AgentEvent { EventType = "safety.unavailable", Metadata = new() { ["reason"] = "SafetyGate 未初始化" } });
                return new AgentResponse { UserReply = "Agent 安全模块未正确初始化。", StopReason = "SafetyUnavailable" };
            }

            using var run = CorrelationContext.BeginRun(_session.ThreadId);
            // ★ 审批恢复：复用被中断的 turn，不递增不写 user_msg
            // English：Approval recovery: Reuse interrupted turns, do not increment or write user_msg
            if (!req.IsApprovalResumption)
                _turnCount++;
            int turn = _turnCount;
            string msg = req.UserMessage;
            if (!req.IsApprovalResumption)
            {
                LastTurnTokens.PromptTokens = 0;
                LastTurnTokens.CompletionTokens = 0;
            }
            var ct = req.Cancellation;
            var sessionSw = Stopwatch.StartNew();
            int maxRounds = RuntimePolicy.MaxReactRounds;

            _session.SetStatus(SessionStatus.Running);
            if (!req.IsApprovalResumption)
            {
                lock (_turnStateLock)
                {
                    _allStepResultsThisTurn.Clear();
                    _allObsThisTurn.Clear();
                }
                _blockedToolsThisTurn.Clear();
                _blockedCallSignaturesThisTurn.Clear();
                _settledStepResults.Clear();
                _completedActionIds.Clear();
                _currentGoalCoverage = null;
            }
            if (req.AppendUserMessage)
                _session.AddMessage(new Message { Role = "user", Content = msg });
            _scheduler.CurrentTurn = turn;
            _scheduler.CurrentPermissionLevel = _session.PermissionLevel;
            _scheduler.IsApprovalResumption = false;
            _scheduler.ApprovedTicketIds = null;
            _scheduler.ApprovedStepIds = null;
            _scheduler.InvalidateInfluenceCache();  // ★ ADR 0051：每轮清除 describe 缓存，防止跨轮次数据污染。 / Clear the describe cache each turn to prevent cross-turn contamination.
            if (req.AppendUserMessage)
                _transcriptTurn?.WriteUserMessage(turn, msg);
            var approvalDecision = req.ApprovalDecision;
            var approvalResumeMessages = new List<Message>();

            // ── ★ 审批恢复——暂停继续，非重试（ADR 0051，2026-07-21）──
            // English：Approval Resumption — Pause and Continue, No Retrial (ADR 0051, 2026-07-21)
            if (req.IsApprovalResumption)
            {
                var savedPlan = _session.Plan;
                if (savedPlan == null || savedPlan.Steps.Count == 0)
                {
                    _session.SetStatus(SessionStatus.Failed);
                    return new AgentResponse { UserReply = "审批状态异常：找不到被暂停的计划。", StopReason = "审批恢复失败" };
                }

                approvalDecision ??= BuildLegacyApprovalDecision(savedPlan, req.Approved);
                var decisionTicket = savedPlan.Steps
                    .Select(step => step.PendingApproval)
                    .FirstOrDefault(ticket => ticket != null
                        && ticket.Id.Equals(
                            approvalDecision.TicketId,
                            StringComparison.OrdinalIgnoreCase));
                if (decisionTicket == null)
                {
                    _session.SetStatus(SessionStatus.Failed);
                    return new AgentResponse
                    {
                        UserReply = "审批状态异常：票据不存在或已经失效。",
                        StopReason = "审批恢复失败"
                    };
                }

                if (approvalDecision.Decision == ApprovalDecisionKind.Approve)
                {
                    var freshness = await ValidateApprovalFreshnessAsync(
                        savedPlan,
                        decisionTicket,
                        ct);
                    if (!freshness.IsCurrent)
                    {
                        return InvalidateStaleApproval(
                            savedPlan,
                            decisionTicket,
                            freshness);
                    }
                }

                if (approvalDecision.Decision == ApprovalDecisionKind.Reject)
                {
                    var rejectedSteps = new List<object>();
                    var rejectionResults = new List<StepExecutionResult>();
                    foreach (var s in savedPlan.Steps)
                    {
                        if (s.Status == PlanStepStatus.AwaitingApproval
                            && s.PendingApproval?.Id == approvalDecision.TicketId)
                        {
                            s.Status = PlanStepStatus.Skipped;
                            // ★ ADR 0055：归一化工具名。LLM 可能用短名/别名调用，
                            // 但 blocked set 必须存 canonical name，否则 Scheduler
                            // 用 canonical 名查不到 → 别名绕过拦截。
                            // English：ADR 0055: Name of the normalization tool. LLMs may be called with shortened/aliases, but the blocked set must store the canonical name; otherwise, the scheduler cannot find the canonical name → bypass interception.
                            string raw = s.ToolCall?.Name ?? "";
                            string canonical = _tools.ResolveName(raw) ?? raw;
                            AddRejectedCallSignature(s);
                            rejectionResults.Add(new StepExecutionResult
                            {
                                StepId = s.Id,
                                ToolName = canonical,
                                Status = StepResultStatus.Blocked,
                                Cause = StepOutcomeCause.UserRejected,
                                InputParameters = s.ToolCall == null
                                    ? new Dictionary<string, object?>()
                                    : new Dictionary<string, object?>(s.ToolCall.Arguments),
                                BlockReason = "用户拒绝了该审批请求。",
                                ApprovalReason = s.PendingApproval?.Evidence?.Summary,
                                ApprovalKind = s.PendingApproval?.Kind,
                                ApprovalTicket = s.PendingApproval,
                                UserApproved = false,
                                Effect = s.Effect
                            });
                            rejectedSteps.Add(new
                            {
                                step = s.Id,
                                tool = canonical,
                                raw_name = raw,
                                ticket_id = s.PendingApproval?.Id ?? "",
                                approval_kind = s.PendingApproval?.Kind.ToString() ?? "",
                                required_permission = s.PendingApproval?.RequiredPermissionLevel?.ToString() ?? "",
                                call_signature = s.PendingApproval?.CoveredCalls
                                    .FirstOrDefault(call => call.StepId == s.Id)?.CallSignature
                                    ?? s.PendingApproval?.CallSignature
                                    ?? ""
                            });
                        }
                    }
                    lock (_turnStateLock)
                    {
                        SettleStepResults(
                            savedPlan,
                            rejectionResults,
                            _allStepResultsThisTurn,
                            _allObsThisTurn,
                            _blockedToolsThisTurn,
                            msg);
                    }
                    _eventBus?.Publish(new AgentEvent
                    {
                        EventType = "safety.approval_resumed",
                        Turn = turn,
                        Metadata = new()
                        {
                            ["approved"] = "false",
                            ["action"] = "rejected",
                            ["rejected_steps"] = string.Join(",", rejectedSteps.Select(x => ((dynamic)x).step.ToString())),
                            ["ticket_id"] = approvalDecision.TicketId
                        },
                        Data = new() { ["rejected"] = rejectedSteps }
                    });
                }
                else
                {
                    // ★ ADR 0055：审计充实——记录批准范围（approval_kind、step、tool），
                    // 事后可证明"本次只执行了被批准的那一步"。
                    // English：ADR 0055: Audit Enrichment—Recording the scope of approval (approval_kind, steps, tools), which can be later proven that "only the approved step was performed this time."
                    var approvedSteps = savedPlan.Steps
                        .Where(s => s.Status == PlanStepStatus.AwaitingApproval
                                    && s.PendingApproval?.Id == approvalDecision.TicketId)
                        .Select(s => new
                        {
                            step = s.Id,
                            tool = _tools.ResolveName(s.ToolCall?.Name ?? "") ?? s.ToolCall?.Name ?? "",
                            raw_name = s.ToolCall?.Name ?? "",
                            ticket_id = s.PendingApproval?.Id ?? "",
                            approval_kind = s.PendingApproval?.Kind.ToString() ?? "",
                            required_permission = s.PendingApproval?.RequiredPermissionLevel?.ToString() ?? ""
                        }).ToList();
                    _eventBus?.Publish(new AgentEvent
                    {
                        EventType = "safety.approval_resumed",
                        Turn = turn,
                        Metadata = new()
                        {
                            ["approved"] = "true",
                            ["action"] = "approved",
                            ["approved_steps"] = string.Join(",", approvedSteps.Select(x => x.step.ToString())),
                            ["approved_tools"] = string.Join(",", approvedSteps.Select(x => x.tool)),
                            ["ticket_id"] = approvalDecision.TicketId,
                            ["approval_kind"] = approvedSteps.FirstOrDefault()?.approval_kind ?? "",
                            ["required_permission"] = approvedSteps.FirstOrDefault()?.required_permission ?? ""
                        },
                        Data = new() { ["approved"] = approvedSteps }
                    });
                    _scheduler.IsApprovalResumption = true;
                    _scheduler.ApprovedTicketIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        approvalDecision.TicketId
                    };
                    // Batch Destructive: pass all covered step IDs to the scheduler.
                    // 中文：批次破坏：将所有覆盖的步 ID 传递给调度器。
                    var approvedTicket = savedPlan.Steps
                        .Select(s => s.PendingApproval)
                        .FirstOrDefault(t => t != null && t.Id.Equals(approvalDecision.TicketId, StringComparison.OrdinalIgnoreCase));
                    if (approvedTicket?.CoveredStepIds.Count > 1)
                        _scheduler.ApprovedStepIds = new HashSet<int>(approvedTicket.CoveredStepIds);
                    _scheduler.SetDomainSnapshot(_session.Domain);
                    _scheduler.SatisfiedActionIds = _completedActionIds;
                    var steps = await _scheduler.RunAsync(savedPlan, ct);
                    lock (_turnStateLock)
                    {
                        SettleStepResults(
                            savedPlan,
                            steps,
                            _allStepResultsThisTurn,
                            _allObsThisTurn,
                            _blockedToolsThisTurn,
                            msg);
                    }

                    var again = steps.FirstOrDefault(sr => sr.Status == StepResultStatus.AwaitingApproval);
                    if (again != null)
                    {
                        var dataRevision = await _contextManager.CaptureDataRevisionAsync(ct);
                        BindApprovalToDataRevision(savedPlan, dataRevision);
                        PublishApprovalBound(
                            turn,
                            savedPlan,
                            dataRevision);
                        _session.SetStatus(SessionStatus.WaitingForApproval);
                        _sessionTotalMs += sessionSw.ElapsedMilliseconds;
                        var checkpointCtx = await _contextManager.GatherLightAsync(msg);
                        PopulateDomainContext(checkpointCtx);
                        var checkpointBundle = _contextBudgeter.Build(
                            msg,
                            checkpointCtx,
                            _session.Messages);
                        string checkpointPromptHash = ComputeSHA256(
                            SystemPrompt.Build(
                                checkpointCtx,
                                SystemPrompt.DetectResponseMode(msg)));
                        await SaveCheckpointAsync(
                            turn,
                            savedPlan.Version,
                            msg,
                            checkpointCtx,
                            checkpointBundle,
                            checkpointPromptHash,
                            ResolvePlanDescriptors(savedPlan),
                            _blockedToolsThisTurn,
                            _session.Status,
                            ct);
                        RecordTurnToolCalls(_allStepResultsThisTurn);
                        return MakeApprovalResponse(again, _allStepResultsThisTurn);
                    }

                    bool allDone = savedPlan.Steps.All(s =>
                        s.Status == PlanStepStatus.Completed || s.Status == PlanStepStatus.Skipped);
                    if (allDone)
                    {
                        lock (_turnStateLock)
                        {
                            RecordCoverageSatisfaction(
                                savedPlan,
                                _allStepResultsThisTurn);
                        }
                        _currentGoalScope = savedPlan.ResolvedScope;
                        _currentGoalCoverage = savedPlan.Coverage;
                        RecordTurnToolCalls(_allStepResultsThisTurn);
                        var resumeCtx = await _contextManager.GatherLightAsync(msg);
                        PopulateDomainContext(resumeCtx);
                        var resumeBundle = _contextBudgeter.Build(msg, resumeCtx, _session.Messages);
                        var resumeTools = ResolvePlanDescriptors(savedPlan);
                        string resumePromptHash = ComputeSHA256(
                            SystemPrompt.Build(
                                resumeCtx,
                                SystemPrompt.DetectResponseMode(msg)));
                        var resumeCompletion = CompletionReportBuilder.Build(
                            msg,
                            _allStepResultsThisTurn,
                            _currentGoalScope,
                            _currentGoalCoverage);
                        if (_goalControlPlane.Decide(
                                _currentGoalScope,
                                _currentGoalCoverage,
                                resumeCompletion.IsComplete)
                            == GoalControlState.Closed)
                        {
                            return await CompleteSettledGoalAsync(
                                turn,
                                savedPlan.Version,
                                msg,
                                resumeCtx,
                                resumeBundle,
                                resumePromptHash,
                                resumeTools,
                                _blockedToolsThisTurn,
                                resumeCompletion,
                                sessionSw,
                                ct);
                        }

                        PublishGoalResidual(
                            turn,
                            _currentGoalScope,
                            _currentGoalCoverage,
                            resumeCompletion);
                        var resumedCalls = savedPlan.Steps
                            .Where(step => step.ToolCall != null)
                            .Select(step => step.ToolCall!)
                            .ToList();
                        approvalResumeMessages.Add(new Message
                        {
                            Role = "assistant",
                            Content = "",
                            ToolCalls = resumedCalls
                        });
                        foreach (var result in ReconcileToolCallFeedback(
                                     resumedCalls,
                                     savedPlan,
                                     _allStepResultsThisTurn,
                                     turn))
                        {
                            approvalResumeMessages.Add(new Message
                            {
                                Role = "tool",
                                ToolCallId = result.ToolCallId,
                                Content = _observationCompressor.Format(
                                    _observationCompressor.Compress(
                                        result,
                                        result.Observation))
                            });
                        }
                        approvalResumeMessages.Add(new Message
                        {
                            Role = "user",
                            Content = "审批批次已经结算，但目标仍有未闭合要求。"
                                + "请仅针对残余要求继续规划；不要重复已验证完成的操作。"
                        });
                    }
                }
                // 批准（有失败）/拒绝 → 继续 ReAct 循环
                // English：Approval (with failure)/rejection → continues the ReAct cycle
            }
            EmitProgress("收集上下文");
            var ctx = await _contextManager.GatherLightAsync(msg);
            PopulateDomainContext(ctx);
            if (!req.IsApprovalResumption)
            {
                _currentGoalScope = _goalScopeResolver.Resolve(
                    msg,
                    _currentGoalScope ?? _lastCompletedGoalScope,
                    _session.Domain);
                _currentGoalCoverage = null;
                PublishGoalCompiled(turn, _currentGoalScope);
            }
            _scheduler.SetDomainSnapshot(_session.Domain);
            DataRevisionSnapshot observedHostRevision =
                await _contextManager.CaptureDataRevisionAsync(ct);
            _transcriptTurn?.WriteContext(turn, ctx.ProjectInfo, ctx.MapInfo,
                ctx.KnowledgeChunks.Count, ctx.MatchedSkills.Count);

            string systemPrompt = SystemPrompt.Build(ctx, SystemPrompt.DetectResponseMode(msg));
            string systemPromptHash = ComputeSHA256(systemPrompt);
            var turnContext = _contextBudgeter.Build(msg, ctx, _session.Messages);
            turnContext.AugmentedUserMessage +=
                BuildGoalContractPrompt(_currentGoalScope);

            // ★ Two-tier tool exposure (Path D + 操作链预测, 2026-07-19):
            //    Tier 1 = always-on + skill-declared + 上回合调用触发链预测（完整参描）
            //    Tier 2 = context top50（轻量：仅名称+类型，剥参描/枚举/标题）
            var skillTools = ctx.MatchedSkills
                .SelectMany(s => s.RequiredTools)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var capabilityTools = _capabilityCatalog.SelectToolNames(
                _currentGoalScope?.RequiredCapabilities
                    .AsEnumerable()
                    ?? Array.Empty<string>(),
                _tools.GetAllDescriptors());
            // Goal-contract capabilities are the strongest deterministic
            // routing signal and must not be displaced by the Tier-1 budget.
            // 中文：目标-合同能力是最强的确定性路由信号，不能被一级预算取代。
            skillTools = capabilityTools
                .Concat(skillTools)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            // 操作链预测：上回合调用的工具 → 其后继预提升到 Tier1
            // English：Chain of operation prediction: The tool called last round → its successor is pre-promoted to Tier 1
            var predicted = RuntimePolicy.PredictNextTools(_lastTurnToolCalls);
            var (tier1, tier2) = RuntimePolicy.SelectExposedToolsTiered(
                _tools.GetAllDescriptors(), ctx.RelevantTools, skillTools, predicted);
            var tools = tier1.Concat(tier2).ToList().AsReadOnly();
            if (predicted.Count > 0)
                Debug.WriteLine($"[AgentRuntime] Tool exposure: Tier1={tier1.Count} Tier2={tier2.Count} Total={tools.Count} predicted={predicted.Count}");

            // ★ Phase C2 fix：工具暴露决策写入 transcript（此前仅 Debug.WriteLine，事后无据可查）
            // English：Phase C2 fix: Tool exposes decision writing transcript (previously only Debug.WriteLine, no evidence available afterward)
            _transcriptTurn?.WriteToolExposure(turn, tier1.Count, tier2.Count,
                RuntimePolicy.AlwaysOnToolNames.Count,
                tier1.Select(t => t.Name).ToList(),
                predicted.Count > 0 ? predicted.ToList() : null,
                predicted.Count > 0 ? "operation_chain" : null);

            // Messages: prior session history + exactly one augmented current
            // user input. The raw current message is retained in the durable
            // session ledger, but must not be sent a second time to the model.
            // 中文：消息：之前的会话历史 + 恰好一次增强的当前用户输入。原始当前消息会被保留在持久会话账本中，但不能第二次发送给模型。
            var messages = new List<Message>(_session.Messages);
            if (messages.Count > 0
                && messages[^1].Role.Equals("user", StringComparison.OrdinalIgnoreCase)
                && messages[^1].Content.Equals(msg, StringComparison.Ordinal))
            {
                messages.RemoveAt(messages.Count - 1);
            }
            messages.Add(new Message { Role = "user", Content = turnContext.AugmentedUserMessage });
            messages.AddRange(approvalResumeMessages);

            await SaveCheckpointAsync(turn, 0, msg, ctx, turnContext, systemPromptHash, tools, Array.Empty<string>(), SessionStatus.Running, ct);

            // ★ REACT LOOP ★
            // 中文：REACT 循环
            var allObs = _allObsThisTurn;
            var allStepResults = _allStepResultsThisTurn;
            var blockedToolsThisTurn = new HashSet<string>(_blockedToolsThisTurn, StringComparer.OrdinalIgnoreCase);
            var blockedCallSignaturesThisTurn = new HashSet<string>(_blockedCallSignaturesThisTurn, StringComparer.OrdinalIgnoreCase);
            _blockedToolsThisTurn.Clear();
            _blockedCallSignaturesThisTurn.Clear();
            // ★ Phase C: propagate blocked tools to Scheduler for hard intercept
            // 中文：C 阶段：将阻塞工具传播到调度器以实现硬拦截
            _scheduler.BlockedToolNames = blockedToolsThisTurn;
            _scheduler.BlockedCallSignatures = blockedCallSignaturesThisTurn;
            bool hadTools = false;
            int nextStepId = allStepResults.Count == 0
                ? 1
                : allStepResults.Max(s => s.StepId) + 1;
            int nextPlanVersion = Math.Max(1, (_session.Plan?.Version ?? 0) + 1);
            var recoveryController = new PlanRecoveryController();

            for (int round = 0; round < maxRounds; round++)
            {
                EmitProgress(round == 0 ? "AI 分析中" : $"继续执行({round + 1}/{maxRounds})");

                // ★ Snapshot trigger: what does the agent see before deciding?
                // 中文：快照触发：代理在决定前看到了什么？
                if (round == 0)
                {
                    _eventBus?.Publish(new AgentEvent
                    {
                        EventType = "turn.start",
                        Turn = turn,
                        Metadata = new() { ["round"] = round.ToString() }
                    });
                }
                _eventBus?.Publish(new AgentEvent
                {
                    EventType = "llm.calling",
                    Turn = turn,
                    Metadata = new() { ["round"] = round.ToString() }
                });

                LLMResponse llmResp;
                var llmSw = Stopwatch.StartNew();
                using var llmSpan = CorrelationContext.BeginSpan("llm_request");
                var activeTools = tools
                    .Where(t => !blockedToolsThisTurn.Contains(t.Name))
                    .ToList()
                    .AsReadOnly();
                var llmToolsForLog = activeTools.Select(t => new
                {
                    t.Name,
                    t.Description,
                    t.Category,
                    Annotation = t.Annotation.ToString(),
                    Parameters = t.Parameters,
                    RequiredParameters = t.RequiredParameters
                }).ToList();
                // model/thinking 取自 Provider 实际请求参数——UI 切换是否生效以此为准
                // English：model/thinking is taken from the actual requested parameters of the Provider—whether the UI switch is effective
                ToolMessageProtocolResult protocol =
                    ToolMessageProtocol.Validate(messages);
                if (!protocol.IsValid)
                {
                    _session.SetStatus(SessionStatus.Failed);
                    _eventBus?.Publish(new AgentEvent
                    {
                        EventType = "llm.message_protocol_invalid",
                        Turn = turn,
                        Metadata = new()
                        {
                            ["round"] = round.ToString(),
                            ["message_index"] =
                                protocol.MessageIndex.ToString()
                        },
                        Data = new() { ["error"] = protocol.Error }
                    });
                    AgentDiagnostics.Warn(
                        "AgentRuntime.MessageProtocol",
                        protocol.Error,
                        null,
                        _eventBus);
                    await SaveCheckpointAsync(
                        turn,
                        round,
                        msg,
                        ctx,
                        turnContext,
                        systemPromptHash,
                        tools,
                        blockedToolsThisTurn,
                        _session.Status,
                        ct);
                    RecordTurnToolCalls(allStepResults);
                    return new AgentResponse
                    {
                        UserReply =
                            "工具执行状态未能完整收敛，已在本地停止请求以保护会话一致性。",
                        StopReason = "MessageProtocolInvalid",
                        HadToolCalls = hadTools,
                        ToolResults = CollectToolResults(allStepResults)
                    };
                }
                _transcriptTurn?.WriteLLMRequest(turn, messages, llmToolsForLog, ComputeSHA256(systemPrompt),
                    _llm.ModelName, _llm.ThinkingEffort);

                try
                {
                    llmResp = await AwaitLlmWithHeartbeatAsync(
                        _llm.ChatAsync(
                            messages,
                            systemPrompt,
                            activeTools,
                            cancellation: ct),
                        turn,
                        round,
                        "planning",
                        ct);
                    llmSw.Stop();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    _session.SetStatus(SessionStatus.Idle);
                    throw;
                }
                catch (Exception ex)
                {
                    _session.SetStatus(SessionStatus.Failed);
                    AgentDiagnostics.Warn("AgentRuntime.LLM", "LLM call threw before producing a response", ex, _eventBus);
                    await SaveCheckpointAsync(turn, round, msg, ctx, turnContext, systemPromptHash, tools, blockedToolsThisTurn, _session.Status, ct);
                    RecordTurnToolCalls(allStepResults);
                    return new AgentResponse { UserReply = "AI 服务暂时不可用。", StopReason = "LLM异常" };
                }

                // ★ Token accounting: accumulate session usage after every LLM call
                // 中文：令牌计费：每次 LLM 调用后累加会话使用量
                LastTurnTokens.PromptTokens += llmResp.PromptTokens;
                LastTurnTokens.CompletionTokens += llmResp.CompletionTokens;
                _session.TokenUsage.PromptTokens += llmResp.PromptTokens;
                _session.TokenUsage.CompletionTokens += llmResp.CompletionTokens;

                _transcriptTurn?.WriteLLMResponse(turn,
                    llmResp.ToolCalls.Select(tc => new { tc.Id, tc.Name, Arguments = tc.Arguments }),
                    llmResp.Content, llmSw.ElapsedMilliseconds,
                    llmResp.PromptTokens, llmResp.CompletionTokens,
                    llmResp.FinishReason, 0);

                if (!llmResp.Success)
                {
                    _session.SetStatus(SessionStatus.Failed);
                    AgentDiagnostics.Warn("AgentRuntime.LLM", llmResp.ErrorMessage, null, _eventBus);
                    await SaveCheckpointAsync(turn, round, msg, ctx, turnContext, systemPromptHash, tools, blockedToolsThisTurn, _session.Status, ct);
                    RecordTurnToolCalls(allStepResults);
                    return new AgentResponse
                    {
                        UserReply = llmResp.Content,
                        StopReason = llmResp.ErrorKind == LLMErrorKind.RateLimit
                            ? "LLM限流"
                            : llmResp.ErrorKind == LLMErrorKind.Timeout
                                ? "LLM超时"
                                : "LLM错误"
                    };
                }

                if (llmResp.IsSemanticallyEmpty)
                {
                    _eventBus?.Publish(new AgentEvent
                    {
                        EventType = "llm.semantic_empty_response",
                        Turn = turn,
                        Metadata = new()
                        {
                            ["round"] = round.ToString(),
                            ["finish_reason"] = llmResp.FinishReason,
                            ["recovery_attempt"] = "1"
                        },
                        Data = new()
                        {
                            ["completion_tokens"] = llmResp.CompletionTokens,
                            ["action"] = "bounded_protocol_recovery"
                        }
                    });
                    var recoveryMessages = messages.ToList();
                    recoveryMessages.Add(new Message
                    {
                        Role = "user",
                        Content = "上一响应在传输成功后没有形成有效内容。请仅做一次协议恢复：返回合法工具调用，或用非空文本明确说明还需要哪项信息；不得返回空响应。"
                    });
                    _transcriptTurn?.WriteLLMRequest(
                        turn,
                        recoveryMessages,
                        llmToolsForLog,
                        ComputeSHA256(systemPrompt),
                        _llm.ModelName,
                        _llm.ThinkingEffort);
                    var recoverySw = Stopwatch.StartNew();
                    LLMResponse recovered;
                    try
                    {
                        recovered = await AwaitLlmWithHeartbeatAsync(
                            _llm.ChatAsync(
                                recoveryMessages,
                                systemPrompt,
                                activeTools,
                                temperature: 0,
                                maxTokens: 8192,
                                cancellation: ct),
                            turn,
                            round,
                            "semantic_recovery",
                            ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        _session.SetStatus(SessionStatus.Idle);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        recovered = new LLMResponse
                        {
                            Success = false,
                            ErrorKind = LLMErrorKind.EmptyResponse,
                            ErrorMessage = ex.Message,
                            Content = "模型空响应恢复失败。"
                        };
                    }
                    recoverySw.Stop();
                    LastTurnTokens.PromptTokens += recovered.PromptTokens;
                    LastTurnTokens.CompletionTokens += recovered.CompletionTokens;
                    _session.TokenUsage.PromptTokens += recovered.PromptTokens;
                    _session.TokenUsage.CompletionTokens += recovered.CompletionTokens;
                    _transcriptTurn?.WriteLLMResponse(
                        turn,
                        recovered.ToolCalls.Select(tc => new
                        {
                            tc.Id,
                            tc.Name,
                            Arguments = tc.Arguments
                        }),
                        recovered.Content,
                        recoverySw.ElapsedMilliseconds,
                        recovered.PromptTokens,
                        recovered.CompletionTokens,
                        recovered.FinishReason,
                        1);
                    llmResp = recovered;

                    if (!llmResp.Success || llmResp.IsSemanticallyEmpty)
                    {
                        string fallbackReply =
                            BuildSemanticEmptyFallback(
                                msg,
                                allStepResults);
                        _session.SetStatus(SessionStatus.Failed);
                        _eventBus?.Publish(new AgentEvent
                        {
                            EventType = "llm.semantic_recovery_failed",
                            Turn = turn,
                            Metadata = new()
                            {
                                ["round"] = round.ToString(),
                                ["finish_reason"] = llmResp.FinishReason,
                                ["had_tool_calls"] = hadTools.ToString()
                            },
                            Data = new()
                            {
                                ["action"] = "deterministic_evidence_fallback",
                                ["tool_result_count"] = allStepResults.Count
                            }
                        });
                        _transcriptTool?.WriteSynthOutput(
                            turn,
                            fallbackReply,
                            0);
                        _session.AddMessage(new Message
                        {
                            Role = "assistant",
                            Content = fallbackReply
                        });
                        await SaveCheckpointAsync(
                            turn, round, msg, ctx, turnContext,
                            systemPromptHash, tools, blockedToolsThisTurn,
                            _session.Status, ct);
                        RecordTurnToolCalls(allStepResults);
                        return new AgentResponse
                        {
                            UserReply = fallbackReply,
                            ExecutionSummary = BuildSummary(allStepResults),
                            StopReason = "LLM空响应",
                            HadToolCalls = hadTools,
                            CanContinue = true,
                            ToolResults = CollectToolResults(allStepResults)
                        };
                    }
                }

                // No tool calls → done
                // 中文：→做任何工具调用
                if (!llmResp.HasToolCalls)
                {
                    _session.SetStatus(SessionStatus.Completed);
                    string finalReply = llmResp.Content;
                    GoalCompletionReport? roundCompletion = null;
                    bool goalRequiresEvidence =
                        _currentGoalScope?.UnresolvedTargets.Count > 0
                        || _currentGoalScope?.ProofObligations.Any(proof =>
                            proof.Required
                            && !_currentGoalScope
                                .SatisfiedProofObligations.Contains(
                                    proof.Id,
                                    StringComparer.OrdinalIgnoreCase))
                            == true;
                    if (hadTools || goalRequiresEvidence)
                    {
                        roundCompletion = CompletionReportBuilder.Build(
                            msg,
                            allStepResults,
                            _currentGoalScope,
                            _currentGoalCoverage);
                        finalReply = CompletionResponseArbitrator.Compose(
                            roundCompletion,
                            llmResp.Content);
                        FinalizeGoalLifecycle(
                            roundCompletion,
                            _currentGoalScope);
                        _transcriptTool?.WriteSynthOutput(
                            turn,
                            finalReply,
                            0);
                    }

                    _session.AddMessage(new Message { Role = "assistant", Content = finalReply });
                    _session.Plan = null;
                    // ★ P1-4：turn 收尾——清除本轮 agent 自建且未清的选择集
                    // English：P1-4: turn to wrap up—Clears the selected set that the agent has built and has not yet cleared in this round
                    if (hadTools)
                        await ClearAgentSelectionsAsync(allStepResults, turn, msg, ct);
                    _sessionTotalMs += sessionSw.ElapsedMilliseconds;
                    await SaveCheckpointAsync(turn, round, msg, ctx, turnContext, systemPromptHash, tools, blockedToolsThisTurn, _session.Status, ct);
                    RecordTurnToolCalls(allStepResults);
                    return new AgentResponse
                    {
                        UserReply = finalReply,
                        ExecutionSummary = BuildSummary(allStepResults),
                        HadToolCalls = hadTools,
                        StopReason = roundCompletion == null
                            ? "纯文本"
                            : StopReasonFor(roundCompletion.Outcome),
                        CanContinue = roundCompletion?.Outcome is
                            GoalOutcomeStatus.Partial
                            or GoalOutcomeStatus.ClarificationRequired,
                        ToolResults = CollectToolResults(allStepResults)
                    };
                }

                // ★ P0-3：工具存在性预检——LLM 编造的工具名在进 Scheduler 前拦截
                // English：P0-3: Tool Existence Precheck—The LLM-generated tool name is intercepted before entering the Scheduler
                var responseToolCalls = llmResp.ToolCalls.ToList();
                ToolMessageProtocolResult responseProtocol =
                    ToolMessageProtocol.ValidateToolCallBatch(
                        responseToolCalls);
                if (!responseProtocol.IsValid)
                {
                    _session.SetStatus(SessionStatus.Failed);
                    AgentDiagnostics.Warn(
                        "AgentRuntime.ToolCallBatch",
                        responseProtocol.Error,
                        null,
                        _eventBus);
                    await SaveCheckpointAsync(
                        turn,
                        round,
                        msg,
                        ctx,
                        turnContext,
                        systemPromptHash,
                        tools,
                        blockedToolsThisTurn,
                        _session.Status,
                        ct);
                    RecordTurnToolCalls(allStepResults);
                    return new AgentResponse
                    {
                        UserReply =
                            "模型返回了无法唯一识别的工具调用，已停止执行以保护任务一致性。",
                        StopReason = "InvalidToolCallBatch",
                        HadToolCalls = true,
                        ToolResults = CollectToolResults(allStepResults)
                    };
                }
                var hallucinatedTools = responseToolCalls
                    .Where(tc => _tools.GetDescriptor(tc.Name) == null && _tools.ResolveName(tc.Name) == null)
                    .Select(tc => tc.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var executableToolCalls = responseToolCalls
                    .Where(tc => !hallucinatedTools.Contains(
                        tc.Name,
                        StringComparer.OrdinalIgnoreCase))
                    .ToList();
                if (hallucinatedTools.Count == responseToolCalls.Count)
                {
                    // 全部幻觉 → 反馈 LLM
                    _eventBus?.Publish(new AgentEvent { EventType = "llm.hallucinated_tools", Turn = turn,
                        Metadata = new() { ["tools"] = string.Join(",", hallucinatedTools) } });
                    messages.Add(new Message
                    {
                        Role = "assistant",
                        Content = llmResp.Content,
                        ReasoningContent = llmResp.ReasoningContent,
                        ToolCalls = responseToolCalls
                    });
                    var rejectedResults = responseToolCalls
                        .Select((call, index) =>
                            CreateNotExecutedToolResult(
                                call,
                                index,
                                "tool is not registered; not executed"))
                        .ToList();
                    foreach (StepExecutionResult result in rejectedResults)
                    {
                        string content = _observationCompressor.Format(
                            _observationCompressor.Compress(result));
                        messages.Add(new Message
                        {
                            Role = "tool",
                            ToolCallId = result.ToolCallId,
                            Content = content
                        });
                    }
                    lock (_turnStateLock)
                        MergeStepResults(allStepResults, rejectedResults);
                    hadTools = true;
                    messages.Add(new Message { Role = "user", Content = $"以下工具不存在：{string.Join("、", hallucinatedTools)}。请用已有工具重新执行。" });
                    blockedToolsThisTurn.UnionWith(hallucinatedTools);
                    await SaveCheckpointAsync(
                        turn,
                        round + 1,
                        msg,
                        ctx,
                        turnContext,
                        systemPromptHash,
                        tools,
                        blockedToolsThisTurn,
                        _session.Status,
                        ct);
                    continue;
                }
                if (hallucinatedTools.Count > 0)
                {
                    // 部分幻觉 → 阻止幻觉工具，其余正常执行
                    // English：Some hallucinations → prevent hallucination tools, while the rest operate normally
                    _eventBus?.Publish(new AgentEvent { EventType = "llm.hallucinated_tools", Turn = turn,
                        Metadata = new() { ["tools"] = string.Join(",", hallucinatedTools) } });
                    blockedToolsThisTurn.UnionWith(hallucinatedTools);
                    // Preserve the original assistant batch for protocol
                    // fidelity; only executable calls enter the physical plan.
                    // 中文：保留原始助理批处理以保证协议的忠实度;只有可执行调用进入物理计划。
                }

                hadTools = true;

                // Compile a physical plan without another model round-trip.
                // The deterministic router keeps ordinary one-tool interactions
                // on the direct path and enables reviewers only for complex risk.
                // 中文：在没有其他模型往返的情况下，编制一份实体计划。确定性路由器将普通单工具交互保持在直接路径上，仅允许审查者处理复杂风险。
                var planningRequest = new PlanningRequest
                {
                    Goal = msg,
                    GoalContractFingerprint =
                        _currentGoalScope?.ContractFingerprint ?? "",
                    ParameterConstraints =
                        (IReadOnlyList<GoalParameterConstraint>?)
                            _currentGoalScope?.ParameterConstraints
                        ?? Array.Empty<GoalParameterConstraint>(),
                    ProofObligations =
                        (IReadOnlyList<GoalProofObligation>?)
                            _currentGoalScope?.ProofObligations
                        ?? Array.Empty<GoalProofObligation>(),
                    ToolCalls = executableToolCalls,
                    Domain = _session.Domain,
                    ContextRevision = turnContext.ContextHash
                };
                var plan = _planCompiler.Compile(planningRequest, nextStepId);
                // The plan is an optimistic transaction over the exact host
                // revision observed before model inference.
                // 中文：该计划是基于模型推断前观察到的确切宿主修正的乐观交易。
                plan.DataRevision = observedHostRevision;
                plan.ProjectRevision = observedHostRevision.Fingerprint;
                plan.Version = nextPlanVersion++;
                plan.ExpectedMutations = ExpectedMutationBuilder.Build(plan);
                planningRequest.Effects = plan.Steps
                    .Select(step => step.Effect)
                    .OfType<InvocationEffect>()
                    .ToList();
                var planning = _complexityRouter.Assess(planningRequest);
                nextStepId += plan.Steps.Count;
                plan.Planning = planning;
                plan.Validation = _planValidator.Validate(plan, _session.Domain);
                plan.ResolvedScope = _currentGoalScope
                    ?? _goalScopeResolver.Resolve(
                        msg,
                        _lastCompletedGoalScope,
                        _session.Domain);
                plan.Coverage = _coverageValidator.Validate(
                    plan.ResolvedScope,
                    plan,
                    _session.Domain);
                plan.CapabilityClosure =
                    _capabilityClosureValidator.Validate(
                        plan.ResolvedScope,
                        plan);
                plan.Admissibility =
                    _planAdmissibilityEvaluator.Evaluate(plan);
                if (plan.Admissibility.Decision ==
                        PlanAdmissibilityDecision.Probe
                    && IsResourceResolutionProbe(plan.ResolvedScope))
                {
                    foreach (PlanStep step in plan.Steps)
                        step.Purpose = PlanStepPurpose.SupportProbe;
                }
                foreach (PlanIssue issue in plan.Admissibility.Issues)
                {
                    if (!plan.Validation.Issues.Contains(issue))
                        plan.Validation.Issues.Add(issue);
                }
                plan.Validation.IsValid = plan.Admissibility.CanExecute;
                if (plan.CapabilityClosure.PlausibleButUnconfirmed.Count > 0)
                {
                    _eventBus?.Publish(new AgentEvent
                    {
                        EventType = "capability.mapping_gap",
                        Turn = turn,
                        Metadata = new()
                        {
                            ["plausible_count"] =
                                plan.CapabilityClosure
                                    .PlausibleButUnconfirmed.Count.ToString(),
                            ["capabilities"] = string.Join(
                                ",",
                                plan.CapabilityClosure
                                    .PlausibleButUnconfirmed),
                            ["decision"] =
                                plan.Admissibility.Decision.ToString()
                        },
                        Data = new()
                        {
                            ["message"] =
                                "Capability declaration gap recorded; "
                                + "admissibility and proof remain separate."
                        }
                    });
                }
                _session.Plan = plan;
                EmitPlanValidation(turn, plan);
                if (!plan.Validation.IsValid)
                {
                    PlanRecoveryObservation recovery =
                        recoveryController.Observe(
                            plan.Admissibility,
                            plan.Validation);
                    if (recovery.ShouldDiversify)
                    {
                        _eventBus?.Publish(new AgentEvent
                        {
                            EventType = "plan.recovery_escalated",
                            Turn = turn,
                            Metadata = new()
                            {
                                ["signature"] = recovery.Signature,
                                ["repeat_count"] =
                                    recovery.RepeatCount.ToString(),
                                ["stop_thrashing"] =
                                    recovery.ShouldStopThrashing.ToString()
                            }
                        });
                    }
                    EmitPlanGenerated(turn, round, plan);
                    messages.Add(new Message
                    {
                        Role = "assistant",
                        Content = llmResp.Content,
                        ReasoningContent = llmResp.ReasoningContent,
                        ToolCalls = responseToolCalls
                    });
                    foreach (var rejectedCall in responseToolCalls)
                    {
                        messages.Add(new Message
                        {
                            Role = "tool",
                            ToolCallId = rejectedCall.Id,
                            Content = "计划校验失败；该工具未执行。"
                        });
                    }
                    messages.Add(new Message
                    {
                        Role = "user",
                        Content = BuildPlanValidationFeedback(
                            plan.Validation,
                            recovery)
                    });
                    if (recovery.ShouldStopThrashing)
                        break;
                    continue;
                }
                _currentGoalCoverage = plan.Coverage;
                if (planning.Mode == PlanningMode.PlannerWithReviewers
                    && planning.RecommendedSubagents > 0)
                {
                    plan.Reviews = (await ReviewPlanAsync(
                        plan,
                        planning.RecommendedSubagents,
                        msg,
                        ct)).ToList();
                    var reviewGate = EvaluateReviewGate(plan.Reviews);
                    EmitReviewGate(turn, plan, reviewGate);
                    if (!reviewGate.CanExecute)
                    {
                        EmitPlanGenerated(turn, round, plan);
                        messages.Add(new Message
                        {
                            Role = "assistant",
                            Content = llmResp.Content,
                            ReasoningContent = llmResp.ReasoningContent,
                            ToolCalls = responseToolCalls
                        });
                        foreach (var rejectedCall in responseToolCalls)
                        {
                            messages.Add(new Message
                            {
                                Role = "tool",
                                ToolCallId = rejectedCall.Id,
                                Content = "计划审查门未通过；该工具未执行。"
                            });
                        }
                        messages.Add(new Message
                        {
                            Role = "user",
                            Content = BuildReviewFeedback(reviewGate)
                        });
                        continue;
                    }
                }
                EmitPlanGenerated(turn, round, plan);
                // Safety pre-check: log intent (audit done after execution based on real status)
                // No pre-execution audit — audit runs on actual results below
                // 中文：安全预检查：日志意图（基于实际状态执行后进行审计）无执行前审计——审计基于以下实际结果进行

                EmitProgress($"执行 {plan.Steps.Count} 步");
                // Persist the exact validated plan before the first tool can
                // mutate ArcGIS. If Pro is terminated during a GP operation,
                // recovery still knows what was in flight; durable tool_start
                // and tool_end records complete the execution timeline.
                // 中文：在第一个工具修改 ArcGIS 状态前，先持久化已验证的精确计划。如果 Pro 在 GP 操作期间终止，恢复流程仍能识别进行中的操作；持久化的 tool_start 和 tool_end 记录可补全执行时间线。
                await SaveCheckpointAsync(
                    turn,
                    round,
                    msg,
                    ctx,
                    turnContext,
                    systemPromptHash,
                    tools,
                    blockedToolsThisTurn,
                    SessionStatus.Running,
                    ct);
                _scheduler.SatisfiedActionIds = _completedActionIds;
                var steps = await _scheduler.RunAsync(plan, ct);
                lock (_turnStateLock)
                {
                    SettleStepResults(
                        plan,
                        steps,
                        allStepResults,
                        allObs,
                        blockedToolsThisTurn,
                        msg);
                }
                bool completedBusinessMutation = plan.Steps
                    .Where(step => steps.Any(result =>
                        result.StepId == step.Id
                        && result.Status == StepResultStatus.Completed))
                    .Any(IsBusinessMutationStep);
                bool hostStateChanged = steps.Any(result =>
                    result.Status == StepResultStatus.Blocked
                    && result.Cause ==
                        StepOutcomeCause.HostStateInvalidated);
                if (completedBusinessMutation || hostStateChanged)
                {
                    // Never plan the next round from a pre-mutation map model.
                    // The same refresh reconciles manual edits that invalidated
                    // optimistic execution.
                    // 中文：绝不要使用变更前的地图模型规划下一轮。同一次刷新还能协调使乐观执行失效的人工编辑。
                    ctx = await _contextManager.GatherLightAsync(msg);
                    PopulateDomainContext(ctx);
                    _scheduler.SetDomainSnapshot(_session.Domain);
                    observedHostRevision = hostStateChanged
                        ? await _contextManager.CaptureDataRevisionAsync(ct)
                        : plan.DataRevision
                            ?? await _contextManager.CaptureDataRevisionAsync(ct);
                    RefreshScopeResourceHandles(
                        plan.ResolvedScope,
                        _session.Domain);
                    systemPrompt = SystemPrompt.Build(
                        ctx,
                        SystemPrompt.DetectResponseMode(msg));
                    systemPromptHash = ComputeSHA256(systemPrompt);
                    turnContext = _contextBudgeter.Build(
                        msg,
                        ctx,
                        _session.Messages);
                    _transcriptTurn?.WriteContext(
                        turn,
                        ctx.ProjectInfo,
                        ctx.MapInfo,
                        ctx.KnowledgeChunks.Count,
                        ctx.MatchedSkills.Count);
                }
                lock (_turnStateLock)
                {
                    PromoteResourceBindings(plan, steps, turn);
                    RecordCoverageSatisfaction(plan, allStepResults);
                }
                var approvalStep = steps.FirstOrDefault(sr => sr.Status == StepResultStatus.AwaitingApproval);
                if (approvalStep != null)
                {
                    SyncPlanStatuses(plan, steps);
                    var dataRevision = await _contextManager.CaptureDataRevisionAsync(ct);
                    BindApprovalToDataRevision(plan, dataRevision);
                    PublishApprovalBound(turn, plan, dataRevision);
                    var pendingTools = steps
                        .Where(sr => sr.Status == StepResultStatus.AwaitingApproval)
                        .Select(sr => sr.ToolName)
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    _session.SetStatus(SessionStatus.WaitingForApproval);
                    _sessionTotalMs += sessionSw.ElapsedMilliseconds;
                    await SaveCheckpointAsync(turn, round, msg, ctx, turnContext, systemPromptHash, tools, blockedToolsThisTurn, _session.Status, ct);
                    RecordTurnToolCalls(allStepResults);

                    string toolText = pendingTools.Count > 0
                        ? string.Join("、", pendingTools)
                        : approvalStep.ToolName;
                    string reason = string.IsNullOrWhiteSpace(approvalStep.ApprovalReason)
                        ? "该操作会修改或删除现有数据，需要用户授权。"
                        : approvalStep.ApprovalReason;

                    return new AgentResponse
                    {
                        UserReply = $"需要授权后才能继续执行：{toolText}。\n{reason}\n请在窗口中选择\"批准并继续\"或\"拒绝\"。",
                        ExecutionSummary = BuildSummary(allStepResults),
                        HadToolCalls = true,
                        StopReason = "WaitingForApproval",
                        RequiresApproval = true,
                        ApprovalKind = approvalStep.ApprovalKind,
                        PendingApproval = approvalStep.ApprovalTicket,
                        PendingTools = pendingTools,
                        RequiredPermissionLevel = approvalStep.RequiredPermissionLevel,
                        ToolResults = CollectToolResults(allStepResults)
                    };
                }
                var settledCompletion = CompletionReportBuilder.Build(
                    msg,
                    allStepResults,
                    _currentGoalScope,
                    _currentGoalCoverage);
                if (_goalControlPlane.Decide(
                        _currentGoalScope,
                        _currentGoalCoverage,
                        settledCompletion.IsComplete)
                    == GoalControlState.Closed)
                {
                    return await CompleteSettledGoalAsync(
                        turn,
                        round + 1,
                        msg,
                        ctx,
                        turnContext,
                        systemPromptHash,
                        tools,
                        blockedToolsThisTurn,
                        settledCompletion,
                        sessionSw,
                        ct);
                }
                PublishGoalResidual(
                    turn,
                    _currentGoalScope,
                    _currentGoalCoverage,
                    settledCompletion);
                foreach (var sr in steps)
                {
                    // ★ Write validation results to append-only ledger
                    // ★ Sync plan step status from execution result
                    // ★ Audit AFTER execution, based on real status
                    // 中文：将验证结果写入仅追加账本；同步计划步骤状态与执行结果；基于实际状态执行事后审计
                }

                // ★ Feed results back to LLM: assistant msg with tool_calls + tool results with tool_call_id
                // 中文：反馈结果给LLM：助理消息，tool_calls+工具结果，使用tool_call_id
                messages.Add(new Message
                {
                    Role = "assistant",
                    Content = llmResp.Content,
                    ReasoningContent = llmResp.ReasoningContent,
                    ToolCalls = responseToolCalls
                });
                var feedbackSteps = ReconcileToolCallFeedback(
                    responseToolCalls,
                    plan,
                    steps,
                    turn);
                var feedbackOnlyResults = feedbackSteps
                    .Where(feedback => !steps.Any(result =>
                        ReferenceEquals(result, feedback)))
                    .ToList();
                if (feedbackOnlyResults.Count > 0)
                {
                    lock (_turnStateLock)
                        MergeStepResults(
                            allStepResults,
                            feedbackOnlyResults);
                }
                foreach (StepExecutionResult sr in feedbackSteps)
                {
                    var o = sr.Observation;
                    string s = _observationCompressor.Format(_observationCompressor.Compress(sr, o));
                    messages.Add(new Message
                    {
                        Role = "tool",
                        ToolCallId = sr.ToolCallId,
                        Content = s
                    });
                }
                messages.Add(new Message
                {
                    Role = "user",
                    Content = RuntimePolicy.BuildContinuationPrompt(
                        round,
                        feedbackSteps,
                        blockedToolsThisTurn,
                        msg)
                });

                await SaveCheckpointAsync(turn, round + 1, msg, ctx, turnContext, systemPromptHash, tools, blockedToolsThisTurn, _session.Status, ct);
            }

            // ★ P1-4：turn 收尾——清除本轮 agent 自建且未清的选择集（终态保留需用户明确选择意图）
            // English：P1-4: turn to wrap up—clears the current round of agents' self-created and uncleared selection sets (retaining the terminal state requires the user's clear choice intention)
            await ClearAgentSelectionsAsync(allStepResults, turn, msg, ct);

            // Max rounds reached → Synthesize
            // 中文：最大弹药达到→合成
            if (allStepResults.Any(s => s.Status == StepResultStatus.Blocked) &&
                !allStepResults.Any(s => s.Status == StepResultStatus.Completed))
            {
                EmitProgress("生成受限回复");
                string blockedReply = BuildConservativeBlockedReply(allStepResults);
                _transcriptTool?.WriteSynthOutput(
                    turn,
                    blockedReply,
                    0);
                _session.SetStatus(SessionStatus.Completed);
                _session.AddMessage(new Message { Role = "assistant", Content = blockedReply });
                _session.Plan = null;
                _sessionTotalMs += sessionSw.ElapsedMilliseconds;
                await SaveCheckpointAsync(turn, maxRounds, msg, ctx, turnContext, systemPromptHash, tools, blockedToolsThisTurn, _session.Status, ct);
                RecordTurnToolCalls(allStepResults);
                return new AgentResponse
                {
                    UserReply = blockedReply,
                    ExecutionSummary = BuildSummary(allStepResults),
                    HadToolCalls = true,
                    StopReason = "SafetyBlocked",
                    CanContinue = false, // 所有步骤均被阻止，无法继续。 / All steps are blocked; nothing can continue.
                    ToolResults = CollectToolResults(allStepResults)
                };
            }

            // ★ 操作链预测：记录本回合调用的工具名，下一回合 SelectExposedToolsTiered 使用
            // English：Chain of operation prediction: Record the name of the tool called in this round, and use it in the next round with SelectExposedToolsTiered
            RecordTurnToolCalls(allStepResults);

            EmitProgress("生成回复");
            var completion = CompletionReportBuilder.Build(
                msg,
                allStepResults,
                _currentGoalScope,
                _currentGoalCoverage);
            string reply = CompletionReportBuilder.Render(completion);
            FinalizeGoalLifecycle(completion, _currentGoalScope);
            _transcriptTool?.WriteSynthOutput(turn, reply, 0);
            _session.SetStatus(SessionStatus.Completed);
            _session.AddMessage(new Message { Role = "assistant", Content = reply });
            _session.Plan = null;
            _sessionTotalMs += sessionSw.ElapsedMilliseconds;
            await SaveCheckpointAsync(turn, maxRounds, msg, ctx, turnContext, systemPromptHash, tools, blockedToolsThisTurn, _session.Status, ct);
            return new AgentResponse
            {
                UserReply = reply,
                ExecutionSummary = BuildSummary(allStepResults),
                HadToolCalls = true,
                StopReason = "达到最大轮次",
                CanContinue = !completion.IsComplete,
                ToolResults = CollectToolResults(allStepResults)
            };
        }

        private string BuildSemanticEmptyFallback(
            string userMessage,
            IReadOnlyList<StepExecutionResult> stepResults)
        {
            const string failureNotice =
                "模型连续返回空响应。本轮保持为失败且可续跑状态，未把空响应当作任务完成。";
            bool hasEvidence = stepResults.Count > 0
                || _currentGoalScope?.ProofObligations.Count > 0
                || _currentGoalCoverage?.GoalTargets.Count > 0;
            if (!hasEvidence)
                return failureNotice + " 请重试。";

            GoalCompletionReport report = CompletionReportBuilder.Build(
                userMessage,
                stepResults,
                _currentGoalScope,
                _currentGoalCoverage);
            return failureNotice
                + Environment.NewLine
                + "以下是运行时依据已落盘工具结果和验证证据生成的确定性状态："
                + Environment.NewLine
                + CompletionReportBuilder.Render(report);
        }

        // ================================================================
        // Synthesis
        // ================================================================
        // 中文：================================================================ 综合================================================================
        async Task<string> Synth(
            string question,
            IReadOnlyList<IObservation> obs,
            IReadOnlyList<StepExecutionResult> steps,
            ContextBundle bundle,
            int turn,
            CancellationToken cancellation)
        {
            var parts = new List<string>();
            if (bundle.Packets.Count > 0)
            {
                parts.Add("[上下文摘要]\n" + string.Join("\n\n",
                    bundle.Packets.Take(4).Select(p => $"{p.Title}\n{p.Body}")));
            }

            bool hasFailures = false;
            var summaries = new List<object>();
            if (steps.Count > 0)
            {
                bool hasStepIssues = steps.Any(s =>
                    s.Status != StepResultStatus.Completed ||
                    s.Validations.Any(v => v.Verdict == ValidationVerdict.FAIL));
                if (hasStepIssues) hasFailures = true;

                parts.Add("[执行步骤账本]\n" + string.Join("\n", steps.Select(FormatStepForSynthesis)));
                summaries.AddRange(steps.Select(s => new
                {
                    step = s.StepId,
                    tool = s.ToolName,
                    status = s.Status.ToString(),
                    target = s.Effect?.Target,
                    inputs = s.InputParameters,
                    awaitingApproval = s.Status == StepResultStatus.AwaitingApproval,
                    approvalReason = s.ApprovalReason,
                    blocked = s.Status == StepResultStatus.Blocked,
                    blockReason = s.BlockReason,
                    error = s.ToolResult?.Error,
                    validations = s.Validations.Select(v => new
                    {
                        rule = v.RuleName,
                        verdict = v.Verdict.ToString(),
                        detail = v.Detail
                    }).ToList()
                }));
            }

            foreach (var o in obs)
            {
                bool failed = o.Warnings.Count > 0;
                if (failed) hasFailures = true;
                string raw = string.IsNullOrEmpty(o.Output.Raw) ? "" :
                    o.Output.Raw.Length > 1200 ? o.Output.Raw[..1200] + "…" : o.Output.Raw;
                parts.Add($"【{o.ToolName} {(failed ? "❌" : "✅")}】\n{raw}");
                if (failed) parts.Add($"⚠️ {string.Join("; ", o.Warnings)}");
                summaries.Add(new { tool = o.ToolName, verified = !failed, warnings = o.Warnings });
            }

            _transcriptTool?.WriteSynthInput(turn, summaries, hasFailures, null);

            if (parts.Count == 0) return "未获取到数据。";

            string failNote = hasFailures
                ? "\n⚠️ 标注了🚫/❌的操作未生效。如实告知用户哪些成功、哪些被阻止或失败；用户要求生成新图层时，被阻止/失败不能说成已完成。"
                : "";
            string rules = "纯文本。直接说结果。逐项对应用户目标、实际目标和验证结果；不确定的事如实说。";

            try
            {
                using var synthSpan = CorrelationContext.BeginSpan("synth");
                var synthSw = Stopwatch.StartNew();
                var r = await _llm.ChatAsync(
                    new List<Message> { new() { Role = "user", Content = $"用户问:{question}\n数据:\n{string.Join("\n\n", parts)}{failNote}\n{rules}" } },
                    "规划师。直接回答。验证失败就如实说。", temperature: 0.5, maxTokens: 2048,
                    cancellation: cancellation);
                synthSw.Stop();
                LastTurnTokens.PromptTokens = r.PromptTokens;
                LastTurnTokens.CompletionTokens = r.CompletionTokens;
                _session.TokenUsage.PromptTokens += r.PromptTokens;
                _session.TokenUsage.CompletionTokens += r.CompletionTokens;
                string c = r.Content?.Trim() ?? "";
                _transcriptTool?.WriteSynthOutput(turn, c, synthSw.ElapsedMilliseconds);
                if (!string.IsNullOrEmpty(c)) return c;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                AgentDiagnostics.Warn("AgentRuntime.Synth",
                    "合成 LLM 调用失败，回退到原始观测拼接", ex);
            }
            var fallback = new List<string>();
            foreach (var o in obs) if (!string.IsNullOrEmpty(o.Output.Raw)) fallback.Add(o.Output.Raw.Length > 200 ? o.Output.Raw[..200] : o.Output.Raw);
            return fallback.Count > 0 ? string.Join("\n", fallback) : "完成。";

            static string FormatStepForSynthesis(StepExecutionResult step)
            {
                string detail = step.Status switch
                {
                    StepResultStatus.AwaitingApproval when !string.IsNullOrWhiteSpace(step.ApprovalReason) => $"：{step.ApprovalReason}",
                    StepResultStatus.Blocked when !string.IsNullOrWhiteSpace(step.BlockReason) => $"：{step.BlockReason}",
                    StepResultStatus.Failed when !string.IsNullOrWhiteSpace(step.ToolResult?.Error) => $"：{step.ToolResult.Error}",
                    StepResultStatus.Skipped when !string.IsNullOrWhiteSpace(step.BlockReason) => $"：{step.BlockReason}",
                    _ => ""
                };
                string validation = step.Validations.Count == 0
                    ? ""
                    : $"；验证：{string.Join("，", step.Validations.Select(v => $"{v.RuleName}/{v.Verdict}: {v.Detail}"))}";
                return $"{RuntimePolicy.StepStatusText(step.Status)}{step.ToolName}{detail}{validation}";
            }
        }

        public void WriteSessionEnd() => _transcriptSession?.WriteSessionEnd(
            _session.ThreadId,
            _turnCount,
            _session.Observations.Count,
            _session.Observations.Count(o => o.Warnings.Count > 0),
            _session.Validations.Count(v => v.Verdict == ValidationVerdict.FAIL),
            _sessionTotalMs);

        public void RecordHostShutdown(string reason, bool hadActiveRun)
        {
            _hostShutdownReason = reason;
            Interlocked.Exchange(ref _hostShutdownRequested, 1);
            if (hadActiveRun)
                _session.SetStatus(SessionStatus.Interrupted);
            _transcriptSession?.WriteHostShutdown(
                _session.ThreadId,
                reason,
                _session.Status,
                hadActiveRun);
        }

        public void RecordHostShutdownObserved(string reason, bool hadActiveRun)
        {
            _hostShutdownObservedReason = reason;
            _transcriptSession?.WriteHostShutdownObserved(
                _session.ThreadId,
                reason,
                _session.Status,
                hadActiveRun);
        }

        public void SaveHostShutdownCheckpointCritical(
            bool terminal,
            string? reason = null)
        {
            if (_checkpointStore == null)
                return;
            var state = BuildHostShutdownState(terminal, reason);
            if (_checkpointStore is ICriticalRunCheckpointStore critical)
                critical.SaveCritical(state);
            else
                _checkpointStore.SaveAsync(state).GetAwaiter().GetResult();
            PublishHostCheckpointSaved(state);
        }

        public async Task SaveHostShutdownCheckpointAsync(CancellationToken cancellation = default)
        {
            if (_checkpointStore == null)
                return;
            var state = BuildHostShutdownState(
                terminal: true,
                _hostShutdownReason);
            await _checkpointStore.SaveAsync(state, cancellation);
            PublishHostCheckpointSaved(state);
        }

        private AgentRunState BuildHostShutdownState(
            bool terminal,
            string? reason)
        {
            List<StepExecutionRecord> durableResults;
            List<ObservationRecord> durableObservations;
            List<DataLineageRecord> lineage;
            lock (_turnStateLock)
            {
                durableResults = _allStepResultsThisTurn
                    .Select(ToRecord)
                    .ToList();
                durableObservations = _allObsThisTurn
                    .Select(ToRecord)
                    .ToList();
                lineage = CaptureLineage();
            }
            var plan = _session.Plan;
            return new AgentRunState
            {
                ThreadId = _session.ThreadId,
                Turn = _turnCount,
                // 98 is the reversible close observation, 99 the terminal
                // barrier. Both sort after normal planner rounds.
                // 中文：98是可逆的近距离观测，99是终极屏障。两者都是在正常的计划轮次后分拣。
                Round = terminal ? 99 : 98,
                UserMessage = _session.Messages.LastOrDefault(message => message.Role == "user")?.Content ?? "",
                Status = _session.Status,
                PermissionLevel = _session.PermissionLevel,
                ProjectName = _session.Domain.Project.Name,
                ProjectPath = _session.Domain.Project.Path,
                DefaultGdb = _session.Domain.Project.DefaultGdb,
                MapName = _session.Domain.Map.Name,
                Plan = plan,
                GoalScope = _currentGoalScope ?? plan?.ResolvedScope,
                PreviousGoalScope = _lastCompletedGoalScope,
                GoalCoverage = _currentGoalCoverage,
                TerminationReason = reason
                    ?? (terminal
                        ? _hostShutdownReason ?? "HostShutdown"
                        : _hostShutdownObservedReason ?? "HostShutdownObserved"),
                IsSessionTerminal = terminal,
                SessionLeaseId = _sessionLease?.LeaseId ?? "",
                SessionLeaseOwnerId = _sessionLease?.OwnerId ?? "",
                Ledger = new PlanLedger
                {
                    PlanId = plan?.PlanId ?? "",
                    Version = plan?.Version ?? 1,
                    Goal = plan?.Goal ?? "",
                    UpdatedAtUtc = DateTime.UtcNow,
                    Results = durableResults
                },
                StepResults = durableResults,
                Observations = durableObservations,
                Messages = _session.Messages.ToList(),
                PendingApproval = plan?.Steps
                    .Where(step => step.Status == PlanStepStatus.AwaitingApproval)
                    .Select(step => step.PendingApproval)
                    .FirstOrDefault(ticket => ticket != null),
                PendingTools = plan?.Steps
                    .Where(step =>
                        step.Status == PlanStepStatus.AwaitingApproval
                        && step.ToolCall != null)
                    .Select(step => step.ToolCall!.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList() ?? new List<string>(),
                Lineage = lineage,
                Artifacts = _session.Domain.Artifacts.ToList(),
                ElapsedMs = _sessionTotalMs,
                SavedAt = DateTime.UtcNow
            };
        }

        private void PublishHostCheckpointSaved(AgentRunState state)
        {
            _eventBus?.Publish(new AgentEvent
            {
                EventType = "checkpoint.saved",
                SessionId = state.ThreadId,
                Turn = state.Turn,
                Metadata = new()
                {
                    ["thread"] = state.ThreadId,
                    ["round"] = state.Round.ToString(),
                    ["status"] = state.Status.ToString(),
                    ["termination_reason"] = state.TerminationReason ?? ""
                }
            });
        }

        /// <summary>
        /// Populate DomainContext with structured fields from AgentContext.
        /// Consumes structured fields (populated by ContextManager during gather)
        /// rather than parsing the ProjectInfo/MapInfo text blobs.
        /// Layers, Selection, Workspace await structured IMapReader DTO methods.
        /// </summary>
        /// <remarks>中文：用AgentContext中的结构化字段填充DomainContext。它消耗结构化字段（采集时由 ContextManager 填充），而不是解析 ProjectInfo/MapInfo 的文本块。图层、选择、工作区等待结构化的IMapReader DTO方法。</remarks>
        private void PopulateDomainContext(AgentContext ctx)
        {
            var domain = _session.Domain;

            if (!string.IsNullOrWhiteSpace(ctx.ProjectName))
                domain.Project.Name = ctx.ProjectName;
            if (!string.IsNullOrWhiteSpace(ctx.ProjectPath))
                domain.Project.Path = ctx.ProjectPath;
            if (!string.IsNullOrWhiteSpace(ctx.GdbPath))
                domain.Project.DefaultGdb = ctx.GdbPath;
            if (!string.IsNullOrWhiteSpace(ctx.MapName))
                domain.Map.Name = ctx.MapName;
            domain.Map.SpatialReference = ctx.SpatialRef.MapSpatialRefLabel;
            foreach (var layer in ctx.Layers)
            {
                layer.LineageRoot = ResolveLineageRoot(layer.Name);
                layer.LineageDepth = ResolveLineageDepth(layer.Name);
            }
            domain.Layers = ctx.Layers.Select(layer => new LayerInfo
            {
                Name = layer.Name,
                MapMemberId = layer.MapMemberId,
                DatasetId = layer.DatasetId,
                DatasetName = layer.DatasetName,
                GeometryType = layer.GeometryType,
                FeatureCount = layer.FeatureCount,
                Fields = layer.Fields.ToList(),
                SchemaFingerprint = layer.SchemaFingerprint,
                StateFingerprint = layer.StateFingerprint,
                StateFingerprintReliable = layer.StateFingerprintReliable,
                LineageRoot = layer.LineageRoot,
                LineageDepth = layer.LineageDepth
            }).ToList();
        }

        private async Task SaveCheckpointAsync(
            int turn,
            int round,
            string userMessage,
            AgentContext ctx,
            ContextBundle bundle,
            string systemPromptHash,
            IReadOnlyList<IToolDescriptor> tools,
            IReadOnlyCollection<string> blockedTools,
            SessionStatus status,
            CancellationToken cancellation = default)
        {
            if (_checkpointStore == null)
                return;

            try
            {
                List<StepExecutionRecord> durableResults;
                List<ObservationRecord> durableObservations;
                List<DataLineageRecord> lineage;
                lock (_turnStateLock)
                {
                    durableResults = _allStepResultsThisTurn
                        .Select(ToRecord)
                        .ToList();
                    durableObservations = _allObsThisTurn
                        .Select(ToRecord)
                        .ToList();
                    lineage = CaptureLineage();
                }
                if (Volatile.Read(ref _hostShutdownRequested) == 1)
                    status = SessionStatus.Interrupted;
                var state = new AgentRunState
                {
                    ThreadId = _session.ThreadId,
                    Turn = turn,
                    Round = round,
                    UserMessage = userMessage,
                    AppendUserMessage = true,
                    Status = status,
                    PermissionLevel = _session.PermissionLevel,
                    SystemPromptHash = systemPromptHash,
                    ContextHash = bundle.ContextHash,
                    ProjectName = _session.Domain.Project.Name,
                    ProjectPath = _session.Domain.Project.Path,
                    DefaultGdb = _session.Domain.Project.DefaultGdb,
                    MapName = _session.Domain.Map.Name,
                    TerminationReason = Volatile.Read(ref _hostShutdownRequested) == 1
                        ? _hostShutdownReason
                        : null,
                    IsSessionTerminal = false,
                    SessionLeaseId = _sessionLease?.LeaseId ?? "",
                    SessionLeaseOwnerId = _sessionLease?.OwnerId ?? "",
                    DataRevision = _session.Plan?.DataRevision,
                    Plan = _session.Plan,
                    GoalScope = _currentGoalScope ?? _session.Plan?.ResolvedScope,
                    PreviousGoalScope = _lastCompletedGoalScope,
                    GoalCoverage = _currentGoalCoverage,
                    Ledger = new PlanLedger
                    {
                        PlanId = _session.Plan?.PlanId ?? "",
                        Version = _session.Plan?.Version ?? 1,
                        Goal = _session.Plan?.Goal ?? userMessage,
                        ProjectRevision = _session.Plan?.ProjectRevision ?? bundle.ContextHash,
                        UpdatedAtUtc = DateTime.UtcNow,
                        Results = durableResults
                    },
                    StepResults = durableResults,
                    Observations = durableObservations,
                    Messages = _session.Messages.ToList(),
                    PendingApproval = _session.Plan?.Steps
                        .Where(s => s.Status == PlanStepStatus.AwaitingApproval)
                        .Select(s => s.PendingApproval)
                        .FirstOrDefault(t => t != null),
                    PendingTools = _session.Plan?.Steps
                        .Where(s => s.Status == PlanStepStatus.AwaitingApproval && s.ToolCall != null)
                        .Select(s => s.ToolCall!.Name)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList() ?? new List<string>(),
                    BlockedTools = blockedTools.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    BlockedCallSignatures = _blockedCallSignaturesThisTurn.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    ToolExposure = tools.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    Lineage = lineage,
                    Artifacts = _session.Domain.Artifacts.ToList(),
                    ElapsedMs = _sessionTotalMs,
                    SavedAt = DateTime.UtcNow
                };

                await _checkpointStore.SaveAsync(state, cancellation);
                _eventBus?.Publish(new AgentEvent
                {
                    EventType = "checkpoint.saved",
                    SessionId = _session.ThreadId,
                    Turn = turn,
                    Metadata = new()
                    {
                        ["thread"] = _session.ThreadId,
                        ["round"] = round.ToString(),
                        ["status"] = status.ToString(),
                        ["pending_approval"] = (state.PendingApproval != null).ToString()
                    }
                });
            }
            catch
            {
                AgentDiagnostics.Warn("AgentRuntime.Checkpoint", "Checkpoint save failed", eventBus: _eventBus);
            }
        }

        /// <summary>
        /// P1-4：turn 结束时清除 agent 本轮自建且未收尾的选择集（终态选择保留，
        /// 用户手工选择不受影响——此处只看本轮执行过的工具调用）。
        /// 走注册表内的标准工具执行，并写入 transcript 保持全程可追溯。
        /// </summary>
        /// <remarks>English：P1-4: At the end of turn, clear the agent's self-built and unfinished selection set (terminal state selection retained, user manual selection unaffected—here only the tool calls executed in this round are considered). Execute using standard tools within the registry and write transcripts to maintain full traceability.</remarks>
        private async Task ClearAgentSelectionsAsync(
            IReadOnlyList<StepExecutionResult> allStepResults, int turn, string userMessage, CancellationToken ct)
        {
            try
            {
                var executed = allStepResults
                    .Where(sr => sr.Status == StepResultStatus.Completed)
                    .Select(sr => (sr.ToolName, (IReadOnlyDictionary<string, object?>?)sr.InputParameters))
                    .ToList();

                var layers = RuntimePolicy.ResolveSelectionCleanups(executed, userMessage);
                foreach (string layer in layers)
                {
                    ct.ThrowIfCancellationRequested();

                    // 工具名/参数知识单点在 RuntimePolicy（审查项 F1）
                    // English：Toolname/parameter knowledge single point in RuntimePolicy (review item F1)
                    var call = RuntimePolicy.BuildSelectionClearCall(layer);

                    _transcriptTool?.WriteToolStart(turn, 0, call.Name, call.Arguments);
                    var sw = Stopwatch.StartNew();
                    var result = await _tools.ExecuteAsync(call, ct);
                    sw.Stop();
                    _transcriptTool?.WriteToolEnd(turn, 0, call.Name, result.Success,
                        result.Output, result.Error, sw.ElapsedMilliseconds, 0);

                    _eventBus?.Publish(new AgentEvent
                    {
                        EventType = "selection.autocleared",
                        Turn = turn,
                        Metadata = new()
                        {
                            ["layer"] = layer,
                            ["success"] = result.Success.ToString()
                        }
                    });
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AgentDiagnostics.Warn("AgentRuntime.SelectionCleanup", "选择集自动清除失败", ex, _eventBus);
            }
        }

        private void ApplyCheckpointState(AgentRunState state)
        {
            if (string.IsNullOrWhiteSpace(state.ThreadId))
                return;

            _turnCount = state.Turn;
            _sessionTotalMs = state.ElapsedMs;
            _session.ResetConversation(state.ThreadId);
            foreach (var message in state.Messages)
                _session.AddMessage(message);

            _session.PermissionLevel = state.PermissionLevel;
            _session.Plan = state.Plan;
            _session.SetStatus(state.Status);
            _blockedToolsThisTurn = new HashSet<string>(state.BlockedTools ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            _blockedCallSignaturesThisTurn = new HashSet<string>(state.BlockedCallSignatures ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            _allStepResultsThisTurn = (state.StepResults ?? new List<StepExecutionRecord>())
                .Select(FromRecord)
                .ToList();
            _allObsThisTurn = (state.Observations ?? new List<ObservationRecord>())
                .Select(FromRecord)
                .Cast<IObservation>()
                .ToList();
            foreach (var observation in _allObsThisTurn)
                _session.AddObservation(observation);
            foreach (var result in _allStepResultsThisTurn)
            {
                foreach (var validation in result.Validations)
                    _session.AddValidation(validation);
                if (state.Plan != null)
                    _settledStepResults.Add(
                        $"{state.Plan.PlanId}:{result.StepId}:{result.Status}");
                if (result.IsSuccessful
                    && !string.IsNullOrWhiteSpace(result.ActionId))
                    _completedActionIds.Add(result.ActionId);
            }
            foreach (var lineage in state.Lineage ?? new List<DataLineageRecord>())
            {
                var normalized = NormalizeLineage(lineage);
                if (normalized == null)
                    continue;
                _lineageParents[normalized.Output] = normalized.Parent;
                _lineageRecords[normalized.Output] = normalized;
            }
            _session.Domain.Artifacts =
                (state.Artifacts ?? new List<DomainArtifactRecord>())
                .ToList();
            _currentGoalScope = state.GoalScope
                ?? state.Plan?.ResolvedScope;
            _lastCompletedGoalScope = state.PreviousGoalScope;
            _currentGoalCoverage = state.GoalCoverage
                ?? state.Plan?.Coverage;
            _scheduler.CurrentTurn = state.Turn;
            _scheduler.CurrentPermissionLevel = state.PermissionLevel;

            // Restore structured domain state so checkpoint recovery
            // has the same project/map context that was saved.
            // 中文：恢复结构化域状态，使检查点恢复拥有保存的项目/地图上下文。
            if (!string.IsNullOrWhiteSpace(state.ProjectName))
                _session.Domain.Project.Name = state.ProjectName;
            if (!string.IsNullOrWhiteSpace(state.ProjectPath))
                _session.Domain.Project.Path = state.ProjectPath;
            if (!string.IsNullOrWhiteSpace(state.DefaultGdb))
                _session.Domain.Project.DefaultGdb = state.DefaultGdb;
            if (!string.IsNullOrWhiteSpace(state.MapName))
                _session.Domain.Map.Name = state.MapName;
            _scheduler.SetDomainSnapshot(_session.Domain);
        }

        /// <summary>
        /// ★ 操作链预测（F1 修复，2026-07-20）：
        /// 从本轮所有执行步骤中提取工具名，供下一回合 PredictNextTools 使用。
        /// 必须在所有退出路径上调用——不限于达到最大轮次时。
        /// 原实现仅在 max-round 合成路径赋值，导致 Path D 预测机制在主成功路径
        /// （正常结束、AwaitingApproval、SafetyBlocked 等）基本不生效。
        /// </summary>
        /// <remarks>English：Chain of Operation Prediction (F1 fix, 2026-07-20): Extract tool names from all execution steps in this round for use by PredictNextTools in the next round. It must be called on all exit paths—not limited to when the maximum round is reached. The original implementation only assigns values to max-round synthesis paths, resulting in the Path D prediction mechanism being basically ineffective on the main success path (such as normal completion, Awaiting Approval, SafetyBlocked, etc.).</remarks>
        private void RecordTurnToolCalls(List<StepExecutionResult> allStepResults)
        {
            _lastTurnToolCalls = allStepResults
                .Where(s => !string.IsNullOrWhiteSpace(s.ToolName))
                .Select(s => s.ToolName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool IsBusinessMutationStep(PlanStep step)
        {
            if (step.WriteSet.Count > 0)
                return true;
            var effects = step.Effect?.Profile.Effects ?? DataEffectKind.None;
            return (effects & ~DataEffectKind.ReadData) != DataEffectKind.None;
        }

        private static void RefreshScopeResourceHandles(
            ResolvedGoalScope? scope,
            DomainContext domain)
        {
            if (scope == null)
                return;

            var refreshed = new List<GoalResourceHandle>();
            foreach (var handle in scope.ResourceHandles)
            {
                var current = domain.Layers.FirstOrDefault(layer =>
                    !string.IsNullOrWhiteSpace(handle.MapMemberId)
                    && layer.MapMemberId.Equals(
                        handle.MapMemberId,
                        StringComparison.OrdinalIgnoreCase))
                    ?? domain.Layers.FirstOrDefault(layer =>
                        !string.IsNullOrWhiteSpace(handle.DatasetId)
                        && layer.DatasetId.Equals(
                            handle.DatasetId,
                            StringComparison.OrdinalIgnoreCase))
                    ?? domain.Layers.FirstOrDefault(layer =>
                        layer.Name.Equals(
                            handle.DisplayName,
                            StringComparison.OrdinalIgnoreCase));
                if (current == null)
                {
                    refreshed.Add(handle);
                    continue;
                }

                refreshed.Add(new GoalResourceHandle
                {
                    DisplayName = current.Name,
                    MapMemberId = current.MapMemberId,
                    DatasetId = current.DatasetId,
                    SchemaFingerprint = current.SchemaFingerprint,
                    StateFingerprint = current.StateFingerprint,
                    StateFingerprintReliable =
                        current.StateFingerprintReliable,
                    LineageRoot = current.LineageRoot,
                    LineageDepth = current.LineageDepth
                });
            }

            scope.ResourceHandles = refreshed;
        }

        private void RecordCoverageSatisfaction(
            AgentPlan plan,
            IReadOnlyList<StepExecutionResult> results)
        {
            var scope = plan.ResolvedScope;
            var coverage = plan.Coverage;
            if (scope == null || coverage == null)
                return;

            GoalProgressUpdate progress = _goalProgressTracker.Update(
                scope,
                coverage,
                plan,
                results);
            var proofEvaluation = _goalClosureEvaluator.Evaluate(
                scope,
                coverage,
                results);
            scope.SatisfiedProofObligations =
                proofEvaluation.SatisfiedProofs.ToList();
            if (progress.NewlySatisfiedTargets.Count > 0
                || progress.IsGoalComplete)
            {
                _eventBus?.Publish(new AgentEvent
                {
                    EventType = "goal.progress",
                    Turn = _scheduler.CurrentTurn,
                    Metadata = new()
                    {
                        ["goal_id"] = scope.GoalId,
                        ["newly_satisfied"] =
                            progress.NewlySatisfiedTargets.Count.ToString(),
                        ["residual"] =
                            progress.ResidualTargets.Count.ToString(),
                        ["complete"] =
                            progress.IsGoalComplete.ToString()
                    },
                    Data = new()
                    {
                        ["newly_satisfied_targets"] =
                            progress.NewlySatisfiedTargets,
                        ["residual_targets"] = progress.ResidualTargets
                    }
                });
            }
        }

        private void PromoteResourceBindings(
            AgentPlan plan,
            IReadOnlyList<StepExecutionResult> results,
            int turn)
        {
            ResolvedGoalScope? scope = plan.ResolvedScope;
            if (scope == null || scope.ResourceBindings.Count == 0)
                return;
            ResourceBindingPromotionResult promotion =
                _resourceBindingEvidencePromoter.Evaluate(
                    scope,
                    plan,
                    results,
                    _session.Domain);
            if (promotion.Evidence.Count == 0)
                return;

            ResourceBindingPatchApplier.Apply(
                scope,
                promotion,
                _session.Domain);
            plan.Coverage = _coverageValidator.Validate(
                scope,
                plan,
                _session.Domain);
            _currentGoalScope = scope;
            _currentGoalCoverage = plan.Coverage;
            _eventBus?.Publish(new AgentEvent
            {
                EventType = "goal.resource_binding_evidence",
                Turn = turn,
                Metadata = new()
                {
                    ["goal_id"] = scope.GoalId,
                    ["evidence_count"] = promotion.Evidence.Count.ToString(),
                    ["patch_count"] = promotion.Patches.Count.ToString(),
                    ["resolved_count"] = promotion.Patches.Count(patch =>
                        patch.NextStatus ==
                            GoalResourceResolutionStatus.Resolved).ToString()
                },
                Data = new()
                {
                    ["evidence"] = promotion.Evidence,
                    ["patches"] = promotion.Patches
                }
            });
        }

        private async Task<AgentResponse> CompleteSettledGoalAsync(
            int turn,
            int round,
            string userMessage,
            AgentContext context,
            ContextBundle contextBundle,
            string systemPromptHash,
            IReadOnlyList<IToolDescriptor> tools,
            IReadOnlyCollection<string> blockedTools,
            GoalCompletionReport completion,
            Stopwatch sessionStopwatch,
            CancellationToken cancellation)
        {
            ResolvedGoalScope? completedScope = _currentGoalScope;
            TargetCoverageResult? completedCoverage =
                _currentGoalCoverage;
            string reply = CompletionReportBuilder.Render(completion);
            _eventBus?.Publish(new AgentEvent
            {
                EventType = "goal.closed",
                Turn = turn,
                Metadata = new()
                {
                    ["goal_id"] = completedScope?.GoalId ?? "",
                    ["outcome"] = completion.Outcome.ToString(),
                    ["target_count"] =
                        (completedCoverage?.GoalTargets.Count ?? 0)
                        .ToString()
                },
                Data = new()
                {
                    ["satisfied_targets"] =
                        completedCoverage?.SatisfiedTargets
                        ?? new List<string>(),
                    ["proofs"] =
                        completedScope?.SatisfiedProofObligations
                        ?? new List<string>()
                }
            });
            FinalizeGoalLifecycle(completion, completedScope);
            _transcriptTool?.WriteSynthOutput(turn, reply, 0);
            _session.SetStatus(SessionStatus.Completed);
            _session.AddMessage(new Message
            {
                Role = "assistant",
                Content = reply
            });
            _session.Plan = null;
            await ClearAgentSelectionsAsync(
                _allStepResultsThisTurn,
                turn,
                userMessage,
                cancellation);
            _sessionTotalMs += sessionStopwatch.ElapsedMilliseconds;
            await SaveCheckpointAsync(
                turn,
                round,
                userMessage,
                context,
                contextBundle,
                systemPromptHash,
                tools,
                blockedTools,
                _session.Status,
                cancellation);
            RecordTurnToolCalls(_allStepResultsThisTurn);
            return new AgentResponse
            {
                UserReply = reply,
                ExecutionSummary =
                    BuildSummary(_allStepResultsThisTurn),
                HadToolCalls = _allStepResultsThisTurn.Count > 0,
                StopReason = StopReasonFor(completion.Outcome),
                CanContinue = false,
                ToolResults = CollectToolResults(
                    _allStepResultsThisTurn)
            };
        }

        private void PublishGoalResidual(
            int turn,
            ResolvedGoalScope? scope,
            TargetCoverageResult? coverage,
            GoalCompletionReport completion)
        {
            if (_eventBus == null || scope == null
                || completion.IsComplete)
            {
                return;
            }
            _eventBus.Publish(new AgentEvent
            {
                EventType = "goal.residual",
                Turn = turn,
                Metadata = new()
                {
                    ["goal_id"] = scope.GoalId,
                    ["residual_target_count"] =
                        (coverage?.ExpectedTargets.Count ?? 0)
                        .ToString(),
                    ["unresolved_requirement_count"] =
                        scope.UnresolvedRequirements.Count.ToString()
                },
                Data = new()
                {
                    ["residual_targets"] =
                        coverage?.ExpectedTargets ?? new List<string>(),
                    ["unresolved_requirements"] =
                        scope.UnresolvedRequirements,
                    ["missing_proofs"] = completion.MissingProofs
                }
            });
        }

        void EmitProgress(string d) => OnProgress?.Invoke(this, d);

        private async Task<LLMResponse> AwaitLlmWithHeartbeatAsync(
            Task<LLMResponse> request,
            int turn,
            int round,
            string phase,
            CancellationToken cancellation)
        {
            var elapsed = Stopwatch.StartNew();
            while (!request.IsCompleted)
            {
                Task completed = await Task.WhenAny(
                    request,
                    Task.Delay(TimeSpan.FromSeconds(5), cancellation));
                if (completed == request)
                    break;
                cancellation.ThrowIfCancellationRequested();
                long seconds = Math.Max(1, (long)elapsed.Elapsed.TotalSeconds);
                EmitProgress($"AI 分析中（已等待 {seconds} 秒）");
                _eventBus?.Publish(new AgentEvent
                {
                    EventType = "llm.progress",
                    Turn = turn,
                    Metadata = new()
                    {
                        ["round"] = round.ToString(),
                        ["phase"] = phase,
                        ["elapsed_ms"] = elapsed.ElapsedMilliseconds.ToString(),
                        ["stalled"] = (seconds >= 20).ToString().ToLowerInvariant()
                    },
                    Data = new()
                    {
                        ["message"] = seconds >= 20
                            ? $"模型仍在推理，已等待 {seconds} 秒；可随时停止本轮。"
                            : $"模型正在推理，已等待 {seconds} 秒。"
                    }
                });
            }
            return await request;
        }

        static string ComputeSHA256(string i)
        { byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes(i)); return "sha256:" + Convert.ToHexString(h)[..12].ToLowerInvariant(); }

        private static bool IsResourceResolutionProbe(
            ResolvedGoalScope? scope)
        {
            if (scope == null)
                return false;
            return scope.UnresolvedTargets.Count > 0
                || scope.UnresolvedRequirements.Count > 0
                || scope.ResourceBindings.Any(binding => binding.Status is
                    GoalResourceResolutionStatus.Missing
                    or GoalResourceResolutionStatus.Ambiguous
                    or GoalResourceResolutionStatus.CandidateFoundAwaitingConfirmation);
        }

        private void AddRejectedCallSignature(PlanStep step)
        {
            if (step.ToolCall == null)
                return;
            string canonical = _tools.ResolveName(step.ToolCall.Name) ?? step.ToolCall.Name;
            string signature = ApprovalProtocol.ComputeCallSignature(
                canonical,
                step.ToolCall.Arguments);
            if (!string.IsNullOrWhiteSpace(signature))
                _blockedCallSignaturesThisTurn.Add(signature);
        }

        private async Task<IReadOnlyList<SubagentResult>> ReviewPlanAsync(
            AgentPlan plan,
            int requestedCount,
            string goal,
            CancellationToken cancellation)
        {
            var roles = requestedCount >= 2
                ? new[] { SubagentRole.SafetyReview, SubagentRole.Verification }
                : new[] { SubagentRole.SafetyReview };
            var tasks = roles.Select(role => new SubagentTask
            {
                Role = role,
                Goal = goal,
                Plan = plan,
                Snapshot = _session.Domain,
                TokenBudget = 1024,
                Timeout = TimeSpan.FromSeconds(5)
            }).ToList();
            var reviews = await _subagentCoordinator.RunAsync(
                tasks,
                maxConcurrency: Math.Min(2, requestedCount),
                cancellation);

            _eventBus?.Publish(new AgentEvent
            {
                EventType = "plan.reviewed",
                Turn = _turnCount,
                Metadata = new()
                {
                    ["plan_id"] = plan.PlanId,
                    ["reviewer_count"] = reviews.Count.ToString(),
                    ["finding_count"] = reviews.Sum(review => review.Findings.Count).ToString()
                },
                Data = new() { ["reviews"] = reviews }
            });
            return reviews;
        }

        private sealed record ReviewGateResult(
            bool CanExecute,
            IReadOnlyList<ReviewFinding> Findings);

        private static ReviewGateResult EvaluateReviewGate(
            IEnumerable<SubagentResult> reviews)
        {
            var actionable = reviews
                .SelectMany(review => review.StructuredFindings)
                .Where(finding =>
                    finding.Disposition is ReviewDisposition.Recompile
                        or ReviewDisposition.Block)
                .ToList();
            return new ReviewGateResult(actionable.Count == 0, actionable);
        }

        private void EmitReviewGate(
            int turn,
            AgentPlan plan,
            ReviewGateResult result)
        {
            _eventBus?.Publish(new AgentEvent
            {
                EventType = "plan.review_gate",
                Turn = turn,
                Metadata = new()
                {
                    ["plan_id"] = plan.PlanId,
                    ["can_execute"] = result.CanExecute.ToString(),
                    ["actionable_count"] = result.Findings.Count.ToString(),
                    ["block_count"] = result.Findings.Count(
                        finding => finding.Disposition == ReviewDisposition.Block).ToString(),
                    ["recompile_count"] = result.Findings.Count(
                        finding => finding.Disposition == ReviewDisposition.Recompile).ToString()
                },
                Data = new() { ["findings"] = result.Findings }
            });
        }

        private static string BuildReviewFeedback(ReviewGateResult result)
        {
            var findings = result.Findings.Select(finding =>
            {
                string step = finding.StepId.HasValue
                    ? $"步骤 {finding.StepId.Value}"
                    : "计划";
                return $"- {step} [{finding.Code}]: {finding.Message}";
            });
            return "只读审查门未通过，任何工具都没有执行。请修正以下问题并生成下一批安全、可执行且能推进目标的工具调用；"
                + "不要复用原计划，也不要声称任务已经完成：\n"
                + string.Join("\n", findings);
        }

        private void EmitPlanValidation(int turn, AgentPlan plan)
        {
            if (_eventBus == null || plan.Validation == null)
                return;

            _eventBus.Publish(new AgentEvent
            {
                EventType = "plan.validation",
                Turn = turn,
                Metadata = new()
                {
                    ["plan_id"] = plan.PlanId,
                    ["plan_version"] = plan.Version.ToString(),
                    ["is_valid"] = plan.Validation.IsValid.ToString(),
                    ["error_count"] = plan.Validation.Issues.Count(issue => issue.Severity == IssueSeverity.Error).ToString(),
                    ["warning_count"] = plan.Validation.Issues.Count(issue => issue.Severity == IssueSeverity.Warning).ToString(),
                    ["info_count"] = plan.Validation.Issues.Count(issue => issue.Severity == IssueSeverity.Info).ToString(),
                    ["admissibility"] =
                        plan.Admissibility?.Decision.ToString() ?? "Unknown",
                    ["can_execute"] =
                        (plan.Admissibility?.CanExecute
                            ?? plan.Validation.IsValid).ToString()
                },
                Data = new()
                {
                    ["issues"] = plan.Validation.Issues
                }
            });
        }

        private static string BuildPlanValidationFeedback(
            PlanValidationResult validation,
            PlanRecoveryObservation? recovery = null)
        {
            var errors = validation.Issues
                .Where(issue => issue.Severity == IssueSeverity.Error)
                .Select(issue =>
                {
                    string step = issue.StepIndex >= 0 ? $"步骤 {issue.StepIndex + 1}" : "计划";
                    string fix = string.IsNullOrWhiteSpace(issue.FixSuggestion)
                        ? ""
                        : $" 建议：{issue.FixSuggestion}";
                    return $"- {step} / {issue.ToolName}"
                        + $" [{issue.ConstraintClass}/{issue.Enforcement}]: "
                        + $"{issue.Description}{fix}";
                });
            var warnings = validation.Issues
                .Where(issue => issue.Severity == IssueSeverity.Warning)
                .ToList();
            string warningNote = warnings.Count > 0
                ? "\n\n注意：以下为软约束警告（不影响执行，仅提示潜在问题）：\n"
                    + string.Join("\n", warnings.Select(w =>
                        $"- {w.ToolName} [{w.ConstraintClass}/"
                        + $"{w.Enforcement}]: {w.Description}"))
                : "";
            string recoveryNote = recovery?.ShouldStopThrashing == true
                ? "\n\n相同约束已连续出现，Runtime 将停止无效重试并保留未完成义务。"
                : recovery?.ShouldDiversify == true
                    ? "\n\n相同约束再次出现。不要重复同一计划；改用只读探测、"
                        + "不同可行策略，或在确有歧义时请求最小必要澄清。"
                    : "";
            return "计划校验未通过，当前计划未进入执行。以下约束要求重新规划、澄清或保护性阻断；"
                + "请生成下一批安全、可执行且能推进目标的工具调用，"
                + "不要声称任务已经完成：\n"
                + string.Join("\n", errors)
                + warningNote
                + recoveryNote;
        }

        private static void BindApprovalToDataRevision(
            AgentPlan plan,
            DataRevisionSnapshot revision)
        {
            plan.DataRevision = revision;
            plan.ProjectRevision = revision.Fingerprint;
            foreach (var ticket in plan.Steps
                         .Where(step => step.Status == PlanStepStatus.AwaitingApproval)
                         .Select(step => step.PendingApproval)
                         .Where(ticket => ticket != null)
                         .DistinctBy(ticket => ticket!.Id))
            {
                ticket!.DataRevisionFingerprint = revision.Fingerprint;
            }
        }

        private void PublishApprovalBound(
            int turn,
            AgentPlan plan,
            DataRevisionSnapshot revision)
        {
            if (_eventBus == null)
                return;
            var tickets = plan.Steps
                .Where(step =>
                    step.Status == PlanStepStatus.AwaitingApproval)
                .Select(step => step.PendingApproval)
                .Where(ticket => ticket != null)
                .DistinctBy(ticket => ticket!.Id)
                .ToList();
            foreach (var ticket in tickets)
            {
                _eventBus.Publish(new AgentEvent
                {
                    EventType = "safety.approval_bound",
                    Turn = turn,
                    Metadata = new()
                    {
                        ["ticket_id"] = ticket!.Id,
                        ["data_revision"] = revision.Fingerprint,
                        ["revision_reliable"] =
                            revision.IsReliable.ToString()
                    },
                    Data = new()
                    {
                        ["covered_steps"] = ticket.CoveredStepIds,
                        ["covered_calls"] = ticket.CoveredCalls
                    }
                });
            }
        }

        /// <summary>Move every active log stream as one lifecycle operation.</summary>
        /// <remarks>中文：将所有活跃日志流作为一个生命周期操作进行移动。</remarks>
        public void MigrateLogging(string logDir)
        {
            _transcriptLifecycle?.MigrateTo(logDir);
            _ledger?.MigrateTo(logDir);
            _snapshotter?.MigrateTo(logDir);
        }

        public void CloseLogging()
        {
            _transcriptLifecycle?.Close();
            _ledger?.Dispose();
            _snapshotter?.Dispose();
            if (_audit is IDisposable disposableAudit)
                disposableAudit.Dispose();
        }

        private async Task<ApprovalFreshnessResult> ValidateApprovalFreshnessAsync(
            AgentPlan plan,
            ApprovalTicket ticket,
            CancellationToken cancellation)
        {
            DataRevisionSnapshot current =
                await _contextManager.CaptureDataRevisionAsync(cancellation);
            DataRevisionSnapshot? approved = plan.DataRevision;

            if (approved == null
                || string.IsNullOrWhiteSpace(approved.Fingerprint)
                || string.IsNullOrWhiteSpace(ticket.DataRevisionFingerprint))
            {
                return ApprovalFreshnessResult.Stale(
                    "审批票据来自旧版 Checkpoint，缺少数据修订指纹。",
                    approved,
                    current);
            }

            if (!approved.IsReliable
                || !current.IsReliable
                || (approved.ContentRevisionRequired
                    && !approved.ContentRevisionReliable)
                || (current.ContentRevisionRequired
                    && !current.ContentRevisionReliable))
            {
                return ApprovalFreshnessResult.Stale(
                    $"无法可靠验证 ArcGIS 数据状态。批准时：{approved.ReliabilityReason}；"
                    + $"当前：{current.ReliabilityReason}",
                    approved,
                    current);
            }

            if (!ticket.DataRevisionFingerprint.Equals(
                    approved.Fingerprint,
                    StringComparison.OrdinalIgnoreCase))
            {
                return ApprovalFreshnessResult.Stale(
                    "票据绑定的数据修订与计划记录不一致。",
                    approved,
                    current);
            }

            if (!approved.Fingerprint.Equals(
                    current.Fingerprint,
                    StringComparison.OrdinalIgnoreCase))
            {
                string components = DescribeRevisionChanges(approved, current);
                return ApprovalFreshnessResult.Stale(
                    $"审批暂停后 ArcGIS 状态发生变化：{components}。",
                    approved,
                    current);
            }

            if (ticket.Kind == ApprovalKind.DataInfluence
                && ticket.Evidence.IsReliable)
            {
                if (_dataInfluenceEstimator == null)
                {
                    return ApprovalFreshnessResult.Stale(
                        "数据影响探针未初始化，无法复核原审批证据。",
                        approved,
                        current);
                }

                _dataInfluenceEstimator.InvalidateCache();
                var step = plan.Steps.FirstOrDefault(candidate =>
                    candidate.Status == PlanStepStatus.AwaitingApproval
                    && candidate.PendingApproval?.Id == ticket.Id
                    && candidate.ToolCall != null);
                if (step?.ToolCall == null)
                {
                    return ApprovalFreshnessResult.Stale(
                        "找不到审批票据对应的工具调用。",
                        approved,
                        current);
                }

                string canonical = _tools.ResolveName(step.ToolCall.Name)
                    ?? step.ToolCall.Name;
                var descriptor = _tools.GetDescriptor(canonical);
                var estimate = await _dataInfluenceEstimator.EstimateAsync(
                    step.ToolCall,
                    new DataInfluenceContext
                    {
                        ToolName = step.ToolCall.Name,
                        CanonicalToolName = canonical,
                        DisplayName = canonical,
                        SafetyProfile = descriptor is IToolSafetyDescriptor safetyDescriptor
                            ? safetyDescriptor.SafetyProfile
                            : null,
                        Governance = descriptor is IToolGovernanceDescriptor governanceDescriptor
                            ? governanceDescriptor.Governance
                            : null
                    });

                if (!estimate.IsReliable)
                {
                    return ApprovalFreshnessResult.Stale(
                        $"当前数据影响证据不可复核：{estimate.ReliabilityReason}",
                        approved,
                        current);
                }

                var changedEvidence = CompareInfluenceEvidence(
                    ticket.Evidence,
                    estimate);
                if (changedEvidence.Count > 0)
                {
                    return ApprovalFreshnessResult.Stale(
                        $"审批依据发生变化：{string.Join("、", changedEvidence)}。",
                        approved,
                        current);
                }
            }

            return ApprovalFreshnessResult.Valid(approved, current);
        }

        private AgentResponse InvalidateStaleApproval(
            AgentPlan plan,
            ApprovalTicket ticket,
            ApprovalFreshnessResult freshness)
        {
            foreach (var step in plan.Steps.Where(step =>
                         step.Status == PlanStepStatus.AwaitingApproval
                         && step.PendingApproval?.Id == ticket.Id))
            {
                step.Status = PlanStepStatus.Skipped;
                step.PendingApproval = null;
            }

            _scheduler.IsApprovalResumption = false;
            _scheduler.ApprovedTicketIds = null;
            _scheduler.ApprovedStepIds = null;
            _session.Plan = null;
            _session.SetStatus(SessionStatus.Idle);
            _eventBus?.Publish(new AgentEvent
            {
                EventType = "safety.approval_invalidated",
                Turn = _turnCount,
                Metadata = new()
                {
                    ["ticket_id"] = ticket.Id,
                    ["reason"] = freshness.Reason,
                    ["approved_revision"] = freshness.Approved?.Fingerprint ?? "",
                    ["current_revision"] = freshness.Current.Fingerprint
                },
                Data = new()
                {
                    ["approved_revision"] = freshness.Approved,
                    ["current_revision"] = freshness.Current
                }
            });

            return new AgentResponse
            {
                UserReply = "原审批已失效，任何工具都没有执行。"
                    + freshness.Reason
                    + "\n请重新提交任务；Runtime 会基于当前地图、选择集和数据状态生成新的计划与审批证据。",
                HadToolCalls = true,
                StopReason = "ApprovalInvalidated",
                CanContinue = true
            };
        }

        private static string DescribeRevisionChanges(
            DataRevisionSnapshot approved,
            DataRevisionSnapshot current)
        {
            var changed = new List<string>();
            if (approved.ContentRevisionReliable
                && current.ContentRevisionReliable
                && !approved.ContentFingerprint.Equals(
                    current.ContentFingerprint,
                    StringComparison.OrdinalIgnoreCase))
                changed.Add("feature geometry or attribute content");
            if (!approved.ProjectFingerprint.Equals(current.ProjectFingerprint, StringComparison.OrdinalIgnoreCase))
                changed.Add("项目");
            if (!approved.MapFingerprint.Equals(current.MapFingerprint, StringComparison.OrdinalIgnoreCase))
                changed.Add("活动地图/视图");
            if (!approved.LayerFingerprint.Equals(current.LayerFingerprint, StringComparison.OrdinalIgnoreCase))
                changed.Add("图层、字段或要素数");
            if (!approved.SelectionFingerprint.Equals(current.SelectionFingerprint, StringComparison.OrdinalIgnoreCase))
                changed.Add("选择集");
            return changed.Count == 0 ? "未知组件" : string.Join("、", changed);
        }

        private static List<string> CompareInfluenceEvidence(
            ApprovalEvidence approved,
            DataInfluenceEstimate current)
        {
            var changed = new List<string>();
            Compare("影响量", approved.InfluenceCount, current.EffectiveCount);
            Compare("对象范围", approved.ScopeObjectCount, current.ScopeObjectCount);
            Compare("目标字段数", approved.TargetFieldCount, current.TargetFieldCount);
            Compare("写入单元数", approved.ScopeValueCount, current.ScopeValueCount);
            Compare("已有非空值", approved.ExistingNonEmptyTargetCount, current.ExistingNonEmptyTargetCount);
            Compare("首次填充值", approved.NewValueFillCount, current.NewValueFillCount);
            Compare("结构变更数", approved.SchemaMutationCount, current.SchemaMutationCount);
            if (approved.UsesSelectionScope != current.UsesSelectionScope)
                changed.Add("选择集作用域");
            if (approved.UsesFilteredScope != current.UsesFilteredScope)
                changed.Add("过滤作用域");
            return changed;

            void Compare(string label, int? oldValue, int newValue)
            {
                if (oldValue.HasValue && oldValue.Value != newValue)
                    changed.Add($"{label} {oldValue.Value}→{newValue}");
            }
        }

        private sealed class ApprovalFreshnessResult
        {
            public bool IsCurrent { get; init; }
            public string Reason { get; init; } = "";
            public DataRevisionSnapshot? Approved { get; init; }
            public DataRevisionSnapshot Current { get; init; } = new();

            public static ApprovalFreshnessResult Valid(
                DataRevisionSnapshot approved,
                DataRevisionSnapshot current) => new()
                {
                    IsCurrent = true,
                    Approved = approved,
                    Current = current
                };

            public static ApprovalFreshnessResult Stale(
                string reason,
                DataRevisionSnapshot? approved,
                DataRevisionSnapshot current) => new()
                {
                    IsCurrent = false,
                    Reason = reason,
                    Approved = approved,
                    Current = current
                };
        }

        private static ApprovalDecision BuildLegacyApprovalDecision(AgentPlan savedPlan, bool approved)
        {
            var pending = savedPlan.Steps
                .Where(s => s.Status == PlanStepStatus.AwaitingApproval && s.PendingApproval != null)
                .Select(s => s.PendingApproval!)
                .FirstOrDefault();

            return new ApprovalDecision
            {
                TicketId = pending?.Id ?? "",
                Decision = approved ? ApprovalDecisionKind.Approve : ApprovalDecisionKind.Reject,
                Scope = pending?.Scope ?? ApprovalScope.ExactCall,
                Reason = approved ? "LegacyApprovedBool" : "LegacyRejectedBool",
                DecidedAtUtc = DateTime.UtcNow
            };
        }

        // ── 审批恢复辅助方法（ADR 0051）──
        // English：Approval Recovery Assistance Method (ADR 0051)

        private static void SyncPlanStatuses(AgentPlan plan, IReadOnlyList<StepExecutionResult> steps)
        {
            foreach (var sr in steps)
            {
                var ps = plan.Steps.FirstOrDefault(s => s.Id == sr.StepId);
                if (ps != null)
                    ps.Status = sr.Status switch
                    {
                        StepResultStatus.Completed => PlanStepStatus.Completed,
                        StepResultStatus.Failed => PlanStepStatus.Failed,
                        StepResultStatus.Blocked => PlanStepStatus.Skipped,
                        StepResultStatus.AwaitingApproval => PlanStepStatus.AwaitingApproval,
                        _ => PlanStepStatus.Skipped
                    };
                if (ps != null)
                    ps.PendingApproval = sr.Status == StepResultStatus.AwaitingApproval ? sr.ApprovalTicket : null;
            }
        }

        private IReadOnlyList<StepExecutionResult> ReconcileToolCallFeedback(
            IReadOnlyList<ToolCall> calls,
            AgentPlan plan,
            IReadOnlyList<StepExecutionResult> schedulerResults,
            int turn)
        {
            var reconciled = new List<StepExecutionResult>(calls.Count);
            var usedResults = new HashSet<StepExecutionResult>();
            var synthesized = new List<string>();

            for (int callIndex = 0; callIndex < calls.Count; callIndex++)
            {
                ToolCall call = calls[callIndex];
                PlanStep? planStep = plan.Steps.FirstOrDefault(step =>
                    step.ToolCall?.Id.Equals(
                        call.Id,
                        StringComparison.Ordinal) == true);
                StepExecutionResult? result = schedulerResults.FirstOrDefault(
                    candidate =>
                        !usedResults.Contains(candidate)
                        && string.Equals(
                            candidate.ToolCallId,
                            call.Id,
                            StringComparison.Ordinal));
                if (result == null && planStep != null)
                {
                    result = schedulerResults.FirstOrDefault(candidate =>
                        !usedResults.Contains(candidate)
                        && candidate.StepId == planStep.Id);
                }

                if (result == null)
                {
                    string reason = planStep == null
                        ? "tool is not registered or was excluded from the physical plan"
                        : "scheduler returned no outcome for this tool call";
                    result = CreateNotExecutedToolResult(
                        call,
                        callIndex,
                        reason,
                        planStep);
                    synthesized.Add(call.Id);
                }
                else
                {
                    result.ToolCallId = call.Id;
                    usedResults.Add(result);
                }

                reconciled.Add(result);
            }

            if (synthesized.Count > 0)
            {
                _eventBus?.Publish(new AgentEvent
                {
                    EventType = "runtime.tool_batch_reconciled",
                    Turn = turn,
                    Metadata = new()
                    {
                        ["expected_count"] = calls.Count.ToString(),
                        ["scheduler_count"] =
                            schedulerResults.Count.ToString(),
                        ["synthesized_count"] =
                            synthesized.Count.ToString()
                    },
                    Data = new()
                    {
                        ["synthesized_tool_call_ids"] = synthesized
                    }
                });
            }

            return reconciled.AsReadOnly();
        }

        private static StepExecutionResult CreateNotExecutedToolResult(
            ToolCall call,
            int callIndex,
            string reason,
            PlanStep? planStep = null) => new()
        {
            StepId = planStep?.Id ?? -(callIndex + 1),
            ToolCallId = call.Id,
            ToolName = call.Name,
            Status = StepResultStatus.Skipped,
            Cause = planStep == null
                ? StepOutcomeCause.ToolUnavailable
                : StepOutcomeCause.BatchStopped,
            InputParameters =
                new Dictionary<string, object?>(call.Arguments),
            ToolResult = new ToolResult
            {
                ToolName = call.Name,
                Success = false,
                Error = reason
            },
            BlockReason = reason,
            Effect = planStep?.Effect
        };

        private IReadOnlyList<IToolDescriptor> ResolvePlanDescriptors(AgentPlan plan) =>
            plan.Steps
                .Select(step => step.ToolCall?.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name =>
                {
                    string canonical = _tools.ResolveName(name!) ?? name!;
                    return _tools.GetDescriptor(canonical);
                })
                .OfType<IToolDescriptor>()
                .DistinctBy(descriptor => descriptor.Name, StringComparer.OrdinalIgnoreCase)
                .ToList()
                .AsReadOnly();

        /// <summary>
        /// Single settlement boundary for every scheduler return path. It is
        /// deliberately invoked before approval/blocked/complete branching so
        /// completed work cannot disappear from session, audit or checkpoints.
        /// </summary>
        /// <remarks>中文：每条调度器返回路径都使用统一的结算边界。该边界特意在批准/阻塞/完成分支之前调用，确保已完成工作不会从会话、审计或检查点中消失。</remarks>
        private void SettleStepResults(
            AgentPlan plan,
            IReadOnlyList<StepExecutionResult> incoming,
            List<StepExecutionResult> ledger,
            List<IObservation> observations,
            ISet<string> blockedTools,
            string userMessage)
        {
            MergeStepResults(ledger, incoming);
            SyncPlanStatuses(plan, incoming);
            var lineageUpdates = new List<DataLineageRecord>();

            foreach (var result in incoming)
            {
                if (string.IsNullOrWhiteSpace(result.ActionId))
                {
                    result.ActionId = plan.Steps.FirstOrDefault(step =>
                        step.Id == result.StepId)?.ActionId ?? "";
                }
                string settlementKey =
                    $"{plan.PlanId}:{result.StepId}:{result.Status}";
                if (!_settledStepResults.Add(settlementKey))
                    continue;

                result.TransitionOutcome = EvaluateTransition(result);
                if (result.Observation != null)
                {
                    _session.AddObservation(result.Observation);
                    observations.Add(result.Observation);
                }
                foreach (var validation in result.Validations)
                    _session.AddValidation(validation);
                result.Cause = SettlementPolicy.NormalizeCause(result);
                if (result.IsSuccessful
                    && !string.IsNullOrWhiteSpace(result.ActionId))
                    _completedActionIds.Add(result.ActionId);
                if (SettlementPolicy.ShouldBlockToolName(result))
                    blockedTools.Add(result.ToolName);

                var risk = _safety?.Classify(result.ToolName)
                    ?? ToolRiskLevel.Modify;
                if (risk >= ToolRiskLevel.Modify
                    && result.Status is StepResultStatus.Completed
                        or StepResultStatus.Failed)
                {
                    _audit?.LogDestructiveOperation(
                        result.ToolName,
                        result.InputParameters,
                        risk,
                        result.BackupPath,
                        result.UserApproved == true,
                        result.Status == StepResultStatus.Completed,
                        _session.ThreadId,
                        userMessage,
                        result.ElapsedMs);
                }

                var lineage = RegisterLineage(result);
                if (lineage != null)
                    lineageUpdates.Add(lineage);
                _artifactLedger.Apply(_session.Domain, result);
            }
            _projectLineageStore?.UpsertLineage(lineageUpdates);
        }

        private StateTransitionOutcome EvaluateTransition(
            StepExecutionResult result)
        {
            if (result.TransitionOutcome ==
                StateTransitionOutcome.AlreadySatisfied)
                return StateTransitionOutcome.AlreadySatisfied;
            if (result.Status == StepResultStatus.Failed)
                return StateTransitionOutcome.Failed;
            if (result.Status != StepResultStatus.Completed)
                return StateTransitionOutcome.Unknown;

            var effect = result.Effect;
            if (effect?.DesiredTargetState != TargetStateExpectation.Absent
                || string.IsNullOrWhiteSpace(effect.Target.Field))
                return StateTransitionOutcome.Changed;

            string owner = string.IsNullOrWhiteSpace(effect.Target.Layer)
                ? effect.Target.Dataset
                : effect.Target.Layer;
            string normalizedOwner = owner.Trim()
                .Replace('/', '\\')
                .TrimEnd('\\');
            var layer = _session.Domain.Layers.FirstOrDefault(candidate =>
                normalizedOwner.Equals(candidate.Name, StringComparison.OrdinalIgnoreCase)
                || normalizedOwner.EndsWith(
                    "\\" + candidate.Name,
                    StringComparison.OrdinalIgnoreCase));
            if (layer == null)
                return StateTransitionOutcome.Unknown;

            bool existedBefore = layer.Fields.Contains(
                effect.Target.Field,
                StringComparer.OrdinalIgnoreCase);
            return existedBefore
                ? StateTransitionOutcome.Changed
                : StateTransitionOutcome.AlreadySatisfied;
        }

        private void MergeStepResults(
            List<StepExecutionResult> ledger,
            IEnumerable<StepExecutionResult> incoming)
        {
            foreach (var result in incoming)
            {
                int replaceIndex = ledger.FindIndex(existing =>
                    existing.StepId == result.StepId
                    && existing.Status == StepResultStatus.AwaitingApproval);
                if (replaceIndex >= 0)
                    ledger[replaceIndex] = result;
                else
                    ledger.Add(result);
            }
        }

        private DataLineageRecord? RegisterLineage(StepExecutionResult result)
        {
            if (!result.IsSuccessful)
                return null;

            string output = result.Effect?.Target.OutputTarget?.Trim() ?? "";
            string parent = result.Effect?.Target.Dataset?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(parent))
                parent = result.Effect?.Target.Layer?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(output) ||
                string.IsNullOrWhiteSpace(parent) ||
                output.Equals(parent, StringComparison.OrdinalIgnoreCase))
                return null;

            _lineageParents[output] = parent;
            var inputNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                parent
            };
            foreach (var argument in result.InputParameters)
            {
                if (ParameterSemanticResolver.Classify(argument.Key)
                    != ParameterSemanticKind.InputDataset)
                    continue;
                string value = ParameterSemanticResolver.FormatValue(argument.Value);
                foreach (string item in value.Split(
                             new[] { ',', ';' },
                             StringSplitOptions.RemoveEmptyEntries
                             | StringSplitOptions.TrimEntries))
                {
                    if (!item.Equals(output, StringComparison.OrdinalIgnoreCase))
                        inputNames.Add(item);
                }
            }
            var record = new DataLineageRecord
            {
                Output = output,
                Parent = parent,
                Inputs = inputNames.Select(input => new DataLineageInput
                {
                    Resource = input,
                    Role = input.Equals(parent, StringComparison.OrdinalIgnoreCase)
                        ? DataLineageInputRole.Primary
                        : DataLineageInputRole.Reference
                }).ToList()
            };
            _lineageRecords[output] = record;
            return CloneLineage(record);
        }

        private List<DataLineageRecord> CaptureLineage() =>
            _lineageParents.Select(pair =>
            {
                if (_lineageRecords.TryGetValue(pair.Key, out var record))
                    return CloneLineage(record);
                return new DataLineageRecord
                {
                    Output = pair.Key,
                    Parent = pair.Value,
                    Inputs = new()
                    {
                        new DataLineageInput
                        {
                            Resource = pair.Value,
                            Role = DataLineageInputRole.Primary
                        }
                    }
                };
            }).ToList();

        private static DataLineageRecord? NormalizeLineage(
            DataLineageRecord? source)
        {
            if (source == null || string.IsNullOrWhiteSpace(source.Output))
                return null;
            string parent = source.Parent?.Trim() ?? "";
            var inputs = source.Inputs?
                .Where(input => !string.IsNullOrWhiteSpace(input.Resource))
                .Select(input => new DataLineageInput
                {
                    Resource = input.Resource.Trim(),
                    Role = input.Role
                })
                .ToList()
                ?? new List<DataLineageInput>();
            if (string.IsNullOrWhiteSpace(parent))
                parent = inputs.FirstOrDefault(input =>
                    input.Role == DataLineageInputRole.Primary)?.Resource ?? "";
            if (string.IsNullOrWhiteSpace(parent))
                return null;
            if (!inputs.Any(input =>
                    input.Resource.Equals(
                        parent, StringComparison.OrdinalIgnoreCase)))
            {
                inputs.Insert(0, new DataLineageInput
                {
                    Resource = parent,
                    Role = DataLineageInputRole.Primary
                });
            }
            return new DataLineageRecord
            {
                Output = source.Output.Trim(),
                Parent = parent,
                Inputs = inputs
            };
        }

        private static DataLineageRecord CloneLineage(
            DataLineageRecord source) => new()
        {
            Output = source.Output,
            Parent = source.Parent,
            Inputs = source.Inputs.Select(input => new DataLineageInput
            {
                Resource = input.Resource,
                Role = input.Role
            }).ToList()
        };

        private string ResolveLineageRoot(string name)
        {
            string current = name;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (_lineageParents.TryGetValue(current, out string? parent) &&
                   visited.Add(current) &&
                   !string.IsNullOrWhiteSpace(parent))
                current = parent;
            return current.Equals(name, StringComparison.OrdinalIgnoreCase) ? "" : current;
        }

        private int ResolveLineageDepth(string name)
        {
            int depth = 0;
            string current = name;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (_lineageParents.TryGetValue(current, out string? parent) &&
                   visited.Add(current) &&
                   !string.IsNullOrWhiteSpace(parent))
            {
                depth++;
                current = parent;
            }
            return depth;
        }

        private static StepExecutionRecord ToRecord(StepExecutionResult result) => new()
        {
            StepId = result.StepId,
            ActionId = result.ActionId,
            ToolCallId = result.ToolCallId,
            ToolName = result.ToolName,
            Status = result.Status,
            Cause = result.Cause,
            ToolResult = result.ToolResult,
            BackupPath = result.BackupPath,
            BackupPaths = result.BackupPaths.ToList(),
            Observation = result.Observation == null ? null : ToRecord(result.Observation),
            Validations = result.Validations.ToList(),
            InputParameters = new Dictionary<string, object?>(result.InputParameters),
            BlockReason = result.BlockReason,
            ApprovalReason = result.ApprovalReason,
            RequiredPermissionLevel = result.RequiredPermissionLevel,
            ApprovalKind = result.ApprovalKind,
            ApprovalTicket = result.ApprovalTicket,
            UserApproved = result.UserApproved,
            Effect = result.Effect,
            ProvidedCapabilities =
                result.ProvidedCapabilities.ToList(),
            Purpose = result.Purpose,
            TransitionOutcome = result.TransitionOutcome,
            ElapsedMs = result.ElapsedMs
        };

        private static ObservationRecord ToRecord(IObservation observation) => new()
        {
            ToolName = observation.ToolName,
            ToolCategory = observation.ToolCategory,
            ElapsedTicks = observation.Elapsed.Ticks,
            Output = observation.Output,
            Warnings = observation.Warnings.ToList(),
            Artifacts = observation.Artifacts.ToList()
        };

        private static StepExecutionResult FromRecord(StepExecutionRecord record) => new()
        {
            StepId = record.StepId,
            ActionId = record.ActionId,
            ToolCallId = record.ToolCallId,
            ToolName = record.ToolName,
            Status = record.Status,
            Cause = record.Cause,
            ToolResult = record.ToolResult,
            BackupPath = record.BackupPath,
            BackupPaths = record.BackupPaths ?? new List<string>(),
            Observation = record.Observation == null ? null : FromRecord(record.Observation),
            Validations = record.Validations ?? new List<ValidationResult>(),
            InputParameters = record.InputParameters ?? new Dictionary<string, object?>(),
            BlockReason = record.BlockReason,
            ApprovalReason = record.ApprovalReason,
            RequiredPermissionLevel = record.RequiredPermissionLevel,
            ApprovalKind = record.ApprovalKind,
            ApprovalTicket = record.ApprovalTicket,
            UserApproved = record.UserApproved,
            Effect = record.Effect,
            ProvidedCapabilities =
                record.ProvidedCapabilities ?? new List<string>(),
            Purpose = record.Purpose,
            TransitionOutcome = record.TransitionOutcome,
            ElapsedMs = record.ElapsedMs
        };

        private static Observation FromRecord(ObservationRecord record) => new()
        {
            ToolName = record.ToolName,
            ToolCategory = record.ToolCategory,
            Elapsed = TimeSpan.FromTicks(record.ElapsedTicks),
            Output = record.Output ?? new ObservationOutput(),
            Warnings = record.Warnings ?? new List<string>(),
            Artifacts = record.Artifacts ?? new List<string>()
        };

        private AgentResponse MakeApprovalResponse(StepExecutionResult approvalStep,
            IReadOnlyList<StepExecutionResult> steps)
        {
            var pendingTools = steps
                .Where(sr => sr.Status == StepResultStatus.AwaitingApproval)
                .Select(sr => sr.ToolName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            string toolText = pendingTools.Count > 0
                ? string.Join("、", pendingTools)
                : approvalStep.ToolName;
            string reason = string.IsNullOrWhiteSpace(approvalStep.ApprovalReason)
                ? "该操作会修改或删除现有数据，需要用户授权。"
                : approvalStep.ApprovalReason;

            return new AgentResponse
            {
                UserReply = $"需要授权后才能继续执行：{toolText}。\n{reason}\n请在窗口中选择\"批准并继续\"或\"拒绝\"。",
                ExecutionSummary = BuildSummary(steps),
                HadToolCalls = true,
                StopReason = "WaitingForApproval",
                RequiresApproval = true,
                ApprovalKind = approvalStep.ApprovalKind,
                PendingApproval = approvalStep.ApprovalTicket,
                PendingTools = pendingTools,
                RequiredPermissionLevel = approvalStep.RequiredPermissionLevel,
                ToolResults = CollectToolResults(steps)
            };
        }

        static string BuildSummary(IReadOnlyList<StepExecutionResult> steps)
        {
            int ok = steps.Count(s => s.Status == StepResultStatus.Completed);
            int fail = steps.Count(s => s.Status == StepResultStatus.Failed);
            int blocked = steps.Count(s => s.Status == StepResultStatus.Blocked);
            int awaiting = steps.Count(s => s.Status == StepResultStatus.AwaitingApproval);
            int warned = steps.Count(s => s.Validations.Any(v => v.Verdict == ValidationVerdict.WARN));

            var sb = new StringBuilder();
            foreach (var s in steps)
            {
                sb.AppendLine($"{StatusIcon(s.Status)} {s.ToolName}");
            }

            sb.Append($"📊 {steps.Count} 步骤 | ✅{ok} ❌{fail} 🚫{blocked}");
            if (awaiting > 0) sb.Append($" ⏸{awaiting}");
            if (warned > 0) sb.Append($" ⚠️{warned}");
            return sb.ToString();

            static string StatusIcon(StepResultStatus st) => st switch
            {
                StepResultStatus.Completed => "✅",
                StepResultStatus.Failed => "❌",
                StepResultStatus.AwaitingApproval => "⏸️",
                StepResultStatus.Blocked => "🚫",
                StepResultStatus.Skipped => "⏭️",
                _ => "⏭️"
            };
        }

        static List<ToolResult> CollectToolResults(IReadOnlyList<StepExecutionResult> steps) =>
            steps.Where(s => s.ToolResult != null).Select(s => s.ToolResult!).ToList();

        static string BuildConservativeBlockedReply(IReadOnlyList<StepExecutionResult> steps)
        {
            var blockedTools = steps
                .Where(s => s.Status == StepResultStatus.Blocked)
                .Select(s => s.ToolName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var failedTools = steps
                .Where(s => s.Status == StepResultStatus.Failed)
                .Select(s => s.ToolName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var lines = new List<string>();
            if (blockedTools.Count > 0)
                lines.Add($"本次关键工具被策略阻止（用户拒绝或签名拦截）：{string.Join("、", blockedTools)}。");
            if (failedTools.Count > 0)
                lines.Add($"另有执行失败的工具：{string.Join("、", failedTools)}。");
            lines.Add("我不能根据未执行或失败的步骤替你推算结果。");
            lines.Add("如果需要，我可以改成只读分析，或者给出手工操作路径。");
            return string.Join("\n", lines);
        }

        void EmitPlanGenerated(int turn, int round, AgentPlan plan)
        {
            if (_eventBus == null || plan.Steps.Count == 0)
                return;

            ToolRiskLevel maxRisk = ToolRiskLevel.ReadOnly;
            var steps = new List<object>();
            foreach (var step in plan.Steps)
            {
                string toolName = step.ToolCall?.Name ?? "";
                var risk = _safety?.Classify(toolName) ?? ToolRiskLevel.Modify;
                if (risk > maxRisk)
                    maxRisk = risk;

                steps.Add(new
                {
                    step = step.Id,
                    tool = toolName,
                    intent = DescribePlanIntent(step),
                    risk = risk.ToString(),
                    domain_pack = step.DomainPackId,
                    effect = step.Effect?.Profile.Id ?? "",
                    effect_version = step.Effect?.Profile.Version ?? "",
                    scope = step.Effect?.Scope.ToString() ?? "",
                    depends_on = step.DependsOn,
                    requires_success = step.RequiresSuccessfulSteps
                });
            }

            _eventBus.Publish(new AgentEvent
            {
                EventType = "plan.generated",
                Turn = turn,
                Metadata = new()
                {
                    ["round"] = round.ToString(),
                    ["step_count"] = plan.Steps.Count.ToString(),
                    ["risk_max"] = maxRisk.ToString(),
                    ["planning_mode"] = plan.Planning?.Mode.ToString() ?? PlanningMode.Direct.ToString(),
                    ["complexity"] = plan.Planning?.Complexity.ToString() ?? TaskComplexity.Simple.ToString(),
                    ["complexity_score"] = plan.Planning?.Score.ToString() ?? "0",
                    ["review_count"] = plan.Reviews.Count.ToString(),
                    ["admissibility"] =
                        plan.Admissibility?.Decision.ToString() ?? "Unknown",
                    ["outstanding_capabilities"] =
                        (plan.Admissibility?.OutstandingCapabilities.Count
                            ?? 0).ToString(),
                    ["outstanding_targets"] =
                        (plan.Admissibility?.OutstandingTargets.Count
                            ?? 0).ToString()
                },
                Data = new()
                {
                    ["steps"] = steps,
                    ["reviews"] = plan.Reviews,
                    ["routing_reasons"] = plan.Planning?.Reasons
                        ?? new List<string>()
                }
            });
        }

        static string DescribePlanIntent(PlanStep step)
        {
            string toolName = step.ToolCall?.Name ?? step.Description ?? "未命名步骤";
            string shortName = ToolNameResolver.ExtractShortName(toolName);
            if (string.IsNullOrWhiteSpace(shortName))
                return toolName;

            return shortName switch
            {
                "CalculateField" => "计算字段",
                "AddField" => "新增字段",
                "Buffer" => "创建缓冲区",
                "Clip" => "裁剪数据",
                "Erase" => "擦除范围",
                "Statistics" or "SummaryStatistics" => "汇总统计",
                "Project" => "投影转换",
                _ => shortName
            };
        }

        private static string BuildGoalContractPrompt(
            ResolvedGoalScope? scope)
        {
            if (scope == null)
                return "";
            string targets = scope.ExplicitTargets.Count == 0
                ? "(由范围量词与领域类型确定)"
                : string.Join("、", scope.ExplicitTargets);
            string references = scope.ReferenceTargets.Count == 0
                ? "(无)"
                : string.Join("、", scope.ReferenceTargets);
            string capabilities = scope.RequiredCapabilities.Count == 0
                ? "(无强制 GIS 能力)"
                : string.Join("、", scope.RequiredCapabilities);
            string unresolved = scope.UnresolvedTargets.Count == 0
                ? "(无)"
                : string.Join("、", scope.UnresolvedTargets);
            string unresolvedRequirements =
                scope.UnresolvedRequirements.Count == 0
                    ? "(无)"
                    : string.Join("、", scope.UnresolvedRequirements);
            string parameters = scope.ParameterConstraints.Count == 0
                ? "(无)"
                : string.Join(
                    "；",
                    scope.ParameterConstraints.Select(constraint =>
                        $"{constraint.AppliesToCapability}."
                        + $"{constraint.Key}={constraint.Value}"));
            string requirements = scope.Requirements.Count == 0
                ? "(无结构化子要求)"
                : string.Join(
                    "；",
                    scope.Requirements.Select(requirement =>
                        $"{requirement.Id}[{requirement.OperationFamily};"
                        + $"{requirement.DomainCategory};"
                        + $"artifact={requirement.ArtifactMention};"
                        + $"fields={string.Join(",", requirement.BoundFields)};"
                        + $"state={requirement.DesiredState};"
                        + $"grounding={requirement.GroundingStatus}]"));

            return "\n\n"
                + "[Runtime 编译目标合同（确定性边界与完成义务）]\n"
                + $"goal_id={scope.GoalId}; version={scope.Version}; "
                + $"relation={scope.TurnRelation}\n"
                + $"quantifier={scope.Quantifier}; targets={targets}; "
                + $"references={references}\n"
                + $"required_capabilities={capabilities}\n"
                + $"parameter_constraints={parameters}\n"
                + $"unresolved_resources={unresolved}\n"
                + $"requirements={requirements}\n"
                + $"unresolved_requirements={unresolvedRequirements}\n"
                + "生成下一批安全、可执行且能推进当前目标的工具调用。"
                + "允许只读探测，也允许分轮覆盖合法目标子集。\n"
                + "未覆盖的目标与能力会作为后续义务保留；"
                + "不要写入合同外资源，不要用无关操作凑完整度，"
                + "也不要在证据未闭合时声称完成。";
        }

        private void PublishGoalCompiled(
            int turn,
            ResolvedGoalScope? scope)
        {
            if (_eventBus == null || scope == null)
                return;
            _eventBus.Publish(new AgentEvent
            {
                EventType = "goal.compiled",
                Turn = turn,
                Metadata = new()
                {
                    ["goal_id"] = scope.GoalId,
                    ["version"] = scope.Version.ToString(),
                    ["requirement_count"] =
                        scope.Requirements.Count.ToString(),
                    ["target_count"] =
                        scope.ExplicitTargets.Count.ToString(),
                    ["unresolved_requirement_count"] =
                        scope.UnresolvedRequirements.Count.ToString(),
                    ["parameter_constraint_count"] =
                        scope.ParameterConstraints.Count.ToString()
                },
                Data = new()
                {
                    ["requirements"] = scope.Requirements,
                    ["targets"] = scope.ExplicitTargets,
                    ["parameter_constraints"] = scope.ParameterConstraints,
                    ["unresolved_requirements"] =
                        scope.UnresolvedRequirements
                }
            });
        }

        private void FinalizeGoalLifecycle(
            GoalCompletionReport report,
            ResolvedGoalScope? scope)
        {
            if (!report.IsComplete || scope == null)
                return;
            _lastCompletedGoalScope = scope;
            _currentGoalScope = null;
            _currentGoalCoverage = null;
        }

        private static string StopReasonFor(GoalOutcomeStatus outcome) =>
            outcome switch
            {
                GoalOutcomeStatus.Completed => "完成",
                GoalOutcomeStatus.CompletedWithWarnings => "完成（有警告）",
                GoalOutcomeStatus.Partial => "部分完成",
                GoalOutcomeStatus.ClarificationRequired => "需要澄清",
                _ => "未完成"
            };
    }
}
