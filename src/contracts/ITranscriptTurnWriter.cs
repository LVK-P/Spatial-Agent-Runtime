using System.Collections.Generic;

namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// ★ ADR 0056 / Phase 4B：Transcript 回合级事件写入接口。
    /// 从 ITranscriptWriter 拆分——记录每个 ReAct 回合的输入、输出和诊断。
    /// </summary>
    public interface ITranscriptTurnWriter
    {
        void WriteUserMessage(int turn, string content);
        void WriteContext(int turn, string projectInfo, string mapInfo, int ragHits, int skillsMatched,
            Dictionary<string, object>? budgetCuts = null);
        void WriteLLMRequest(int turn, object messages, object? tools, string systemPromptHash, string model = "", string thinking = "");
        void WriteModelSwitch(string reason, string model, string thinking);
        void WriteLLMResponse(int turn, object toolCalls, string? textContent,
            long elapsedMs, int promptTokens = 0, int completionTokens = 0,
            string finishReason = "", int recoveryAttempt = 0);
        void WriteToolExposure(int turn, int tier1Count, int tier2Count, int alwaysOnCount,
            List<string> tier1Tools, List<string>? chainPredicted, string? chainSource);
    }
}
