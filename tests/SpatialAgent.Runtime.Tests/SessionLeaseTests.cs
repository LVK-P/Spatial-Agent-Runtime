using FluentAssertions;
using Newtonsoft.Json;
using NSubstitute;
using SpatialAgent.Runtime.Contracts;
using SpatialAgent.Services;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    public sealed class SessionLeaseTests
    {
        [Fact]
        public void Normal_finalizer_writes_each_terminal_artifact_once_in_commit_order()
        {
            var finalizer = new DurableSessionFinalizer();
            var writes = new List<string>();

            bool first = finalizer.TryFinalize(
                "session-normal",
                () => writes.Add("terminal-checkpoint"),
                () => writes.Add("terminal-summary"),
                () => writes.Add("session-end"),
                () => writes.Add("lease-release"));
            bool duplicate = finalizer.TryFinalize(
                "session-normal",
                () => writes.Add("duplicate-checkpoint"),
                () => writes.Add("duplicate-summary"),
                () => writes.Add("duplicate-session-end"),
                () => writes.Add("duplicate-release"));

            first.Should().BeTrue();
            duplicate.Should().BeFalse();
            writes.Should().Equal(
                "terminal-checkpoint",
                "terminal-summary",
                "session-end",
                "lease-release");
        }

        [Fact]
        public async Task Normal_release_is_idempotent_and_is_never_claimed_as_stale()
        {
            string root = TempRoot();
            try
            {
                var store = new FileSessionLeaseStore(
                    root,
                    ownerIsAlive: _ => false);
                DateTime now = DateTime.UtcNow;
                SessionLease lease = await store.AcquireAsync(Lease(
                    "lease-normal",
                    "session-normal",
                    now,
                    now + TimeSpan.FromSeconds(30)));

                bool first = await store.ReleaseAsync(
                    lease.LeaseId,
                    lease.OwnerId,
                    "NormalClose",
                    now + TimeSpan.FromSeconds(1));
                bool second = await store.ReleaseAsync(
                    lease.LeaseId,
                    lease.OwnerId,
                    "NormalClose",
                    now + TimeSpan.FromSeconds(2));
                IReadOnlyList<SessionLease> stale = await store.ClaimStaleAsync(
                    "recovery-owner",
                    now + TimeSpan.FromHours(1));

                first.Should().BeTrue();
                second.Should().BeTrue();
                stale.Should().BeEmpty();
            }
            finally
            {
                DeleteTempRoot(root);
            }
        }

        [Fact]
        public async Task Abandoned_recovery_claim_can_be_taken_over_and_only_current_owner_completes()
        {
            string root = TempRoot();
            try
            {
                DateTime now = DateTime.UtcNow;
                var store = new FileSessionLeaseStore(
                    root,
                    ownerIsAlive: _ => false);
                SessionLease lease = await store.AcquireAsync(Lease(
                    "lease-recovery-crash",
                    "session-recovery-crash",
                    now - TimeSpan.FromMinutes(2),
                    now - TimeSpan.FromMinutes(1)));

                IReadOnlyList<SessionLease> first = await store.ClaimStaleAsync(
                    "recovery-owner-1",
                    now);
                IReadOnlyList<SessionLease> tooEarly = await store.ClaimStaleAsync(
                    "recovery-owner-2",
                    now + TimeSpan.FromMinutes(1));
                IReadOnlyList<SessionLease> takeover = await store.ClaimStaleAsync(
                    "recovery-owner-2",
                    now + TimeSpan.FromMinutes(3));
                bool oldOwnerCompleted = await store.CompleteRecoveryAsync(
                    lease.LeaseId,
                    "recovery-owner-1",
                    now + TimeSpan.FromMinutes(3));
                bool currentOwnerCompleted = await store.CompleteRecoveryAsync(
                    lease.LeaseId,
                    "recovery-owner-2",
                    now + TimeSpan.FromMinutes(3));
                IReadOnlyList<SessionLease> afterCompletion =
                    await store.ClaimStaleAsync(
                        "recovery-owner-3",
                        now + TimeSpan.FromHours(1));

                first.Should().ContainSingle(item =>
                    item.State == SessionLeaseState.Recovering
                    && item.RecoveryOwnerId == "recovery-owner-1");
                tooEarly.Should().BeEmpty();
                takeover.Should().ContainSingle(item =>
                    item.State == SessionLeaseState.Recovering
                    && item.RecoveryOwnerId == "recovery-owner-2");
                oldOwnerCompleted.Should().BeFalse();
                currentOwnerCompleted.Should().BeTrue();
                afterCompletion.Should().BeEmpty();
            }
            finally
            {
                DeleteTempRoot(root);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Stale_lease_is_claimed_once_and_terminalizes_checkpoint_without_session_end(
            bool terminalCheckpointWasWrittenBeforeCrash)
        {
            string root = TempRoot();
            try
            {
                DateTime now = DateTime.UtcNow;
                var leases = new FileSessionLeaseStore(
                    root,
                    ownerIsAlive: _ => false);
                SessionLease lease = await leases.AcquireAsync(Lease(
                    "lease-stale",
                    "session-stale",
                    now - TimeSpan.FromMinutes(2),
                    now - TimeSpan.FromMinutes(1)));
                var checkpoints = new FileRunCheckpointStore(root);
                await checkpoints.SaveAsync(new AgentRunState
                {
                    ThreadId = lease.SessionId,
                    Turn = 4,
                    Round = 2,
                    Status = terminalCheckpointWasWrittenBeforeCrash
                        ? SessionStatus.Completed
                        : SessionStatus.Running,
                    IsSessionTerminal = terminalCheckpointWasWrittenBeforeCrash,
                    TerminationReason = terminalCheckpointWasWrittenBeforeCrash
                        ? "NormalCloseBeforeLeaseRelease"
                        : "",
                    SessionLeaseId = lease.LeaseId,
                    SessionLeaseOwnerId = lease.OwnerId
                });
                IProjectMemoryService memory =
                    Substitute.For<IProjectMemoryService>();
                ITranscriptSessionWriter transcript =
                    Substitute.For<ITranscriptSessionWriter>();

                IReadOnlyList<SessionLease> claimed = await leases.ClaimStaleAsync(
                    "recovery-owner",
                    now);
                var recovery = new StaleSessionRecoveryCoordinator(
                    checkpoints,
                    memory,
                    transcript,
                    leases);
                StaleSessionRecoveryResult recovered = await recovery.RecoverAsync(
                    claimed,
                    now);
                IReadOnlyList<SessionLease> claimedAgain = await leases.ClaimStaleAsync(
                    "other-recovery-owner",
                    now + TimeSpan.FromHours(1));
                AgentRunState? terminal = await checkpoints.LoadLatestAsync(
                    lease.SessionId);

                claimed.Should().ContainSingle();
                claimedAgain.Should().BeEmpty();
                recovered.ClaimedLeaseCount.Should().Be(1);
                recovered.TerminalizedCheckpointCount.Should().Be(1);
                terminal.Should().NotBeNull();
                terminal!.Status.Should().Be(SessionStatus.Interrupted);
                terminal.IsSessionTerminal.Should().BeTrue();
                terminal.TerminationReason.Should().Be("StaleSessionLeaseExpired");
                terminal.SessionLeaseId.Should().Be(lease.LeaseId);
                memory.Received(1).RecordSessionSummary(Arg.Is<SessionSummary>(summary =>
                    summary.ThreadId == lease.SessionId
                    && summary.IsTerminal
                    && summary.TerminalStatus == SessionStatus.Interrupted.ToString()
                    && summary.SessionLeaseId == lease.LeaseId));
                transcript.Received(1).WriteStaleSessionRecovered(
                    lease.SessionId,
                    lease.LeaseId,
                    lease.HeartbeatAtUtc,
                    now,
                    SessionStatus.Interrupted,
                    true);
                transcript.DidNotReceiveWithAnyArgs().WriteSessionEnd(
                    default,
                    default,
                    default,
                    default,
                    default);
            }
            finally
            {
                DeleteTempRoot(root);
            }
        }

        [Fact]
        public async Task Schema_v11_checkpoint_loads_with_safe_v12_lease_defaults()
        {
            string root = TempRoot();
            try
            {
                string thread = "legacy-session";
                string threadRoot = Path.Combine(
                    root,
                    ".agent",
                    "checkpoints",
                    thread);
                Directory.CreateDirectory(threadRoot);
                string checkpointPath = Path.Combine(
                    threadRoot,
                    "turn_0001_round_01.json");
                await File.WriteAllTextAsync(
                    checkpointPath,
                    "{\"SchemaVersion\":11,\"ThreadId\":\"legacy-session\","
                    + "\"Turn\":1,\"Round\":1,\"Status\":0}");
                await File.WriteAllTextAsync(
                    Path.Combine(threadRoot, "latest.json"),
                    JsonConvert.SerializeObject(new
                    {
                        threadId = thread,
                        turn = 1,
                        round = 1,
                        path = checkpointPath,
                        savedAt = DateTime.UtcNow
                    }));

                AgentRunState? loaded = await new FileRunCheckpointStore(root)
                    .LoadLatestAsync(thread);

                loaded.Should().NotBeNull();
                loaded!.SchemaVersion.Should().Be(
                    AgentRunState.CurrentSchemaVersion);
                loaded.IsSessionTerminal.Should().BeFalse();
                loaded.SessionLeaseId.Should().BeEmpty();
                loaded.SessionLeaseOwnerId.Should().BeEmpty();
            }
            finally
            {
                DeleteTempRoot(root);
            }
        }

        private static SessionLease Lease(
            string leaseId,
            string sessionId,
            DateTime heartbeat,
            DateTime expires) => new()
            {
                LeaseId = leaseId,
                SessionId = sessionId,
                OwnerId = "owner",
                ProcessId = 424242,
                ProcessStartedAtUtc = heartbeat - TimeSpan.FromMinutes(1),
                AcquiredAtUtc = heartbeat,
                HeartbeatAtUtc = heartbeat,
                ExpiresAtUtc = expires
            };

        private static string TempRoot() => Path.Combine(
            Path.GetTempPath(),
            "spatial-agent-lease-tests-" + Guid.NewGuid().ToString("N"));

        private static void DeleteTempRoot(string root)
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
