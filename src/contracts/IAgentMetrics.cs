// ================================================================
// IAgentMetrics.cs — Agent 运行指标收集接口
//
// 轻量级内存计数器，不需要外部基础设施。
// 定期 Snapshot() 写入 Transcript 日志。
// 覆盖：工具调用成功率、延迟分布、参数纠正率、Token 消耗。
//
// 来源：跨实现架构审查后的可观测性合同。
// ================================================================
// English：================================================================ IAgentMetrics.cs — Agent Operation Metrics Collection Interface Lightweight memory counter requires no external infrastructure. Periodically writes Snapshot() to the Transcript log. Coverage: tool call success rate, delay distribution, parameter correction rate, token consumption. Source: Cross-implementation Architecture Review of Observability Contracts. ================================================================
namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Collects operational metrics for the agent runtime.
    /// Implement with in-memory counters (ConcurrentDictionary + Interlocked).
    /// Snapshot() periodically flushed to Transcript.
    /// </summary>
    /// <remarks>中文：收集 Agent Runtime 的运行指标。使用内存计数器（ConcurrentDictionary + Interlocked）实现，并定期把 Snapshot() 写入 Transcript。</remarks>
    public interface IAgentMetrics
    {
        /// <summary>Record a tool call outcome.</summary>
        /// <remarks>中文：记录工具调用的结果。</remarks>
        void RecordToolCall(string toolName, bool success, long elapsedMs, string? errorClass);

        /// <summary>Record a validation verdict.</summary>
        /// <remarks>中文：记录验证结论。</remarks>
        void RecordValidation(string ruleName, ValidationVerdict verdict);

        /// <summary>Record a parameter auto-correction (useful for measuring LLM error rate).</summary>
        /// <remarks>中文：记录参数自动纠正，用于衡量 LLM 错误率。</remarks>
        void RecordParameterCorrection(string paramName);

        /// <summary>Record batch operation outcome.</summary>
        /// <remarks>中文：记录批次操作结果。</remarks>
        void RecordBatchOperation(int layerCount, int successCount);

        /// <summary>Record LLM token usage for this turn.</summary>
        /// <remarks>中文：记录本轮的 LLM Token 使用量。</remarks>
        void RecordLlmTokenUsage(int promptTokens, int completionTokens);

        /// <summary>Take a point-in-time snapshot for logging.</summary>
        /// <remarks>中文：获取用于日志记录的时点快照。</remarks>
        AgentMetricsSnapshot Snapshot();
    }

    /// <summary>
    /// Point-in-time metrics snapshot. Serializable for Transcript logging.
    /// </summary>
    /// <remarks>中文：时点指标快照，可序列化并写入 Transcript。</remarks>
    public class AgentMetricsSnapshot
    {
        /// <summary>Total tool calls executed (all sessions).</summary>
        /// <remarks>中文：执行的工具调用总数（所有会话）。</remarks>
        public long TotalToolCalls { get; set; }

        /// <summary>Tool call success rate (0.0–1.0).</summary>
        /// <remarks>中文：工具调用成功率（0.0–1.0）。</remarks>
        public double SuccessRate { get; set; }

        /// <summary>P95 latency in milliseconds.</summary>
        /// <remarks>中文：P95延迟以毫秒计。</remarks>
        public long P95LatencyMs { get; set; }

        /// <summary>Total parameter auto-corrections applied.</summary>
        /// <remarks>中文：已应用的参数自动纠正总数。</remarks>
        public long ParameterCorrections { get; set; }

        /// <summary>Total layers processed in batch operations.</summary>
        /// <remarks>中文：批量处理的总层数。</remarks>
        public long BatchLayersProcessed { get; set; }

        /// <summary>Batch operation success rate (0.0–1.0).</summary>
        /// <remarks>中文：批次操作成功率（0.0–1.0）。</remarks>
        public double BatchSuccessRate { get; set; }

        /// <summary>Total prompt tokens consumed.</summary>
        /// <remarks>中文：总共消耗的提示令牌数。</remarks>
        public long TotalPromptTokens { get; set; }

        /// <summary>Total completion tokens consumed.</summary>
        /// <remarks>中文：已消耗的 Completion Token 总数。</remarks>
        public long TotalCompletionTokens { get; set; }
    }
}
