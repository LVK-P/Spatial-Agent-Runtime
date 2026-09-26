// ================================================================
// IAgentMetrics.cs — Agent 运行指标收集接口
//
// 轻量级内存计数器，不需要外部基础设施。
// 定期 Snapshot() 写入 Transcript 日志。
// 覆盖：工具调用成功率、延迟分布、参数纠正率、Token 消耗。
//
// 来源：跨实现架构审查后的可观测性合同。
// ================================================================
namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Collects operational metrics for the agent runtime.
    /// Implement with in-memory counters (ConcurrentDictionary + Interlocked).
    /// Snapshot() periodically flushed to Transcript.
    /// </summary>
    public interface IAgentMetrics
    {
        /// <summary>Record a tool call outcome.</summary>
        void RecordToolCall(string toolName, bool success, long elapsedMs, string? errorClass);

        /// <summary>Record a validation verdict.</summary>
        void RecordValidation(string ruleName, ValidationVerdict verdict);

        /// <summary>Record a parameter auto-correction (useful for measuring LLM error rate).</summary>
        void RecordParameterCorrection(string paramName);

        /// <summary>Record batch operation outcome.</summary>
        void RecordBatchOperation(int layerCount, int successCount);

        /// <summary>Record LLM token usage for this turn.</summary>
        void RecordLlmTokenUsage(int promptTokens, int completionTokens);

        /// <summary>Take a point-in-time snapshot for logging.</summary>
        AgentMetricsSnapshot Snapshot();
    }

    /// <summary>
    /// Point-in-time metrics snapshot. Serializable for Transcript logging.
    /// </summary>
    public class AgentMetricsSnapshot
    {
        /// <summary>Total tool calls executed (all sessions).</summary>
        public long TotalToolCalls { get; set; }

        /// <summary>Tool call success rate (0.0–1.0).</summary>
        public double SuccessRate { get; set; }

        /// <summary>P95 latency in milliseconds.</summary>
        public long P95LatencyMs { get; set; }

        /// <summary>Total parameter auto-corrections applied.</summary>
        public long ParameterCorrections { get; set; }

        /// <summary>Total layers processed in batch operations.</summary>
        public long BatchLayersProcessed { get; set; }

        /// <summary>Batch operation success rate (0.0–1.0).</summary>
        public double BatchSuccessRate { get; set; }

        /// <summary>Total prompt tokens consumed.</summary>
        public long TotalPromptTokens { get; set; }

        /// <summary>Total completion tokens consumed.</summary>
        public long TotalCompletionTokens { get; set; }
    }
}
