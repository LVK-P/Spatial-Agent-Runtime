namespace SpatialAgent.Runtime.Contracts
{
    public class AgentResponse
    {
        public string UserReply { get; set; } = "";
        public string ExecutionSummary { get; set; } = "";
        public bool HadToolCalls { get; set; }
        public string StopReason { get; set; } = "";
        public bool RequiresApproval { get; set; }
        public ApprovalKind? ApprovalKind { get; set; }
        public ApprovalTicket? PendingApproval { get; set; }
        public List<string> PendingTools { get; set; } = new();
        public AgentPermissionLevel? RequiredPermissionLevel { get; set; }
        public List<ToolResult> ToolResults { get; set; } = new();
        public bool CanContinue { get; set; }
    }
}
