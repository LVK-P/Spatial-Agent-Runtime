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
    public enum ValidationAction
    {
        /// <summary>Parameters are correct — proceed to execution.</summary>
        Proceed,

        /// <summary>Minor issues auto-corrected — proceed with corrected args.</summary>
        Corrected,

        /// <summary>Unrecoverable errors — return Observation to LLM for fix, do not execute.</summary>
        Rejected
    }

    /// <summary>
    /// Result of pre-execution parameter validation.
    /// Returned by ParameterValidator.Validate() before Scheduler executes a tool.
    /// </summary>
    public class ParameterValidationResult
    {
        /// <summary>Overall action for this tool call.</summary>
        public ValidationAction Action { get; set; } = ValidationAction.Proceed;

        /// <summary>
        /// Corrected arguments (may differ from original if auto-correct applied).
        /// Always populated — equals input args if no corrections.
        /// </summary>
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
        public List<string> Errors { get; set; } = new();

        /// <summary>
        /// Warnings that didn't block execution.
        /// E.g. "空间参考参数的值不像 WKID"
        /// </summary>
        public List<string> Warnings { get; set; } = new();

        /// <summary>Whether the tool call can proceed (Proceed or Corrected).</summary>
        public bool IsProceedable => Action != ValidationAction.Rejected;
    }
}
