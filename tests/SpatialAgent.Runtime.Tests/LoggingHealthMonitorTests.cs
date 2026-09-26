using FluentAssertions;
using SpatialAgent.Runtime.Contracts;
using SpatialAgent.Services;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    /// <summary>
    /// LoggingHealthMonitor unit tests — failure counting, snapshot
    /// correctness, reset behavior, and late binding of stats.
    /// </summary>
    public class LoggingHealthMonitorTests
    {
        [Fact]
        public void New_monitor_reports_all_healthy()
        {
            var monitor = new LoggingHealthMonitor();
            var snap = monitor.Snapshot();

            snap.AllHealthy.Should().BeTrue();
            snap.TranscriptOk.Should().BeTrue();
            snap.LedgerOk.Should().BeTrue();
            snap.SnapshotOk.Should().BeTrue();
            snap.AuditOk.Should().BeTrue();
            snap.FaultSummary().Should().Be("全部健康");
        }

        [Fact]
        public void ReportWriteFailure_sets_layer_unhealthy()
        {
            var monitor = new LoggingHealthMonitor();
            monitor.ReportWriteFailure("ledger", "DiskFull");

            var snap = monitor.Snapshot();
            snap.AllHealthy.Should().BeFalse();
            snap.LedgerOk.Should().BeFalse();
            snap.LedgerWriteFailures.Should().Be(1);
            snap.FaultSummary().Should().Contain("Ledger(失败1次)");
        }

        [Fact]
        public void Multiple_failures_accumulate()
        {
            var monitor = new LoggingHealthMonitor();
            monitor.ReportWriteFailure("transcript", "ChannelFull");
            monitor.ReportWriteFailure("transcript", "IOException");
            monitor.ReportWriteFailure("audit", "FileLocked");

            var snap = monitor.Snapshot();
            snap.TranscriptLostEvents.Should().Be(2);
            snap.AuditWriteFailures.Should().Be(1);
            snap.LedgerWriteFailures.Should().Be(0);
            snap.AllHealthy.Should().BeFalse();
        }

        [Fact]
        public void Reset_clears_all_counters()
        {
            var monitor = new LoggingHealthMonitor();
            monitor.ReportWriteFailure("ledger", "test");
            monitor.ReportWriteFailure("snapshot", "test");
            monitor.ReportWriteFailure("audit", "test");

            monitor.Reset();
            var snap = monitor.Snapshot();

            snap.AllHealthy.Should().BeTrue();
            snap.LedgerWriteFailures.Should().Be(0);
            snap.SnapshotWriteFailures.Should().Be(0);
            snap.AuditWriteFailures.Should().Be(0);
        }

        [Fact]
        public void BindStats_connects_external_counters()
        {
            int checks = 42, passed = 40, failed = 2;
            int snapshots = 5, audits = 3;

            var monitor = new LoggingHealthMonitor();
            monitor.BindStats(
                ledgerTotalChecks: () => checks,
                ledgerPassedChecks: () => passed,
                ledgerFailedChecks: () => failed,
                snapshotCount: () => snapshots,
                auditEntryCount: () => audits);

            var snap = monitor.Snapshot();
            snap.LedgerTotalChecks.Should().Be(42);
            snap.LedgerPassedChecks.Should().Be(40);
            snap.LedgerFailedChecks.Should().Be(2);
            snap.SnapshotCount.Should().Be(5);
            snap.AuditEntryCount.Should().Be(3);
        }

        [Fact]
        public void ReportWriteSuccess_does_not_change_counters()
        {
            var monitor = new LoggingHealthMonitor();
            monitor.ReportWriteSuccess("ledger");
            monitor.ReportWriteSuccess("transcript");

            var snap = monitor.Snapshot();
            snap.AllHealthy.Should().BeTrue();
        }

        [Fact]
        public void FaultSummary_lists_all_faulty_layers()
        {
            var monitor = new LoggingHealthMonitor();
            monitor.ReportWriteFailure("ledger", "e1");
            monitor.ReportWriteFailure("snapshot", "e2");

            var summary = monitor.Snapshot().FaultSummary();
            summary.Should().Contain("Ledger(失败1次)");
            summary.Should().Contain("Snapshot(失败1次)");
            summary.Should().NotContain("Transcript");
            summary.Should().NotContain("Audit");
        }
    }
}
