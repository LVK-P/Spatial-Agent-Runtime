using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Services
{
    public sealed class StaleSessionRecoveryResult
    {
        public int ClaimedLeaseCount { get; set; }
        public int TerminalizedCheckpointCount { get; set; }
        public List<string> RecoveredSessionIds { get; set; } = new();
    }

    /// <summary>
    /// Completes the durable half of a stale-lease recovery transaction. Lease
    /// claiming happens first, so repeated startup cannot replay the recovery.
    /// </summary>
    public sealed class StaleSessionRecoveryCoordinator
    {
        private readonly IRunCheckpointStore _checkpoints;
        private readonly IProjectMemoryService _memory;
        private readonly ITranscriptSessionWriter _transcript;
        private readonly ISessionLeaseStore _leases;

        public StaleSessionRecoveryCoordinator(
            IRunCheckpointStore checkpoints,
            IProjectMemoryService memory,
            ITranscriptSessionWriter transcript,
            ISessionLeaseStore leases)
        {
            _checkpoints = checkpoints;
            _memory = memory;
            _transcript = transcript;
            _leases = leases;
        }

        public async Task<StaleSessionRecoveryResult> RecoverAsync(
            IReadOnlyList<SessionLease> claimedLeases,
            DateTime recoveredAtUtc,
            CancellationToken cancellation = default)
        {
            var result = new StaleSessionRecoveryResult
            {
                ClaimedLeaseCount = claimedLeases.Count
            };
            foreach (SessionLease lease in claimedLeases)
            {
                cancellation.ThrowIfCancellationRequested();
                AgentRunState? state = await _checkpoints.LoadLatestAsync(
                    lease.SessionId,
                    cancellation);
                bool checkpointTerminalized = false;
                bool alreadyRecovered = state != null
                    && state.IsSessionTerminal
                    && state.Status == SessionStatus.Interrupted
                    && state.TerminationReason == "StaleSessionLeaseExpired"
                    && state.SessionLeaseId == lease.LeaseId;
                // An Active lease is the authority for an incomplete normal
                // finalization transaction. Even if the terminal checkpoint
                // was written before a crash, absence of lease release means
                // the remaining terminal artifacts were not committed.
                if (state != null && !alreadyRecovered)
                {
                    state.Status = SessionStatus.Interrupted;
                    state.IsSessionTerminal = true;
                    state.TerminationReason = "StaleSessionLeaseExpired";
                    state.SessionLeaseId = lease.LeaseId;
                    state.SessionLeaseOwnerId = lease.OwnerId;
                    state.Round = 97;
                    state.SavedAt = recoveredAtUtc;
                    await _checkpoints.SaveAsync(state, cancellation);
                    checkpointTerminalized = true;
                    result.TerminalizedCheckpointCount++;
                }

                _memory.RecordSessionSummary(new SessionSummary
                {
                    ThreadId = lease.SessionId,
                    Revision = state?.Turn ?? 0,
                    IsTerminal = true,
                    TerminalStatus = SessionStatus.Interrupted.ToString(),
                    TerminationReason = "StaleSessionLeaseExpired",
                    SessionLeaseId = lease.LeaseId,
                    Date = recoveredAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                    User = "ArcGISPro",
                    WhatWasDone = "上一会话未观察到正常关闭；已按过期 Session Lease 标记为 Interrupted。",
                    PendingIssues = new()
                    {
                        "宿主进程在 lease 释放前终止；请从最后一个持久 checkpoint 复盘未完成工作。"
                    }
                });
                _transcript.WriteStaleSessionRecovered(
                    lease.SessionId,
                    lease.LeaseId,
                    lease.HeartbeatAtUtc,
                    recoveredAtUtc,
                    SessionStatus.Interrupted,
                    checkpointTerminalized);
                bool completed = await _leases.CompleteRecoveryAsync(
                    lease.LeaseId,
                    lease.RecoveryOwnerId,
                    recoveredAtUtc,
                    cancellation);
                if (!completed)
                    throw new InvalidOperationException(
                        $"Session lease recovery claim was lost: {lease.LeaseId}");
                result.RecoveredSessionIds.Add(lease.SessionId);
            }
            return result;
        }
    }
}
