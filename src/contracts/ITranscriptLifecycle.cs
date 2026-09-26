namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// ★ ADR 0056 / Phase 4B：Transcript 生命周期管理接口。
    /// 从 ITranscriptWriter 拆分——控制 transcript 的初始化、迁移、订阅和状态。
    /// </summary>
    public interface ITranscriptLifecycle
    {
        string? FilePath { get; }
        bool IsEnabled { get; }

        void Initialize(string logDir);
        void MigrateTo(string targetDir);
        void Close();
        void SubscribeTo(IEventBus eventBus);
        void SetLoggingHealthMonitor(ILoggingHealthMonitor? hm);
    }
}
