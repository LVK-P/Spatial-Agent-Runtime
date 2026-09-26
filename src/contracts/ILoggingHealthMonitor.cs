using System;
using System.Collections.Generic;

namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// ★ Phase B4（2026-07-20）：统一查询四层日志健康状态。
    /// 各日志组件在写入失败时调用 ReportWriteFailure()，
    /// session_end 时通过 Snapshot() 获取聚合健康快照写入 transcript。
    /// </summary>
    public interface ILoggingHealthMonitor
    {
        /// <summary>获取当前健康快照（不重置计数器）。</summary>
        LoggingHealthSnapshot Snapshot();

        /// <summary>报告一次成功的日志写入。</summary>
        void ReportWriteSuccess(string layer);

        /// <summary>报告一次失败的日志写入。</summary>
        void ReportWriteFailure(string layer, string reason);

        /// <summary>重置所有计数器（新 session 前调用）。</summary>
        void Reset();
    }

    /// <summary>
    /// 四层日志健康快照——写入 session_end 行。
    /// </summary>
    public class LoggingHealthSnapshot
    {
        /// <summary>快照时间戳。</summary>
        public string Ts { get; set; } = DateTime.Now.ToString("O");

        // ── Transcript ──
        public bool TranscriptOk { get; set; } = true;
        public long TranscriptLostEvents { get; set; }
        public string? TranscriptFirstLossAt { get; set; }
        public string? TranscriptLastError { get; set; }

        // ── VerificationLedger ──
        public bool LedgerOk { get; set; } = true;
        public int LedgerWriteFailures { get; set; }
        public int LedgerTotalChecks { get; set; }
        public int LedgerPassedChecks { get; set; }
        public int LedgerFailedChecks { get; set; }

        // ── ContextSnapshotter ──
        public bool SnapshotOk { get; set; } = true;
        public int SnapshotWriteFailures { get; set; }
        public int SnapshotCount { get; set; }

        // ── AuditLogger ──
        public bool AuditOk { get; set; } = true;
        public int AuditWriteFailures { get; set; }
        public int AuditEntryCount { get; set; }

        /// <summary>是否有任何日志层出现故障。</summary>
        public bool AllHealthy =>
            TranscriptOk && LedgerOk && SnapshotOk && AuditOk;

        /// <summary>故障摘要（用于 UI 展示）。</summary>
        public string FaultSummary()
        {
            var faults = new List<string>();
            if (!TranscriptOk) faults.Add($"Transcript(丢失{TranscriptLostEvents}条)");
            if (!LedgerOk) faults.Add($"Ledger(失败{LedgerWriteFailures}次)");
            if (!SnapshotOk) faults.Add($"Snapshot(失败{SnapshotWriteFailures}次)");
            if (!AuditOk) faults.Add($"Audit(失败{AuditWriteFailures}次)");
            return faults.Count == 0 ? "全部健康" : string.Join(", ", faults);
        }
    }
}
