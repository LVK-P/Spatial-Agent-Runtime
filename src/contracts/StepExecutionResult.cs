namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Per-step execution result — one per tool_call.
    /// Every step produces exactly one result, even if blocked or skipped.
    /// Replaces implicit Observation-only return from Scheduler.
    /// </summary>
    /// <remarks>中文：每个 tool_call 对应一个步骤执行结果。每一步都只产生一个结果，即使被阻止或跳过；它取代 Scheduler 原先隐式的仅 Observation 返回方式。</remarks>
    public enum StepResultStatus
    {
        Completed,
        Failed,
        AwaitingApproval, // paused before execution for user approval
        Blocked,  // blocked by safety gate
        Skipped   // skipped due to prior failure or user cancellation
    }

    public enum StateTransitionOutcome
    {
        Unknown,
        Changed,
        AlreadySatisfied,
        Failed
    }

    /// <summary>
    /// Structured control-flow cause for a step outcome. Settlement, recovery
    /// and UI consume this enum; free-text BlockReason is presentation only.
    /// </summary>
    /// <remarks>中文：步骤结果的结构化控制流原因。结算、恢复和 UI 使用此枚举；自由文本 BlockReason 仅用于展示。</remarks>
    public enum StepOutcomeCause
    {
        None,
        ParameterValidation,
        SafetyPolicy,
        ApprovalRequired,
        /// <summary>External host change invalidated the plan revision.</summary>
        /// <remarks>中文：宿主发生外部变更，使计划所基于的修订失效。</remarks>
        HostStateInvalidated,
        /// <summary>Host probe lag / self-write race; may be retried in-plan.</summary>
        /// <remarks>中文：宿主探针延迟或自身写入竞争，可在当前计划内重试。</remarks>
        HostSelfWriteRace,
        /// <summary>Revision probes were unreliable.</summary>
        /// <remarks>中文：修订探针不可靠。</remarks>
        HostProbeUnreliable,
        DependencyNotSatisfied,
        ToolUnavailable,
        BatchStopped,
        /// <summary>User explicitly rejected an approval ticket.</summary>
        /// <remarks>中文：用户明确拒绝了审批工单。</remarks>
        UserRejected,
        /// <summary>Approval ticket invalidated by data revision change.</summary>
        /// <remarks>中文：审批票据因数据修订变化而失效。</remarks>
        ApprovalInvalidated,
        /// <summary>Blocked by an explicit tool-level policy this turn.</summary>
        /// <remarks>中文：本回合被一个明确的工具级策略阻挡。</remarks>
        BlockedByToolPolicy,
        /// <summary>Blocked by an exact call-signature policy this turn.</summary>
        /// <remarks>中文：本轮被精确调用签名策略阻止。</remarks>
        BlockedByCallSignature,
        /// <summary>Goal scope was not sufficient to authorize mutation.</summary>
        /// <remarks>中文：目标范围不足以授权修改操作。</remarks>
        ScopeInsufficient,
        /// <summary>Required logical capability missing from the physical plan.</summary>
        /// <remarks>中文：物理计划中缺少所需的逻辑能力。</remarks>
        CapabilityMissing,
        FailedExecution,
        FailedVerification
    }

    public class StepExecutionResult
    {
        /// <summary>Step index (1-based)</summary>
        /// <remarks>中文：步骤索引（从 1 开始）。</remarks>
        public int StepId { get; set; }
        public string ActionId { get; set; } = "";

        /// <summary>
        /// Opaque provider call id. This is the stable join key between the
        /// assistant tool-call batch, the physical plan and provider feedback.
        /// </summary>
        /// <remarks>中文：不透明的提供程序调用 ID，是助手工具调用批次、物理计划和提供程序反馈之间的稳定关联键。</remarks>
        public string ToolCallId { get; set; } = "";

        /// <summary>Tool name as called</summary>
        /// <remarks>中文：实际调用的工具名称。</remarks>
        public string ToolName { get; set; } = "";

        /// <summary>Execution status</summary>
        /// <remarks>中文：执行状态</remarks>
        public StepResultStatus Status { get; set; }

        /// <summary>
        /// Structured control-flow cause. User-facing text may change without
        /// changing orchestration behavior.
        /// </summary>
        /// <remarks>中文：结构化控制流原因。面向用户的文本可能会变化，但编排行为不变。</remarks>
        public StepOutcomeCause Cause { get; set; }

        /// <summary>Raw tool execution result (null if blocked/skipped)</summary>
        /// <remarks>中文：原始工具执行结果（被阻止或跳过时为 null）。</remarks>
        public ToolResult? ToolResult { get; set; }

        /// <summary>Backup path created before execution, if any.</summary>
        /// <remarks>中文：在执行前创建备份路径（如果有的话）。</remarks>
        public string? BackupPath { get; set; }

        /// <summary>
        /// All backup artifacts created before execution. BackupPath remains the
        /// first item for compatibility with existing transcript and UI readers.
        /// </summary>
        /// <remarks>中文：执行前创建的全部备份产物。为兼容现有 transcript 和 UI 读取器，BackupPath 仍指向第一项。</remarks>
        public List<string> BackupPaths { get; set; } = new();

        /// <summary>Structured observation (null if blocked before execution)</summary>
        /// <remarks>中文：结构化观察（执行前被阻止时为 null）。</remarks>
        public IObservation? Observation { get; set; }

        /// <summary>Validation results from post-condition check</summary>
        /// <remarks>中文：事后条件检查验证结果</remarks>
        public List<ValidationResult> Validations { get; set; } = new();

        /// <summary>Input parameters to the tool call</summary>
        /// <remarks>中文：工具调用的输入参数</remarks>
        public Dictionary<string, object?> InputParameters { get; set; } = new();

        /// <summary>Block reason (when Status == Blocked)</summary>
        /// <remarks>中文：阻止原因（Status == Blocked 时）。</remarks>
        public string? BlockReason { get; set; }

        /// <summary>Reason shown when Status == AwaitingApproval.</summary>
        /// <remarks>中文：Status == AwaitingApproval 时显示的原因。</remarks>
        public string? ApprovalReason { get; set; }

        /// <summary>Suggested permission level to continue the paused operation.</summary>
        /// <remarks>中文：继续执行已暂停操作所需的建议权限级别。</remarks>
        public AgentPermissionLevel? RequiredPermissionLevel { get; set; }

        /// <summary>Why this step was paused for approval (Destructive vs DataInfluence).</summary>
        /// <remarks>中文：为什么这一步会被暂停审批（破坏性 vs DataInfluence）。</remarks>
        public ApprovalKind? ApprovalKind { get; set; }

        /// <summary>The exact approval ticket emitted for this paused step.</summary>
        /// <remarks>中文：为该暂停步骤签发的精确审批票据。</remarks>
        public ApprovalTicket? ApprovalTicket { get; set; }

        /// <summary>
        /// True only when this exact call (or immutable exact batch) was
        /// explicitly approved by the user; false when explicitly rejected;
        /// null when no approval decision applied.
        /// </summary>
        /// <remarks>中文：仅当用户明确批准该精确调用（或不可变的精确批次）时为 true；明确拒绝时为 false；没有适用的审批决定时为 null。</remarks>
        public bool? UserApproved { get; set; }

        public InvocationEffect? Effect { get; set; }

        /// <summary>
        /// Logical capabilities supplied by the compiled plan step. Completion
        /// consumes these ids rather than inferring business success from a
        /// tool name.
        /// </summary>
        /// <remarks>中文：已编译计划步骤提供的逻辑能力。完成判定使用这些 ID，而不从工具名称推断业务成功。</remarks>
        public List<string> ProvidedCapabilities { get; set; } = new();
        public PlanStepPurpose Purpose { get; set; } = PlanStepPurpose.Unspecified;

        /// <summary>
        /// Whether the invocation changed state or merely confirmed an already
        /// satisfied postcondition. Derived from effect/verification metadata.
        /// </summary>
        /// <remarks>中文：表示本次调用是改变了状态，还是仅确认已满足的后置条件；由效果/验证元数据派生。</remarks>
        public StateTransitionOutcome TransitionOutcome { get; set; }
            = StateTransitionOutcome.Unknown;

        /// <summary>Total elapsed time for this step</summary>
        /// <remarks>中文：此步骤的总经过时间</remarks>
        public long ElapsedMs { get; set; }

        /// <summary>Whether the step was successful (Completed with no FAIL validations)</summary>
        /// <remarks>中文：步骤是否成功（完成且无失败验证）</remarks>
        public bool IsSuccessful =>
            Status == StepResultStatus.Completed
            && Validations.All(v => v.Verdict != ValidationVerdict.FAIL);
    }
}
