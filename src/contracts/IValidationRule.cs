namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Three-tier validation verdict.
    /// </summary>
    public enum ValidationVerdict
    {
        PASS,
        WARN,
        FAIL
    }

    /// <summary>
    /// Result of running a validation rule against an observation.
    /// </summary>
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
        public List<string> SatisfiedProofObligationIds { get; set; } = new();
    }

    /// <summary>
    /// Validates a tool observation. Produces PASS/WARN/FAIL verdicts.
    /// </summary>
    public interface IValidationRule
    {
        string Name { get; }
        string Description { get; }

        /// <summary>Current conversation turn — set by orchestrator before validation.</summary>
        int CurrentTurn { get; set; }

        /// <summary>Check if this rule applies to the given tool/observation</summary>
        bool AppliesTo(string toolName, IObservation observation);

        /// <summary>Execute the validation</summary>
        Task<ValidationResult> ValidateAsync(IObservation observation);
    }
}
