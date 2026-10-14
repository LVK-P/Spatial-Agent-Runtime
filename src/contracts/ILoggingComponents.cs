using System;
using System.Threading.Tasks;

namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// ★ Phase B1（2026-07-20）：VerificationLedger 的合约抽象。
    /// Runtime 层通过此接口访问 Ledger，避免直接依赖 Services 层。
    /// </summary>
    /// <remarks>English：Phase B1 (2026-07-20): Contract abstraction of VerificationLedger. The Runtime layer accesses the Ledger through this interface, avoiding direct dependence on the Services layer.</remarks>
    public interface IVerificationLedger : IDisposable
    {
        string? FilePath { get; }
        int TotalChecks { get; }
        int PassedChecks { get; }
        int FailedChecks { get; }
        int WriteFailures { get; }
        void Initialize(string logDir);
        void MigrateTo(string logDir);
        void Flush();
        void SetLoggingHealthMonitor(ILoggingHealthMonitor? hm);
    }

    /// <summary>
    /// ★ Phase B1（2026-07-20）：ContextSnapshotter 的合约抽象。
    /// Runtime 层通过此接口触发快照，避免直接依赖 Services 层。
    /// </summary>
    /// <remarks>English：Phase B1 (2026-07-20): Contract abstraction for ContextSnapshotter. The Runtime layer triggers snapshots through this interface, avoiding direct dependence on the Services layer.</remarks>
    public interface IContextSnapshotter : IDisposable
    {
        string? FilePath { get; }
        int SnapshotCount { get; }
        int WriteFailures { get; }
        void Initialize(string logDir);
        void MigrateTo(string logDir);
        void Flush();
        void SetLoggingHealthMonitor(ILoggingHealthMonitor? hm);

        /// <summary>拍摄上下文快照。fire-and-forget，失败不影响主流程。</summary>
        /// <remarks>English：Take contextual snapshots. Fire-and-forget, failure does not affect the main flow.</remarks>
        Task SnapshotAsync(string trigger, int turn, string? detail = null);
    }

    /// <summary>
    /// ★ Phase B1（2026-07-20）：日志初始化配置，
    /// 从 ViewModel 传递到 AgentRuntime.InitializeLogging()。
    /// </summary>
    public class LoggingConfig
    {
        /// <summary>日志目录（AppData fallback 或项目 agent_logs/）。</summary>
        public string LogDir { get; set; } = "";

        /// <summary>项目文件夹路径（AuditLogger 需要）。</summary>
        /// <remarks>English：Project folder path (required by AuditLogger).</remarks>
        public string? ProjectFolder { get; set; }
    }
}
