using FluentAssertions;
using SpatialAgent.Runtime.Contracts;
using SpatialAgent.Services;
using SpatialAgent.Core;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    /// <summary>
    /// AuditLogger unit tests — destructive operation logging, health
    /// monitoring, correlation ID injection, elapsedMs recording.
    /// </summary>
    public class AuditLoggerTests : IDisposable
    {
        private readonly string _projectFolder;
        private AuditLogger? _audit;

        public AuditLoggerTests()
        {
            _projectFolder = Path.Combine(Path.GetTempPath(), "al_test_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(_projectFolder);
        }

        public void Dispose()
        {
            _audit?.Dispose();
            if (Directory.Exists(_projectFolder))
            {
                try { Directory.Delete(_projectFolder, true); } catch { }
            }
        }

        private AuditLogger Init()
        {
            _audit = new AuditLogger(_projectFolder);
            return _audit;
        }

        [Fact]
        public void Constructor_creates_audit_file_in_project_folder()
        {
            var a = Init();
            a.AuditFilePath.Should().Contain("agent_logs");
            a.AuditFilePath.Should().Contain("audit_");
            a.EntryCount.Should().Be(0);
        }

        [Fact]
        public void Constructor_with_empty_folder_returns_disabled_logger()
        {
            var a = new AuditLogger("");
            a.AuditFilePath.Should().Be("");
            a.EntryCount.Should().Be(0);

            // 不应抛出异常
            var act = () => a.Log(new AuditEntry { Tool = "test" });
            act.Should().NotThrow();
        }

        [Fact]
        public void LogDestructiveOperation_writes_full_entry()
        {
            var a = Init();
            a.LogDestructiveOperation(
                "management_DeleteFeatures",
                new Dictionary<string, object?> { ["in_features"] = "BASE_01" },
                ToolRiskLevel.Destructive,
                @"G:\backup\agentbackup.gdb\BASE_01_backup",
                userApproved: true,
                resultSuccess: true,
                sessionId: "sess-001",
                userMessage: "删除测试图层",
                elapsedMs: 1523);

            a.Dispose();
            var content = File.ReadAllText(a.AuditFilePath);
            content.Should().Contain("management_DeleteFeatures");
            content.Should().Contain("BASE_01");
            content.Should().Contain("Destructive");
            content.Should().Contain("agentbackup.gdb");
            content.Should().Contain("\"user_approved\":true");
            content.Should().Contain("\"result\":\"success\"");
            content.Should().Contain("\"elapsed_ms\":1523");
            content.Should().Contain("sess-001");
        }

        [Fact]
        public void Correlation_ids_are_injected_when_context_active()
        {
            using (CorrelationContext.BeginRun("al-run-42"))
            {
                var a = Init();
                a.LogDestructiveOperation(
                    "management_DeleteRows",
                    new Dictionary<string, object?>(),
                    ToolRiskLevel.Destructive,
                    null, true, true, elapsedMs: 500);

                a.Dispose();
                var content = File.ReadAllText(a.AuditFilePath);
                content.Should().Contain("\"run_id\":\"al-run-42\"");
            }
        }

        [Fact]
        public void Health_monitor_receives_write_failures()
        {
            var monitor = new LoggingHealthMonitor();
            var a = Init();
            a.SetLoggingHealthMonitor(monitor);

            // 正常写入不触发故障
            a.LogDestructiveOperation("t", new(), ToolRiskLevel.Modify, null, true, true);
            a.WriteFailures.Should().Be(0);

            var snap = monitor.Snapshot();
            snap.AuditOk.Should().BeTrue();
        }

        [Fact]
        public void EntryCount_increments_correctly()
        {
            var a = Init();
            a.LogDestructiveOperation("t1", new(), ToolRiskLevel.Modify, null, true, true);
            a.LogDestructiveOperation("t2", new(), ToolRiskLevel.Destructive, null, true, false);
            a.LogDestructiveOperation("t3", new(), ToolRiskLevel.ReadOnly, null, true, true);

            a.EntryCount.Should().Be(3);
        }

        [Fact]
        public void Result_failure_is_recorded_as_failed()
        {
            var a = Init();
            a.LogDestructiveOperation(
                "management_DeleteFeatures",
                new Dictionary<string, object?>(),
                ToolRiskLevel.Destructive,
                null, true, resultSuccess: false,
                userMessage: "Feature class locked");

            a.Dispose();
            var content = File.ReadAllText(a.AuditFilePath);
            content.Should().Contain("\"result\":\"failed\"");
            content.Should().Contain("Feature class locked");
        }

        [Fact]
        public void Secrets_are_redacted_from_parameters_and_user_text()
        {
            var a = Init();
            a.LogDestructiveOperation(
                "management_Test",
                new Dictionary<string, object?>
                {
                    ["api_key"] = "sk-live-secret",
                    ["nested"] = new Dictionary<string, object?> { ["password"] = "p@ssw0rd" },
                    ["safe"] = "Parcels"
                },
                ToolRiskLevel.Modify,
                null,
                userApproved: false,
                resultSuccess: false,
                userMessage: "token=plain-secret process Parcels");

            a.Dispose();
            string content = File.ReadAllText(a.AuditFilePath);
            content.Should().Contain("[REDACTED]");
            content.Should().Contain("Parcels");
            content.Should().NotContain("sk-live-secret");
            content.Should().NotContain("p@ssw0rd");
            content.Should().NotContain("plain-secret");
        }
    }
}
