using System;

namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Transcript writer abstraction. Concrete implementation in Services layer.
    /// AgentRuntime depends on this interface, not the concrete TranscriptWriter.
    ///
    /// ★ ADR 0056 / Phase 4B：此接口已拆分为 5 角色接口，保留兼容期后移除。
    /// 新代码请使用 ITranscriptLifecycle / ITranscriptSessionWriter /
    /// ITranscriptTurnWriter / ITranscriptToolWriter / ITranscriptHealth。
    /// </summary>
    [Obsolete("Use ITranscriptLifecycle + ITranscriptSessionWriter + ITranscriptTurnWriter + ITranscriptToolWriter + ITranscriptHealth instead (ADR 0056 Phase 4B)")]
    public interface ITranscriptWriter
    {
        string? FilePath { get; }
        int EventCount { get; }
        bool IsEnabled { get; }

        /// <summary>★ Phase B1：初始化日志文件 + 启动 Channel 消费者。</summary>
        /// <remarks>English：Phase B1: Initialize log files + start Channel consumers.</remarks>
        void Initialize(string logDir);

        /// <summary>★ Phase B1：订阅 EventBus 事件（含 diagnostics.*）。</summary>
        void SubscribeTo(IEventBus eventBus);

        /// <summary>★ Phase B1：配置健康监控回调。</summary>
        /// <remarks>English：Phase B1: Configure health monitoring callbacks.</remarks>
        void SetLoggingHealthMonitor(ILoggingHealthMonitor? hm);

        void WriteSessionStart(string sessionId, string model, string thinkingEffort, string? projectPath);
        void WriteUserMessage(int turn, string content);

        /// <summary>
        /// ★ Phase A5（2026-07-20）：context 不再截断。projectInfo/mapInfo 全量写入。
        /// budgetCuts 记录被 ContextBudgeter 裁剪的部分（可选）。
        /// </summary>
        /// <remarks>English：Phase A5 (2026-07-20): context is no longer truncated. projectInfo/mapInfo is fully written. budgetCuts records the parts clipped by ContextBudgeter (optional).</remarks>
        void WriteContext(int turn, string projectInfo, string mapInfo, int ragHits, int skillsMatched,
            Dictionary<string, object>? budgetCuts = null);

        void WriteLLMRequest(int turn, object messages, object? tools, string systemPromptHash, string model = "", string thinking = "");

        /// <summary>
        /// Record a model / thinking-effort switch. model/thinking must be the
        /// provider's ACTUAL parameters, cross-checkable against subsequent
        /// llm_request events.
        /// </summary>
        /// <remarks>中文：记录一个模式/思维与努力的切换。模型/思维必须是提供者的实际参数，并且可以与后续llm_request事件进行交叉核对。</remarks>
        void WriteModelSwitch(string reason, string model, string thinking);
        void WriteLLMResponse(int turn, object toolCalls, string? textContent,
            long elapsedMs, int promptTokens = 0, int completionTokens = 0,
            string finishReason = "", int recoveryAttempt = 0);
        void WriteToolStart(int turn, int step, string toolName, Dictionary<string, object?> args);
        void WriteToolEnd(int turn, int step, string toolName, bool success, object? output, string? error, long elapsedMs, int retryCount);
        void WriteVerification(int turn, int step, string toolName, string ruleType, string target, string expected, string actual, string verdict, object? sample, string detail);
        void WriteSynthInput(int turn, object observations, bool hasFailures, List<string>? rulesInjected);
        void WriteSynthOutput(int turn, string reply, long elapsedMs);
        void WriteSessionEnd(int totalTurns, int toolCalls, int toolFailures, int verificationFailures, long totalElapsedMs);

        /// <summary>
        /// ★ Phase C2（2026-07-20）：记录每轮工具暴露决策——
        /// Tier1/Tier2/always-on/操作链预测，回答"模型当时能调什么"。
        /// </summary>
        /// <remarks>English：Phase C2 (2026-07-20): Records each round of tool exposure decisions—Tier 1/Tier 2/always-on/chain of operation predictions, answering "What can the model adjust at that time?"</remarks>
        void WriteToolExposure(int turn, int tier1Count, int tier2Count, int alwaysOnCount,
            List<string> tier1Tools, List<string>? chainPredicted, string? chainSource);
    }
}
