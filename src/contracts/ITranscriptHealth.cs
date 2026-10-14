namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// ★ ADR 0056 / Phase 4B：Transcript 健康监控接口。
    /// 从 ITranscriptWriter 拆分——记录事件计数、丢失检测和一致性校验。
    /// </summary>
    public interface ITranscriptHealth
    {
        int EventCount { get; }
        long LostEventCount { get; }

        void RecordLoss(string reason);
        void CheckConsistency(IEventCounters? counters = null);

        /// <summary>低层级写入（供诊断/审计直接写行）。</summary>
        /// <remarks>English：Low-level write (for diagnostic/auditing to write lines directly).</remarks>
        void Write(object entry);

        /// <summary>刷新输出缓冲区。</summary>
        /// <remarks>English：Refresh the output buffer.</remarks>
        void Flush();
    }
}
