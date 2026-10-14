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
// English：================================================================ IRetryPolicy.cs — Tool executes retry policy interfaces and handles errors separately by error category (Transient/Permanent/Correctable). Correctable is a GIS category: parameters are automatically corrected by the validator and retried directly, avoiding throwing errors back to the LLM and wasting a conversation. Source: Cross-implementation Strategy Retry Strategy Contract after Architecture Review. ================================================================
namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Error classification for retry decisions.
    /// </summary>
    /// <remarks>中文：重试决策的错误分类。</remarks>
    public enum ErrorClass
    {
        /// <summary>Temporary failure — safe to retry (timeout, lock, network).</summary>
        /// <remarks>中文：暂时失败——安全重试（超时、锁定、网络）。</remarks>
        Transient,

        /// <summary>Unrecoverable failure — do not retry (bad params, permission denied).</summary>
        /// <remarks>中文：无法恢复的失败——不要重试（参数错误，权限被拒）。</remarks>
        Permanent,

        /// <summary>Correctable failure — auto-fix applied, retry once with corrected args.</summary>
        /// <remarks>中文：可纠正失败——已应用自动修复，重新尝试一次并修正了 ARGS。</remarks>
        Correctable
    }

    /// <summary>
    /// Classifies tool execution errors and determines retry behavior.
    /// Default implementation in Runtime layer; GIS-specific overrides in Adapters.
    /// </summary>
    /// <remarks>中文：分类工具执行错误并确定重试行为。运行时层的默认实现;适配器中的 GIS 专用覆盖。</remarks>
    public interface IRetryPolicy
    {
        /// <summary>Classify the error from a tool execution result.</summary>
        /// <remarks>中文：对工具执行结果的错误进行分类。</remarks>
        ErrorClass Classify(ToolResult result);

        /// <summary>Maximum retry attempts for this error class (0 = no retry).</summary>
        /// <remarks>中文：该错误类别的最大重试次数（0 = 无重试）。</remarks>
        int MaxRetries(ErrorClass errorClass);

        /// <summary>Backoff delay between retry attempts.</summary>
        /// <remarks>中文：重试尝试之间的退避延迟。</remarks>
        TimeSpan BackoffDelay(int attemptNumber);
    }
}
