// AuditLogger.cs — Destructive/modify operation audit trail.
// Every destructive or modify-level operation is logged with: timestamp,
// tool name, parameters, backup path, user approval status, execution
// result, and rollback info. JSONL format (one record per line) is
// ingestible by ELK/Splunk for security auditing and compliance.
// 中文：AuditLogger.cs — 破坏/修改操作审计追踪。每一次破坏性或修改级别操作都会被记录：时间戳、工具名称、参数、备份路径、用户审批状态、执行结果和回滚信息。JSONL 格式（每行一条记录）可被 ELK/Splunk 用于安全审计和合规。

using SpatialAgent.Runtime.Contracts;
using SpatialAgent.Core;
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace SpatialAgent.Services
{
    /// <summary>
    /// A single audit record for one tool execution.
    /// </summary>
    /// <remarks>中文：一个工具执行的单一审计记录。</remarks>
    public class AuditEntry
    {
        [JsonProperty("timestamp")]
        public DateTime Timestamp { get; set; } = DateTime.Now;

        [JsonProperty("session_id")]
        public string SessionId { get; set; } = "";

        [JsonProperty("tool")]
        public string Tool { get; set; } = "";

        [JsonProperty("params")]
        public string Params { get; set; } = "";  // JSON 序列化的参数字典。 / JSON-serialized argument dictionary.

        [JsonProperty("risk_level")]
        public string RiskLevel { get; set; } = "";

        [JsonProperty("backup_path")]
        public string? BackupPath { get; set; }

        [JsonProperty("user_approved")]
        public bool UserApproved { get; set; }

        [JsonProperty("result")]
        public string Result { get; set; } = "";  // “成功”|“失败”|“已取消”。 / "success" | "failed" | "cancelled".

        [JsonProperty("rollback_available")]
        public bool RollbackAvailable { get; set; }

        [JsonProperty("rollback_info")]
        public string RollbackInfo { get; set; } = "";

        [JsonProperty("user_message")]
        public string UserMessage { get; set; } = "";

        /// <summary>★ Phase A4：关联 run ID</summary>
        [JsonProperty("run_id")]
        public string? RunId { get; set; }

        /// <summary>★ Phase A4：关联 span ID</summary>
        [JsonProperty("span_id")]
        public string? SpanId { get; set; }

        /// <summary>★ Phase B3：操作耗时（毫秒）</summary>
        [JsonProperty("elapsed_ms")]
        public long ElapsedMs { get; set; }
    }

    /// <summary>
    /// Append-only JSONL audit log for all modify/destructive operations.
    /// </summary>
    /// <remarks>中文：仅附加所有修改/破坏操作的JSONL审计日志。</remarks>
    public class AuditLogger : IDisposable, IAuditLogger
    {
        private readonly string _auditFile;
        private readonly object _lock = new();
        private int _entryCount;

        // ── ★ Phase B3（2026-07-20）：健康监控 ──
        private ILoggingHealthMonitor? _healthMonitor;
        private int _writeFailures;

        public void SetLoggingHealthMonitor(ILoggingHealthMonitor? hm) => _healthMonitor = hm;
        public int WriteFailures => _writeFailures;

        public string AuditFilePath => _auditFile;
        public int EntryCount => _entryCount;

        public AuditLogger(string projectFolder)
        {
            if (string.IsNullOrEmpty(projectFolder))
            {
                _auditFile = "";
                return;
            }

            string logDir = Path.Combine(projectFolder, "agent_logs");
            Directory.CreateDirectory(logDir);
            _auditFile = Path.Combine(logDir, $"audit_{DateTime.Now:yyyyMMdd}.jsonl");
        }

        /// <summary>
        /// Append a single audit entry to the JSONL file.
        /// ★ Phase B3：写入失败上报健康监控 + 自动注入关联 ID。
        /// </summary>
        /// <remarks>中文：在 JSONL 文件中附加一个审计条目。Phase B3：写入失败上报健康监控 + 自动注入关联 ID。</remarks>
        public void Log(AuditEntry entry)
        {
            if (string.IsNullOrEmpty(_auditFile)) return;

            // ★ Phase A4：自动注入关联 ID
            string runId = CorrelationContext.RunId;
            string spanId = CorrelationContext.SpanId;
            if (!string.IsNullOrEmpty(runId)) entry.RunId = runId;
            if (!string.IsNullOrEmpty(spanId)) entry.SpanId = spanId;

            lock (_lock)
            {
                _entryCount++;
                try
                {
                    string json = JsonConvert.SerializeObject(entry) + "\n";
                    File.AppendAllText(_auditFile, json);
                }
                catch (Exception ex)
                {
                    _writeFailures++;
                    _healthMonitor?.ReportWriteFailure("audit", ex.GetType().Name);
                }
            }
        }

        /// <summary>
        /// Convenience method: log a destructive or modify-level operation
        /// with all relevant context for auditing and compliance.
        /// ★ Phase B3：新增 elapsedMs 参数，记录操作耗时。
        /// </summary>
        /// <remarks>中文：便捷方法：记录破坏性或修改级别的操作，并附上所有相关上下文，以便审计和合规。Phase B3：新增 elapsedMs 参数，记录操作耗时。</remarks>
        public void LogDestructiveOperation(
            string toolName,
            Dictionary<string, object?> parameters,
            ToolRiskLevel riskLevel,
            string? backupPath,
            bool userApproved,
            bool resultSuccess,
            string sessionId = "",
            string userMessage = "",
            long elapsedMs = 0)
        {
            Log(new AuditEntry
            {
                SessionId = sessionId,
                Tool = toolName,
                Params = JsonConvert.SerializeObject(SensitiveDataRedactor.Redact(parameters)),
                RiskLevel = riskLevel.ToString(),
                BackupPath = backupPath,
                UserApproved = userApproved,
                Result = resultSuccess ? "success" : "failed",
                RollbackAvailable = !string.IsNullOrEmpty(backupPath),
                RollbackInfo = string.IsNullOrEmpty(backupPath)
                    ? "no backup — rollback unavailable"
                    : $"restore from backup: {backupPath}",
                UserMessage = SensitiveDataRedactor.RedactText(userMessage),
                ElapsedMs = elapsedMs
            });
        }

        public void Dispose()
        {
            // JSONL writes are flushed immediately; no buffered state to drain.
            // 中文：JSONL 写入会立即被清除;没有缓冲状态需要排水。
        }
    }
}
