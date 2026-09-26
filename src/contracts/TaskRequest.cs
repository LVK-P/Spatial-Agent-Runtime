namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Encapsulated user request.
    /// </summary>
    public class TaskRequest
    {
        public string UserMessage { get; set; } = "";
        public CancellationToken Cancellation { get; set; } = CancellationToken.None;
        public bool AppendUserMessage { get; set; } = true;
        public bool IsApprovalResumption { get; set; }
        public bool Approved { get; set; }
        public ApprovalDecision? ApprovalDecision { get; set; }
    }
}
