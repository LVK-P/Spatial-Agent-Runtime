namespace SpatialAgent.Runtime.Contracts
{
    public class AgentRunState
    {
        public const int CurrentSchemaVersion = 12;

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public string RuntimeVersion { get; set; } = "3.3";
        public string ThreadId { get; set; } = "";
        public int Turn { get; set; }
        public int Round { get; set; }
        public string UserMessage { get; set; } = "";
        public bool AppendUserMessage { get; set; } = true;
        public SessionStatus Status { get; set; } = SessionStatus.Idle;
        public AgentPermissionLevel PermissionLevel { get; set; } = AgentPermissionLevel.RequestApproval;
        public string SystemPromptHash { get; set; } = "";
        public string ContextHash { get; set; } = "";
        public string? ProjectPath { get; set; }
        public string? ProjectName { get; set; }
        public string? DefaultGdb { get; set; }
        public string? MapName { get; set; }
        public DataRevisionSnapshot? DataRevision { get; set; }
        public string? Summary { get; set; }
        public string? TerminationReason { get; set; }
        public bool IsSessionTerminal { get; set; }
        public string SessionLeaseId { get; set; } = "";
        public string SessionLeaseOwnerId { get; set; } = "";
        public AgentPlan? Plan { get; set; }
        /// <summary>
        /// Scope resolved for the turn represented by this checkpoint. This
        /// must never be populated from an older turn merely because the
        /// current turn has not yet settled.
        /// </summary>
        /// <remarks>中文：该检查点所代表轮次中解析出的范围。绝不能仅因当前轮次尚未结算，就使用旧轮次的范围填充此值。</remarks>
        public ResolvedGoalScope? GoalScope { get; set; }
        /// <summary>
        /// Most recent successfully settled business scope. Follow-up turns
        /// may inherit from this value, while diagnostics and UI render the
        /// independent current <see cref="GoalScope"/>.
        /// </summary>
        /// <remarks>中文：最近一次成功结算的业务范围。后续轮次可以继承此值，而诊断和 UI 会单独呈现当前 GoalScope。</remarks>
        public ResolvedGoalScope? PreviousGoalScope { get; set; }
        public TargetCoverageResult? GoalCoverage { get; set; }
        public PlanLedger Ledger { get; set; } = new();
        public List<StepExecutionRecord> StepResults { get; set; } = new();
        public List<ObservationRecord> Observations { get; set; } = new();
        public List<Message> Messages { get; set; } = new();
        public ApprovalTicket? PendingApproval { get; set; }
        public List<string> PendingTools { get; set; } = new();
        public List<string> BlockedTools { get; set; } = new();
        public List<string> BlockedCallSignatures { get; set; } = new();
        public List<string> ToolExposure { get; set; } = new();
        public List<DataLineageRecord> Lineage { get; set; } = new();
        public List<DomainArtifactRecord> Artifacts { get; set; } = new();
        public long ElapsedMs { get; set; }
        public DateTime SavedAt { get; set; } = DateTime.UtcNow;
    }

    public sealed class DataLineageRecord
    {
        public string Output { get; set; } = "";
        /// <summary>Legacy primary input retained for checkpoint compatibility.</summary>
        /// <remarks>中文：保留了遗留的主要输入以保证检查点兼容性。</remarks>
        public string Parent { get; set; } = "";
        public List<DataLineageInput> Inputs { get; set; } = new();
    }

    public enum DataLineageInputRole
    {
        Primary,
        Reference
    }

    public sealed class DataLineageInput
    {
        public string Resource { get; set; } = "";
        public DataLineageInputRole Role { get; set; }
    }

    public interface IRunCheckpointStore
    {
        Task SaveAsync(AgentRunState state, CancellationToken cancellation = default);
        Task<AgentRunState?> LoadMostRecentAsync(CancellationToken cancellation = default);
        Task<AgentRunState?> LoadLatestAsync(string threadId, CancellationToken cancellation = default);
        Task<AgentRunState?> LoadAsync(string threadId, int turn, CancellationToken cancellation = default);
        Task DeleteAsync(string threadId, CancellationToken cancellation = default);
    }

    /// <summary>
    /// Optional bounded writer for a host shutdown boundary. The atomic
    /// checkpoint must be durable when this call returns.
    /// </summary>
    /// <remarks>中文：宿主关闭边界使用的可选有界写入器。调用返回时，原子检查点必须已经持久化。</remarks>
    public interface ICriticalRunCheckpointStore
    {
        void SaveCritical(AgentRunState state);
    }
}
