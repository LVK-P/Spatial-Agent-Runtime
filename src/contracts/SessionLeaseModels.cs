namespace SpatialAgent.Runtime.Contracts
{
    public enum SessionLeaseState
    {
        Active = 0,
        Released = 1,
        Recovered = 2,
        Recovering = 3
    }

    public sealed class SessionLease
    {
        public const int CurrentSchemaVersion = 2;

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public string LeaseId { get; set; } = Guid.NewGuid().ToString("N");
        public string SessionId { get; set; } = "";
        public string OwnerId { get; set; } = "";
        public int ProcessId { get; set; }
        public DateTime ProcessStartedAtUtc { get; set; }
        public DateTime AcquiredAtUtc { get; set; }
        public DateTime HeartbeatAtUtc { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
        public SessionLeaseState State { get; set; } = SessionLeaseState.Active;
        public DateTime? ReleasedAtUtc { get; set; }
        public string ReleaseReason { get; set; } = "";
        public DateTime? RecoveryClaimedAtUtc { get; set; }
        public DateTime? RecoveryClaimExpiresAtUtc { get; set; }
        public DateTime? RecoveredAtUtc { get; set; }
        public string RecoveryOwnerId { get; set; } = "";
    }

    public interface ISessionLeaseStore
    {
        Task<SessionLease> AcquireAsync(
            SessionLease lease,
            CancellationToken cancellation = default);

        Task<SessionLease?> HeartbeatAsync(
            string leaseId,
            string ownerId,
            DateTime heartbeatAtUtc,
            TimeSpan timeToLive,
            CancellationToken cancellation = default);

        Task<bool> ReleaseAsync(
            string leaseId,
            string ownerId,
            string reason,
            DateTime releasedAtUtc,
            CancellationToken cancellation = default);

        Task<IReadOnlyList<SessionLease>> ClaimStaleAsync(
            string recoveryOwnerId,
            DateTime observedAtUtc,
            CancellationToken cancellation = default);

        Task<bool> CompleteRecoveryAsync(
            string leaseId,
            string recoveryOwnerId,
            DateTime recoveredAtUtc,
            CancellationToken cancellation = default);
    }
}
