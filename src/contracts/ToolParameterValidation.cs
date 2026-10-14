using System.Collections.Generic;

// ================================================================
// ToolParameterValidation.cs — 工具参数校验结果 DTO
//
// ParameterValidator 的输出。定义三种校验动作：
// Proceed（放行）、Corrected（自动纠正后放行）、Rejected（拒绝）。
//
// 来源：跨实现架构审查后的参数验证合同。
// ================================================================
namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Validation action — what to do with the tool call after validation.
    /// </summary>
    /// <remarks>中文：验证操作——验证后工具调用该怎么办。</remarks>
    public enum ValidationAction
    {
        /// <summary>Parameters are correct — proceed to execution.</summary>
        /// <remarks>中文：参数正确——继续执行。</remarks>
        Proceed,

        /// <summary>Minor issues auto-corrected — proceed with corrected args.</summary>
        /// <remarks>中文：小问题自动更正——请继续修正ARG。</remarks>
        Corrected,

        /// <summary>Unrecoverable errors — return Observation to LLM for fix, do not execute.</summary>
        /// <remarks>中文：不可恢复错误——将观察返回LLM进行修复，不执行。</remarks>
        Rejected
    }

    /// <summary>
    /// Result of pre-execution parameter validation.
    /// Returned by ParameterValidator.Validate() before Scheduler executes a tool.
    /// </summary>
    /// <remarks>中文：预执行参数验证的结果。由 ParameterValidator.Validate（） 返回，在 Scheduler 执行工具之前。</remarks>
    public class ParameterValidationResult
    {
        /// <summary>Overall action for this tool call.</summary>
        /// <remarks>中文：本次工具发布会的整体行动。</remarks>
        public ValidationAction Action { get; set; } = ValidationAction.Proceed;

        /// <summary>
        /// Corrected arguments (may differ from original if auto-correct applied).
        /// Always populated — equals input args if no corrections.
        /// </summary>
        /// <remarks>中文：修正后的论点（如果应用自动更正，可能与原始不同）。始终填充——如果没有修正，输入的 arg。</remarks>
        public Dictionary<string, object?> CorrectedArgs { get; set; } = new();

        /// <summary>
        /// Corrections applied during validation.
        /// Logged to EventBus for audit + injected into Observation for LLM feedback.
        /// E.g. "selection_type: 'new' → 'NEW_SELECTION' (大小写纠正)"
        /// </summary>
        public List<string> Corrections { get; set; } = new();

        /// <summary>
        /// Errors that prevented validation from passing.
        /// Injected into Observation for LLM to fix on next turn.
        /// </summary>
        /// <remarks>中文：导致验证无法通过的错误。注入观察系统以便LLM在下一回合修复。</remarks>
        public List<string> Errors { get; set; } = new();

        /// <summary>
        /// Warnings that didn't block execution.
        /// E.g. "空间参考参数的值不像 WKID"
        /// </summary>
        public List<string> Warnings { get; set; } = new();

        /// <summary>Whether the tool call can proceed (Proceed or Corrected).</summary>
        /// <remarks>中文：工具调用是否可以继续（继续或纠正）。</remarks>
        public bool IsProceedable => Action != ValidationAction.Rejected;
    }
}
