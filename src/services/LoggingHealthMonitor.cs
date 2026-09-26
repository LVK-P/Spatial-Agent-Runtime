using System;
using System.Threading;
using SpatialAgent.Runtime.Contracts;

// ================================================================
// LoggingHealthMonitor.cs — 四层日志统一健康状态跟踪。
//
// 各日志组件通过 ReportWriteSuccess/ReportWriteFailure 上报，
// Snapshot() 产出聚合快照供 session_end 写入 transcript。
// 线程安全——所有计数器使用 Interlocked。
// ================================================================
namespace SpatialAgent.Services
{
    public class LoggingHealthMonitor : ILoggingHealthMonitor
    {
        // ── Transcript ──
        private long _transcriptLostEvents;
        private string? _transcriptFirstLossAt;
        private string? _transcriptLastError;

        // ── VerificationLedger ──
        private int _ledgerWriteFailures;

        // ── ContextSnapshotter ──
        private int _snapshotWriteFailures;

        // ── AuditLogger ──
        private int _auditWriteFailures;

        // ── 外部引用（用于读取统计信息）──
        private Func<int>? _ledgerTotalChecks;
        private Func<int>? _ledgerPassedChecks;
        private Func<int>? _ledgerFailedChecks;
        private Func<int>? _snapshotCount;
        private Func<int>? _auditEntryCount;

        public LoggingHealthMonitor(
            Func<int>? ledgerTotalChecks = null,
            Func<int>? ledgerPassedChecks = null,
            Func<int>? ledgerFailedChecks = null,
            Func<int>? snapshotCount = null,
            Func<int>? auditEntryCount = null)
        {
            _ledgerTotalChecks = ledgerTotalChecks;
            _ledgerPassedChecks = ledgerPassedChecks;
            _ledgerFailedChecks = ledgerFailedChecks;
            _snapshotCount = snapshotCount;
            _auditEntryCount = auditEntryCount;
        }

        /// <summary>★ Phase B4：延迟绑定统计读取委托（在 Initialize 之后调用）。</summary>
        public void BindStats(
            Func<int>? ledgerTotalChecks = null,
            Func<int>? ledgerPassedChecks = null,
            Func<int>? ledgerFailedChecks = null,
            Func<int>? snapshotCount = null,
            Func<int>? auditEntryCount = null)
        {
            _ledgerTotalChecks = ledgerTotalChecks ?? _ledgerTotalChecks;
            _ledgerPassedChecks = ledgerPassedChecks ?? _ledgerPassedChecks;
            _ledgerFailedChecks = ledgerFailedChecks ?? _ledgerFailedChecks;
            _snapshotCount = snapshotCount ?? _snapshotCount;
            _auditEntryCount = auditEntryCount ?? _auditEntryCount;
        }

        public void ReportWriteSuccess(string layer)
        {
            // 成功时不需要操作——只跟踪失败。
            // 保留此方法以便未来扩展（如成功率计算）。
        }

        public void ReportWriteFailure(string layer, string reason)
        {
            switch (layer.ToLowerInvariant())
            {
                case "transcript":
                    Interlocked.Increment(ref _transcriptLostEvents);
                    if (Interlocked.CompareExchange(ref _transcriptFirstLossAt,
                        DateTime.Now.ToString("O"), null) == null)
                    {
                        // 首次丢失——记录时间
                    }
                    _transcriptLastError = reason;
                    break;
                case "ledger":
                    Interlocked.Increment(ref _ledgerWriteFailures);
                    break;
                case "snapshot":
                    Interlocked.Increment(ref _snapshotWriteFailures);
                    break;
                case "audit":
                    Interlocked.Increment(ref _auditWriteFailures);
                    break;
            }
        }

        public LoggingHealthSnapshot Snapshot()
        {
            return new LoggingHealthSnapshot
            {
                Ts = DateTime.Now.ToString("O"),

                // Transcript
                TranscriptOk = Interlocked.Read(ref _transcriptLostEvents) == 0,
                TranscriptLostEvents = Interlocked.Read(ref _transcriptLostEvents),
                TranscriptFirstLossAt = _transcriptFirstLossAt,
                TranscriptLastError = _transcriptLastError,

                // VerificationLedger
                LedgerOk = _ledgerWriteFailures == 0,
                LedgerWriteFailures = _ledgerWriteFailures,
                LedgerTotalChecks = _ledgerTotalChecks?.Invoke() ?? 0,
                LedgerPassedChecks = _ledgerPassedChecks?.Invoke() ?? 0,
                LedgerFailedChecks = _ledgerFailedChecks?.Invoke() ?? 0,

                // ContextSnapshotter
                SnapshotOk = _snapshotWriteFailures == 0,
                SnapshotWriteFailures = _snapshotWriteFailures,
                SnapshotCount = _snapshotCount?.Invoke() ?? 0,

                // AuditLogger
                AuditOk = _auditWriteFailures == 0,
                AuditWriteFailures = _auditWriteFailures,
                AuditEntryCount = _auditEntryCount?.Invoke() ?? 0
            };
        }

        public void Reset()
        {
            Interlocked.Exchange(ref _transcriptLostEvents, 0);
            _transcriptFirstLossAt = null;
            _transcriptLastError = null;
            Interlocked.Exchange(ref _ledgerWriteFailures, 0);
            Interlocked.Exchange(ref _snapshotWriteFailures, 0);
            Interlocked.Exchange(ref _auditWriteFailures, 0);
        }
    }
}
