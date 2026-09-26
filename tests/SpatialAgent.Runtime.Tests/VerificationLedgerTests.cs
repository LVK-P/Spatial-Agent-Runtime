using FluentAssertions;
using SpatialAgent.Runtime.Contracts;
using SpatialAgent.Services;
using SpatialAgent.Core;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    /// <summary>
    /// VerificationLedger unit tests — recording entries, statistics,
    /// health monitoring, correlation ID injection, and dispose safety.
    /// </summary>
    public class VerificationLedgerTests : IDisposable
    {
        private readonly string _logDir;
        private VerificationLedger? _ledger;

        public VerificationLedgerTests()
        {
            _logDir = Path.Combine(Path.GetTempPath(), "vl_test_" + Guid.NewGuid().ToString("N")[..8]);
        }

        public void Dispose()
        {
            _ledger?.Dispose();
            if (Directory.Exists(_logDir))
            {
                try { Directory.Delete(_logDir, true); } catch { }
            }
        }

        private VerificationLedger Init()
        {
            _ledger = new VerificationLedger();
            _ledger.Initialize(_logDir);
            return _ledger;
        }

        [Fact]
        public void Initialize_creates_file()
        {
            var v = Init();
            v.FilePath.Should().NotBeNull();
            File.Exists(v.FilePath).Should().BeTrue();
        }

        [Fact]
        public void Record_increments_statistics()
        {
            var v = Init();
            v.Record(new VerificationEntry { Verdict = "PASS", Turn = 1 });
            v.Record(new VerificationEntry { Verdict = "PASS", Turn = 1 });
            v.Record(new VerificationEntry { Verdict = "FAIL", Turn = 1 });
            v.Record(new VerificationEntry { Verdict = "WARN", Turn = 2 });

            v.TotalChecks.Should().Be(4);
            v.PassedChecks.Should().Be(2);
            v.FailedChecks.Should().Be(1);
        }

        [Fact]
        public void Record_writes_to_jsonl_file()
        {
            var v = Init();
            v.Record(new VerificationEntry
            {
                Turn = 1,
                Step = 3,
                ToolName = "management_Buffer",
                RuleType = "verify_field",
                Target = "buffer_dist",
                Expected = "> 0",
                Actual = "50",
                Verdict = "PASS",
                Detail = "Buffer distance applied correctly"
            });

            v.Dispose();
            var content = File.ReadAllText(v.FilePath!);
            content.Should().Contain("management_Buffer");
            content.Should().Contain("verify_field");
            content.Should().Contain("PASS");
            content.Should().Contain("Buffer distance applied correctly");
            content.Should().Contain("ledger_summary");
        }

        [Fact]
        public void Health_monitor_receives_write_failures()
        {
            var monitor = new LoggingHealthMonitor();
            var v = Init();
            v.SetLoggingHealthMonitor(monitor);

            v.Record(new VerificationEntry { Verdict = "PASS", Turn = 1 });

            v.WriteFailures.Should().Be(0);
            monitor.Snapshot().LedgerOk.Should().BeTrue();
        }

        [Fact]
        public void Record_before_initialize_does_not_throw()
        {
            var v = new VerificationLedger();
            var act = () => v.Record(new VerificationEntry { Verdict = "PASS", Turn = 1 });
            act.Should().NotThrow();
            v.TotalChecks.Should().Be(0);
        }

        [Fact]
        public void Correlation_ids_are_injected_when_context_active()
        {
            using (CorrelationContext.BeginRun("vl-run-001"))
            {
                using (CorrelationContext.BeginSpan("verify"))
                {
                    var v = Init();
                    // VerificationEntry doesn't expose injected fields directly,
                    // but they show up in the serialized JSON.
                    v.Record(new VerificationEntry { Verdict = "PASS", Turn = 1 });
                    v.Dispose();

                    var content = File.ReadAllText(v.FilePath!);
                    content.Should().Contain("\"run_id\":\"vl-run-001\"");
                    content.Should().Contain("verify");
                }
            }
        }

        [Fact]
        public void Dispose_writes_summary_and_releases_file()
        {
            var v = Init();
            var path = v.FilePath!;
            v.Record(new VerificationEntry { Verdict = "PASS", Turn = 1 });
            v.Dispose();

            var content = File.ReadAllText(path);
            content.Should().Contain("ledger_summary");
            content.Should().Contain("\"total_checks\":1");
            content.Should().Contain("\"write_failures\":0");
        }
    }
}
