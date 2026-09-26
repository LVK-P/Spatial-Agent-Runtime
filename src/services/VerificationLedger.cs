using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SpatialAgent.Core;

// ────────────────────────────────────────────────────────────────
// VerificationLedger.cs — Post-condition verification audit trail.
//
// Records expected vs. actual vs. sample evidence for every verification
// check. Provides the auditable foundation for answering "how do you know
// this tool actually produced the expected result?"
//
// Output: {project}/agent_logs/verification_{timestamp}.jsonl
// ────────────────────────────────────────────────────────────────
namespace SpatialAgent.Services
{
    public class VerificationLedger : IDisposable, Runtime.Contracts.IVerificationLedger
    {
        private readonly object _lock = new();
        private string? _filePath;
        private StreamWriter? _writer;
        private bool _disposed;

        // ── ★ Phase B2（2026-07-20）：健康监控 ──
        private Runtime.Contracts.ILoggingHealthMonitor? _healthMonitor;
        private int _writeFailures;

        public void SetLoggingHealthMonitor(Runtime.Contracts.ILoggingHealthMonitor? hm) => _healthMonitor = hm;
        public int WriteFailures => _writeFailures;

        // ── Statistics ──
        public int TotalChecks { get; private set; }
        public int PassedChecks { get; private set; }
        public int FailedChecks { get; private set; }
        public string? FilePath => _filePath;

        /// <summary>
        /// Initialize ledger file in the given log directory.
        /// </summary>
        public void Initialize(string logDir)
        {
            lock (_lock)
            {
                if (_writer != null) return;
                Directory.CreateDirectory(logDir);
                _filePath = Path.Combine(logDir, $"verification_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");
                _writer = new StreamWriter(_filePath, append: true, Encoding.UTF8) { AutoFlush = true };
            }
        }

        public void MigrateTo(string logDir)
        {
            lock (_lock)
            {
                if (_writer == null || _filePath == null || _disposed)
                {
                    Initialize(logDir);
                    return;
                }

                string currentDir = Path.GetDirectoryName(_filePath) ?? "";
                string targetDir = Path.GetFullPath(logDir);
                if (Path.GetFullPath(currentDir).Equals(targetDir, StringComparison.OrdinalIgnoreCase))
                    return;

                Directory.CreateDirectory(targetDir);
                string targetPath = Path.Combine(targetDir, Path.GetFileName(_filePath));
                _writer.Flush();
                _writer.Dispose();
                File.Move(_filePath, targetPath, overwrite: true);
                _filePath = targetPath;
                _writer = new StreamWriter(_filePath, append: true, Encoding.UTF8) { AutoFlush = true };
            }
        }

        /// <summary>
        /// Record a verification check with full expected/actual/sample detail.
        /// ★ Phase B2：写入失败上报健康监控 + 自动注入 run_id/span_id。
        /// </summary>
        public void Record(VerificationEntry entry)
        {
            lock (_lock)
            {
                if (_writer == null) return;

                TotalChecks++;
                if (entry.Verdict == "PASS") PassedChecks++;
                else if (entry.Verdict == "FAIL") FailedChecks++;

                try
                {
                    // ★ Phase A4：自动注入关联 ID
                    string runId = CorrelationContext.RunId;
                    string spanId = CorrelationContext.SpanId;
                    if (!string.IsNullOrEmpty(runId)) entry.RunId = runId;
                    if (!string.IsNullOrEmpty(spanId)) entry.SpanId = spanId;

                    string json = JsonConvert.SerializeObject(entry, Formatting.None,
                        new JsonSerializerSettings
                        {
                            NullValueHandling = NullValueHandling.Ignore,
                            ReferenceLoopHandling = ReferenceLoopHandling.Ignore
                        });
                    _writer.WriteLine(json);
                }
                catch (Exception ex)
                {
                    _writeFailures++;
                    _healthMonitor?.ReportWriteFailure("ledger", ex.GetType().Name);
                }
            }
        }

        public void Flush()
        {
            lock (_lock) { try { _writer?.Flush(); } catch { } }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                try
                {
                    // Write summary footer
                    var summary = new
                    {
                        type = "ledger_summary",
                        ts = DateTime.Now.ToString("O"),
                        total_checks = TotalChecks,
                        passed = PassedChecks,
                        failed = FailedChecks,
                        write_failures = _writeFailures
                    };
                    _writer?.WriteLine(JsonConvert.SerializeObject(summary, Formatting.None));
                    _writer?.Flush();
                    _writer?.Dispose();
                }
                catch { }
            }
        }
    }

    /// <summary>
    /// Single verification ledger entry.
    /// </summary>
    public class VerificationEntry
    {
        /// <summary>Event timestamp</summary>
        [JsonProperty("ts")]
        public string Timestamp { get; set; } = DateTime.Now.ToString("HH:mm:ss.fff");

        /// <summary>Conversation turn number</summary>
        [JsonProperty("turn")]
        public int Turn { get; set; }

        /// <summary>Step within the turn</summary>
        [JsonProperty("step")]
        public int Step { get; set; }

        /// <summary>Tool being verified</summary>
        [JsonProperty("tool")]
        public string ToolName { get; set; } = "";

        /// <summary>Verification rule type (verify_field, verify_count_min, etc.)</summary>
        [JsonProperty("rule")]
        public string RuleType { get; set; } = "";

        /// <summary>Verification target (e.g., "highway1.buffer_dist")</summary>
        [JsonProperty("target")]
        public string Target { get; set; } = "";

        /// <summary>What we expected to find</summary>
        [JsonProperty("expected")]
        public string Expected { get; set; } = "";

        /// <summary>What we actually found</summary>
        [JsonProperty("actual")]
        public string Actual { get; set; } = "";

        /// <summary>Sample data from the actual state (e.g., first 3 field values)</summary>
        [JsonProperty("sample")]
        public object? Sample { get; set; }

        /// <summary>How we queried the actual state (SDK API used)</summary>
        [JsonProperty("query")]
        public string QueryMethod { get; set; } = "";

        /// <summary>PASS, FAIL, or WARN</summary>
        [JsonProperty("verdict")]
        public string Verdict { get; set; } = "PASS";

        /// <summary>Human-readable detail</summary>
        [JsonProperty("detail")]
        public string Detail { get; set; } = "";

        /// <summary>Elapsed time for this verification check</summary>
        [JsonProperty("elapsed_ms")]
        public long ElapsedMs { get; set; }

        /// <summary>★ Phase A4：关联 run ID</summary>
        [JsonProperty("run_id")]
        public string? RunId { get; set; }

        /// <summary>★ Phase A4：关联 span ID</summary>
        [JsonProperty("span_id")]
        public string? SpanId { get; set; }
    }
}
