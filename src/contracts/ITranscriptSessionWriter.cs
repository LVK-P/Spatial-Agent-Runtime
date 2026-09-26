namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// ★ ADR 0056 / Phase 4B：Transcript 会话边界写入接口。
    /// 从 ITranscriptWriter 拆分——记录 session 开始和结束。
    /// </summary>
    public interface ITranscriptSessionWriter
    {
        void WriteSessionStart(string sessionId, string model, string thinkingEffort, string? projectPath);
        void WriteSessionResume(
            string sessionId,
            string model,
            string thinkingEffort,
            string? projectPath) =>
            WriteSessionStart(sessionId, model, thinkingEffort, projectPath);
        void WriteHostShutdownObserved(string sessionId, string reason, SessionStatus status, bool hadActiveRun);
        void WriteHostShutdown(string sessionId, string reason, SessionStatus status, bool hadActiveRun);
        void WriteStaleSessionRecovered(
            string staleSessionId,
            string leaseId,
            DateTime lastHeartbeatAtUtc,
            DateTime recoveredAtUtc,
            SessionStatus status,
            bool checkpointTerminalized) { }
        void WriteSessionEnd(int totalTurns, int toolCalls, int toolFailures, int verificationFailures, long totalElapsedMs);

        void WriteSessionEnd(
            string sessionId,
            int totalTurns,
            int toolCalls,
            int toolFailures,
            int verificationFailures,
            long totalElapsedMs) =>
            WriteSessionEnd(
                totalTurns,
                toolCalls,
                toolFailures,
                verificationFailures,
                totalElapsedMs);
    }
}
