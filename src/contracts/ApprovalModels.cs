namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Why the agent paused for approval. Describes the pause reason only.
    /// Session permission is user-controlled and independent of approval kind.
    /// </summary>
    public enum ApprovalKind
    {
        Destructive,
        DataInfluence
    }

    /// <summary>
    /// Scope of a user decision. ExactCall is the secure default.
    /// </summary>
    public enum ApprovalScope
    {
        ExactCall,
        /// <summary>
        /// One decision covering an immutable set of exact call signatures.
        /// Any change to one covered call invalidates the batch decision.
        /// </summary>
        ExactBatch,
        SameCapabilityOnTarget,
        ToolSessionWide
    }

    public enum ApprovalDecisionKind
    {
        Approve,
        Reject
    }

    /// <summary>
    /// Stable identity for the GIS target this approval applies to.
    /// </summary>
    public class ApprovalTarget
    {
        public string Dataset { get; set; } = "";
        public string Layer { get; set; } = "";
        public string Field { get; set; } = "";
        public string WhereClause { get; set; } = "";
        public string OutputTarget { get; set; } = "";
        public string Signature { get; set; } = "";
    }

    /// <summary>
    /// Human/audit-facing evidence that justified the approval pause.
    /// </summary>
    public class ApprovalEvidence
    {
        public string Summary { get; set; } = "";
        public string Source { get; set; } = "";
        public bool IsReliable { get; set; }
        public string ReliabilityReason { get; set; } = "";
        public int? InfluenceCount { get; set; }
        public int? ScopeObjectCount { get; set; }
        public int? TargetFieldCount { get; set; }
        public int? ScopeValueCount { get; set; }
        public int? ExistingNonEmptyTargetCount { get; set; }
        public int? NewValueFillCount { get; set; }
        public int? SchemaMutationCount { get; set; }
        public bool UsesSelectionScope { get; set; }
        public bool UsesFilteredScope { get; set; }
        public ToolModifySubtype ModifySubtype { get; set; } = ToolModifySubtype.Unknown;
        public string BackupTarget { get; set; } = "";
        public string BackupPath { get; set; } = "";
    }

    /// <summary>
    /// A single approval request emitted by Runtime and later consumed exactly once.
    /// For batch Destructive approval, CoveredStepIds contains all step IDs covered
    /// by this ticket; the pre-scan collects all Destructive steps in one plan into
    /// a single ticket so the user approves once for all, not once per step.
    /// </summary>
    public class ApprovalTicket
    {
        public string Id { get; set; } = "";
        public int StepId { get; set; }
        public List<int> CoveredStepIds { get; set; } = new();
        public List<string> CoveredActionIds { get; set; } = new();
        public ApprovalKind Kind { get; set; }
        public ApprovalScope Scope { get; set; } = ApprovalScope.ExactCall;
        public AgentPermissionLevel? RequiredPermissionLevel { get; set; }
        public string ToolName { get; set; } = "";
        public string CanonicalToolName { get; set; } = "";
        public string CallSignature { get; set; } = "";
        public List<string> CoveredCallSignatures { get; set; } = new();
        public List<ApprovalCoveredCall> CoveredCalls { get; set; } = new();
        public string BatchSignature { get; set; } = "";
        public string DataRevisionFingerprint { get; set; } = "";
        public ApprovalTarget Target { get; set; } = new();
        public ApprovalEvidence Evidence { get; set; } = new();
        public DateTime RequestedAtUtc { get; set; } = DateTime.UtcNow;
    }

    public sealed class ApprovalCoveredCall
    {
        public int StepId { get; set; }
        public string ActionId { get; set; } = "";
        public string ToolName { get; set; } = "";
        public string CanonicalToolName { get; set; } = "";
        public string CallSignature { get; set; } = "";
        public ApprovalTarget Target { get; set; } = new();
        public string EffectProfile { get; set; } = "";
        public string EffectSummary { get; set; } = "";
        public EvidenceConfidence Confidence { get; set; }
    }

    /// <summary>
    /// User decision returned to Runtime when resuming a paused run.
    /// </summary>
    public class ApprovalDecision
    {
        public string TicketId { get; set; } = "";
        public ApprovalDecisionKind Decision { get; set; }
        public ApprovalScope Scope { get; set; } = ApprovalScope.ExactCall;
        public string Reason { get; set; } = "";
        public DateTime DecidedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
