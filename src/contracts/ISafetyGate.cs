namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Tool risk levels — ADR 0051 four-tier model.
    /// ReadOnly: read-only queries, no data change.
    /// Increment: creates new data without overwriting existing values (Buffer, Clip, Statistics).
    /// Modify: overwrites existing field values (CalculateField, cc_calculate_area on re-run).
    /// Destructive: irreversible delete/schema-alter (DeleteField, TruncateTable).
    /// </summary>
    public enum ToolRiskLevel { ReadOnly, Increment, Modify, Destructive }

    public enum SafetyDecisionKind { Allow, RequireApproval, Block }

    public class SafetyDecision
    {
        public SafetyDecisionKind Kind { get; set; }
        public ToolRiskLevel RiskLevel { get; set; }
        public string ToolName { get; set; } = "";
        public string Reason { get; set; } = "";
        public InvocationEffect? Effect { get; set; }
    }

    public interface ISafetyGate
    {
        ToolRiskLevel Classify(string toolName);
        SafetyDecision Evaluate(string toolName, AgentPermissionLevel permissionLevel);
        bool IsDestructive(string toolName);
        bool NeedsBackup(string toolName);
    }

    public interface IAuditLogger
    {
        void LogDestructiveOperation(string tool, Dictionary<string, object?> parameters, ToolRiskLevel risk, string? backupPath, bool approved, bool success, string sessionId, string userMessage, long elapsedMs = 0);
    }
}
