namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Serializable observation DTO used by the durable run ledger. Runtime
    /// implementations can materialize this back into an IObservation.
    /// </summary>
    public sealed class ObservationRecord
    {
        public string ToolName { get; set; } = "";
        public string ToolCategory { get; set; } = "";
        public long ElapsedTicks { get; set; }
        public ObservationOutput Output { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public List<string> Artifacts { get; set; } = new();
    }

    /// <summary>
    /// Durable, provider-neutral evidence for one execution step.
    /// This deliberately avoids interface-typed members so checkpoint JSON can
    /// be restored without TypeNameHandling.
    /// </summary>
    public sealed class StepExecutionRecord
    {
        public int StepId { get; set; }
        public string ActionId { get; set; } = "";
        public string ToolCallId { get; set; } = "";
        public string ToolName { get; set; } = "";
        public StepResultStatus Status { get; set; }
        public StepOutcomeCause Cause { get; set; }
        public ToolResult? ToolResult { get; set; }
        public string? BackupPath { get; set; }
        public List<string> BackupPaths { get; set; } = new();
        public ObservationRecord? Observation { get; set; }
        public List<ValidationResult> Validations { get; set; } = new();
        public Dictionary<string, object?> InputParameters { get; set; } = new();
        public string? BlockReason { get; set; }
        public string? ApprovalReason { get; set; }
        public AgentPermissionLevel? RequiredPermissionLevel { get; set; }
        public ApprovalKind? ApprovalKind { get; set; }
        public ApprovalTicket? ApprovalTicket { get; set; }
        public bool? UserApproved { get; set; }
        public InvocationEffect? Effect { get; set; }
        public List<string> ProvidedCapabilities { get; set; } = new();
        public PlanStepPurpose Purpose { get; set; } = PlanStepPurpose.Unspecified;
        public StateTransitionOutcome TransitionOutcome { get; set; }
        public long ElapsedMs { get; set; }
    }

    /// <summary>
    /// Durable plan/execution ledger. Version is incremented whenever the
    /// physical plan changes; approvals bind to one immutable version.
    /// </summary>
    public sealed class PlanLedger
    {
        public string PlanId { get; set; } = Guid.NewGuid().ToString("N");
        public int Version { get; set; } = 1;
        public string Goal { get; set; } = "";
        public string ProjectRevision { get; set; } = "";
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
        public List<StepExecutionRecord> Results { get; set; } = new();
    }
}
