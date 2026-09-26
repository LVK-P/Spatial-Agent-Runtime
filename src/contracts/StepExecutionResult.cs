namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Per-step execution result — one per tool_call.
    /// Every step produces exactly one result, even if blocked or skipped.
    /// Replaces implicit Observation-only return from Scheduler.
    /// </summary>
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
    public enum StepOutcomeCause
    {
        None,
        ParameterValidation,
        SafetyPolicy,
        ApprovalRequired,
        /// <summary>External host change invalidated the plan revision.</summary>
        HostStateInvalidated,
        /// <summary>Host probe lag / self-write race; may be retried in-plan.</summary>
        HostSelfWriteRace,
        /// <summary>Revision probes were unreliable.</summary>
        HostProbeUnreliable,
        DependencyNotSatisfied,
        ToolUnavailable,
        BatchStopped,
        /// <summary>User explicitly rejected an approval ticket.</summary>
        UserRejected,
        /// <summary>Approval ticket invalidated by data revision change.</summary>
        ApprovalInvalidated,
        /// <summary>Blocked by an explicit tool-level policy this turn.</summary>
        BlockedByToolPolicy,
        /// <summary>Blocked by an exact call-signature policy this turn.</summary>
        BlockedByCallSignature,
        /// <summary>Goal scope was not sufficient to authorize mutation.</summary>
        ScopeInsufficient,
        /// <summary>Required logical capability missing from the physical plan.</summary>
        CapabilityMissing,
        FailedExecution,
        FailedVerification
    }

    public class StepExecutionResult
    {
        /// <summary>Step index (1-based)</summary>
        public int StepId { get; set; }
        public string ActionId { get; set; } = "";

        /// <summary>
        /// Opaque provider call id. This is the stable join key between the
        /// assistant tool-call batch, the physical plan and provider feedback.
        /// </summary>
        public string ToolCallId { get; set; } = "";

        /// <summary>Tool name as called</summary>
        public string ToolName { get; set; } = "";

        /// <summary>Execution status</summary>
        public StepResultStatus Status { get; set; }

        /// <summary>
        /// Structured control-flow cause. User-facing text may change without
        /// changing orchestration behavior.
        /// </summary>
        public StepOutcomeCause Cause { get; set; }

        /// <summary>Raw tool execution result (null if blocked/skipped)</summary>
        public ToolResult? ToolResult { get; set; }

        /// <summary>Backup path created before execution, if any.</summary>
        public string? BackupPath { get; set; }

        /// <summary>
        /// All backup artifacts created before execution. BackupPath remains the
        /// first item for compatibility with existing transcript and UI readers.
        /// </summary>
        public List<string> BackupPaths { get; set; } = new();

        /// <summary>Structured observation (null if blocked before execution)</summary>
        public IObservation? Observation { get; set; }

        /// <summary>Validation results from post-condition check</summary>
        public List<ValidationResult> Validations { get; set; } = new();

        /// <summary>Input parameters to the tool call</summary>
        public Dictionary<string, object?> InputParameters { get; set; } = new();

        /// <summary>Block reason (when Status == Blocked)</summary>
        public string? BlockReason { get; set; }

        /// <summary>Reason shown when Status == AwaitingApproval.</summary>
        public string? ApprovalReason { get; set; }

        /// <summary>Suggested permission level to continue the paused operation.</summary>
        public AgentPermissionLevel? RequiredPermissionLevel { get; set; }

        /// <summary>Why this step was paused for approval (Destructive vs DataInfluence).</summary>
        public ApprovalKind? ApprovalKind { get; set; }

        /// <summary>The exact approval ticket emitted for this paused step.</summary>
        public ApprovalTicket? ApprovalTicket { get; set; }

        /// <summary>
        /// True only when this exact call (or immutable exact batch) was
        /// explicitly approved by the user; false when explicitly rejected;
        /// null when no approval decision applied.
        /// </summary>
        public bool? UserApproved { get; set; }

        public InvocationEffect? Effect { get; set; }

        /// <summary>
        /// Logical capabilities supplied by the compiled plan step. Completion
        /// consumes these ids rather than inferring business success from a
        /// tool name.
        /// </summary>
        public List<string> ProvidedCapabilities { get; set; } = new();
        public PlanStepPurpose Purpose { get; set; } = PlanStepPurpose.Unspecified;

        /// <summary>
        /// Whether the invocation changed state or merely confirmed an already
        /// satisfied postcondition. Derived from effect/verification metadata.
        /// </summary>
        public StateTransitionOutcome TransitionOutcome { get; set; }
            = StateTransitionOutcome.Unknown;

        /// <summary>Total elapsed time for this step</summary>
        public long ElapsedMs { get; set; }

        /// <summary>Whether the step was successful (Completed with no FAIL validations)</summary>
        public bool IsSuccessful =>
            Status == StepResultStatus.Completed
            && Validations.All(v => v.Verdict != ValidationVerdict.FAIL);
    }
}
