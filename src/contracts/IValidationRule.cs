namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Three-tier validation verdict.
    /// </summary>
    /// <remarks>中文：三档验证结论。</remarks>
    public enum ValidationVerdict
    {
        PASS,
        WARN,
        FAIL
    }

    /// <summary>
    /// Result of running a validation rule against an observation.
    /// </summary>
    /// <remarks>中文：对观察运行验证规则的结果。</remarks>
    public class ValidationResult
    {
        public string RuleName { get; set; } = "";
        public ValidationVerdict Verdict { get; set; } = ValidationVerdict.PASS;
        public string Detail { get; set; } = "";
        public string? RepairSuggestion { get; set; }
        /// <summary>
        /// Exact Goal proof obligations closed by this PASS. A generic PASS is
        /// deliberately not allowed to satisfy a parameter-scoped proof.
        /// </summary>
        /// <remarks>中文：该 PASS 所闭合的精确目标证明义务。通用 PASS 不得满足参数范围证明。</remarks>
        public List<string> SatisfiedProofObligationIds { get; set; } = new();
    }

    /// <summary>
    /// Validates a tool observation. Produces PASS/WARN/FAIL verdicts.
    /// </summary>
    /// <remarks>中文：验证工具 Observation，并产生 PASS/WARN/FAIL 结论。</remarks>
    public interface IValidationRule
    {
        string Name { get; }
        string Description { get; }

        /// <summary>Current conversation turn — set by orchestrator before validation.</summary>
        /// <remarks>中文：当前对话回合——由编排者在验证前设定。</remarks>
        int CurrentTurn { get; set; }

        /// <summary>Check if this rule applies to the given tool/observation</summary>
        /// <remarks>中文：检查规则是否适用于指定工具和 Observation。</remarks>
        bool AppliesTo(string toolName, IObservation observation);

        /// <summary>Execute the validation</summary>
        /// <remarks>中文：执行验证</remarks>
        Task<ValidationResult> ValidateAsync(IObservation observation);
    }
}
