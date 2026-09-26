using System.Diagnostics;
using System.Text;
using Newtonsoft.Json;
using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Services
{
    /// <summary>
    /// Project-scoped durable session leases. One file per lease avoids a
    /// process-wide single-session assumption; a cross-process lock protects
    /// claim/heartbeat/release transitions.
    /// </summary>
    public sealed class FileSessionLeaseStore : ISessionLeaseStore
    {
        private static readonly TimeSpan RecoveryClaimTtl =
            TimeSpan.FromMinutes(2);
        private readonly string _leaseRoot;
        private readonly string _lockPath;
        private readonly Func<SessionLease, bool> _ownerIsAlive;

        public FileSessionLeaseStore(
            string projectFolder,
            Func<SessionLease, bool>? ownerIsAlive = null)
        {
            _leaseRoot = Path.Combine(
                projectFolder,
                ".agent",
                "session_leases");
            _lockPath = Path.Combine(_leaseRoot, ".lease.lock");
            _ownerIsAlive = ownerIsAlive ?? IsOwnerProcessAlive;
        }

        public Task<SessionLease> AcquireAsync(
            SessionLease lease,
            CancellationToken cancellation = default)
        {
            ArgumentNullException.ThrowIfNull(lease);
            if (string.IsNullOrWhiteSpace(lease.LeaseId)
                || string.IsNullOrWhiteSpace(lease.SessionId)
                || string.IsNullOrWhiteSpace(lease.OwnerId))
                throw new ArgumentException("Lease identity is incomplete.");
            cancellation.ThrowIfCancellationRequested();
            return Task.FromResult(WithLock(cancellation, () =>
            {
                Directory.CreateDirectory(_leaseRoot);
                string path = LeasePath(lease.LeaseId);
                if (File.Exists(path))
                    throw new InvalidOperationException(
                        $"Session lease already exists: {lease.LeaseId}");
                lease.SchemaVersion = SessionLease.CurrentSchemaVersion;
                lease.State = SessionLeaseState.Active;
                WriteAtomic(path, lease);
                return Clone(lease);
            }));
        }

        public Task<SessionLease?> HeartbeatAsync(
            string leaseId,
            string ownerId,
            DateTime heartbeatAtUtc,
            TimeSpan timeToLive,
            CancellationToken cancellation = default)
        {
            cancellation.ThrowIfCancellationRequested();
            return Task.FromResult(WithLock(cancellation, () =>
            {
                SessionLease? lease = Read(LeasePath(leaseId));
                if (lease == null
                    || lease.State != SessionLeaseState.Active
                    || !lease.OwnerId.Equals(
                        ownerId,
                        StringComparison.Ordinal))
                    return null;
                lease.HeartbeatAtUtc = heartbeatAtUtc;
                lease.ExpiresAtUtc = heartbeatAtUtc + timeToLive;
                WriteAtomic(LeasePath(lease.LeaseId), lease);
                return Clone(lease);
            }));
        }

        public Task<bool> ReleaseAsync(
            string leaseId,
            string ownerId,
            string reason,
            DateTime releasedAtUtc,
            CancellationToken cancellation = default)
        {
            cancellation.ThrowIfCancellationRequested();
            return Task.FromResult(WithLock(cancellation, () =>
            {
                SessionLease? lease = Read(LeasePath(leaseId));
                if (lease == null
                    || !lease.OwnerId.Equals(ownerId, StringComparison.Ordinal))
                    return false;
                if (lease.State == SessionLeaseState.Released)
                    return true;
                if (lease.State != SessionLeaseState.Active)
                    return false;
                lease.State = SessionLeaseState.Released;
                lease.ReleasedAtUtc = releasedAtUtc;
                lease.ReleaseReason = reason ?? "";
                WriteAtomic(LeasePath(lease.LeaseId), lease);
                return true;
            }));
        }

        public Task<IReadOnlyList<SessionLease>> ClaimStaleAsync(
            string recoveryOwnerId,
            DateTime observedAtUtc,
            CancellationToken cancellation = default)
        {
            if (string.IsNullOrWhiteSpace(recoveryOwnerId))
                throw new ArgumentException(
                    "Recovery owner is required.",
                    nameof(recoveryOwnerId));
            cancellation.ThrowIfCancellationRequested();
            IReadOnlyList<SessionLease> claimed = WithLock(cancellation, () =>
            {
                Directory.CreateDirectory(_leaseRoot);
                var result = new List<SessionLease>();
                foreach (string path in Directory.EnumerateFiles(
                             _leaseRoot,
                             "*.json",
                             SearchOption.TopDirectoryOnly))
                {
                    SessionLease? lease = Read(path);
                    if (lease == null)
                        continue;
                    bool staleActive = lease.State == SessionLeaseState.Active
                        && (lease.ExpiresAtUtc <= observedAtUtc
                            || !_ownerIsAlive(lease));
                    bool abandonedRecovery =
                        lease.State == SessionLeaseState.Recovering
                        && lease.RecoveryClaimExpiresAtUtc.HasValue
                        && lease.RecoveryClaimExpiresAtUtc.Value <= observedAtUtc;
                    if (!staleActive && !abandonedRecovery)
                        continue;
                    lease.State = SessionLeaseState.Recovering;
                    lease.RecoveryClaimedAtUtc = observedAtUtc;
                    lease.RecoveryClaimExpiresAtUtc =
                        observedAtUtc + RecoveryClaimTtl;
                    lease.RecoveredAtUtc = null;
                    lease.RecoveryOwnerId = recoveryOwnerId;
                    WriteAtomic(path, lease);
                    result.Add(Clone(lease));
                }
                return result.AsReadOnly();
            });
            return Task.FromResult(claimed);
        }

        public Task<bool> CompleteRecoveryAsync(
            string leaseId,
            string recoveryOwnerId,
            DateTime recoveredAtUtc,
            CancellationToken cancellation = default)
        {
            cancellation.ThrowIfCancellationRequested();
            return Task.FromResult(WithLock(cancellation, () =>
            {
                SessionLease? lease = Read(LeasePath(leaseId));
                if (lease == null
                    || !lease.RecoveryOwnerId.Equals(
                        recoveryOwnerId,
                        StringComparison.Ordinal))
                    return false;
                if (lease.State == SessionLeaseState.Recovered)
                    return true;
                if (lease.State != SessionLeaseState.Recovering)
                    return false;
                lease.State = SessionLeaseState.Recovered;
                lease.RecoveredAtUtc = recoveredAtUtc;
                lease.RecoveryClaimExpiresAtUtc = null;
                WriteAtomic(LeasePath(lease.LeaseId), lease);
                return true;
            }));
        }

        private T WithLock<T>(
            CancellationToken cancellation,
            Func<T> action)
        {
            Directory.CreateDirectory(_leaseRoot);
            IOException? last = null;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    using var stream = new FileStream(
                        _lockPath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None);
                    return action();
                }
                catch (IOException ex)
                {
                    last = ex;
                    Thread.Sleep(10);
                }
            }
            throw new IOException(
                "Could not acquire the session lease transition lock.",
                last);
        }

        private string LeasePath(string leaseId) => Path.Combine(
            _leaseRoot,
            Sanitize(leaseId) + ".json");

        private static SessionLease? Read(string path)
        {
            if (!File.Exists(path))
                return null;
            try
            {
                SessionLease? lease = JsonConvert.DeserializeObject<SessionLease>(
                    File.ReadAllText(path, Encoding.UTF8));
                if (lease == null
                    || lease.SchemaVersion > SessionLease.CurrentSchemaVersion)
                    return null;
                return lease;
            }
            catch
            {
                return null;
            }
        }

        private static void WriteAtomic(string path, SessionLease lease)
        {
            string temp = path + $".{Guid.NewGuid():N}.tmp";
            string backup = path + ".bak";
            try
            {
                File.WriteAllText(
                    temp,
                    JsonConvert.SerializeObject(lease, Formatting.Indented),
                    Encoding.UTF8);
                if (File.Exists(path))
                    File.Replace(temp, path, backup, ignoreMetadataErrors: true);
                else
                    File.Move(temp, path);
            }
            finally
            {
                try
                {
                    if (File.Exists(temp))
                        File.Delete(temp);
                }
                catch { }
            }
        }

        private static bool IsOwnerProcessAlive(SessionLease lease)
        {
            try
            {
                Process process = Process.GetProcessById(lease.ProcessId);
                if (process.HasExited)
                    return false;
                DateTime actual = process.StartTime.ToUniversalTime();
                return Math.Abs(
                    (actual - lease.ProcessStartedAtUtc).TotalSeconds) < 2;
            }
            catch
            {
                return false;
            }
        }

        private static string Sanitize(string value)
        {
            var invalid = Path.GetInvalidFileNameChars().ToHashSet();
            return new string(value.Select(character =>
                invalid.Contains(character) ? '_' : character).ToArray());
        }

        private static SessionLease Clone(SessionLease lease) => new()
        {
            SchemaVersion = lease.SchemaVersion,
            LeaseId = lease.LeaseId,
            SessionId = lease.SessionId,
            OwnerId = lease.OwnerId,
            ProcessId = lease.ProcessId,
            ProcessStartedAtUtc = lease.ProcessStartedAtUtc,
            AcquiredAtUtc = lease.AcquiredAtUtc,
            HeartbeatAtUtc = lease.HeartbeatAtUtc,
            ExpiresAtUtc = lease.ExpiresAtUtc,
            State = lease.State,
            ReleasedAtUtc = lease.ReleasedAtUtc,
            ReleaseReason = lease.ReleaseReason,
            RecoveryClaimedAtUtc = lease.RecoveryClaimedAtUtc,
            RecoveryClaimExpiresAtUtc = lease.RecoveryClaimExpiresAtUtc,
            RecoveredAtUtc = lease.RecoveredAtUtc,
            RecoveryOwnerId = lease.RecoveryOwnerId
        };
    }
}
