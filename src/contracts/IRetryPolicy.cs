using System;

// ================================================================
// IRetryPolicy.cs — 工具执行重试策略接口
//
// 按错误类别（Transient/Permanent/Correctable）分别处理。
// Correctable 是 GIS 特有类别：参数被校验器自动纠正后直接重试，
// 不把错误抛回 LLM 浪费一轮对话。
//
// 来源：跨实现架构审查后的重试策略合同。
// ================================================================
namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Error classification for retry decisions.
    /// </summary>
    public enum ErrorClass
    {
        /// <summary>Temporary failure — safe to retry (timeout, lock, network).</summary>
        Transient,

        /// <summary>Unrecoverable failure — do not retry (bad params, permission denied).</summary>
        Permanent,

        /// <summary>Correctable failure — auto-fix applied, retry once with corrected args.</summary>
        Correctable
    }

    /// <summary>
    /// Classifies tool execution errors and determines retry behavior.
    /// Default implementation in Runtime layer; GIS-specific overrides in Adapters.
    /// </summary>
    public interface IRetryPolicy
    {
        /// <summary>Classify the error from a tool execution result.</summary>
        ErrorClass Classify(ToolResult result);

        /// <summary>Maximum retry attempts for this error class (0 = no retry).</summary>
        int MaxRetries(ErrorClass errorClass);

        /// <summary>Backoff delay between retry attempts.</summary>
        TimeSpan BackoffDelay(int attemptNumber);
    }
}
