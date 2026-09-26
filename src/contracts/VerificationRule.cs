namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Verification rule definition — specifies what to check after tool execution.
    /// Used by PostConditionVerifier and GPToolContract.
    /// </summary>
    public class VerificationRule
    {
        public string Type { get; set; } = "notnull";
        public string Target { get; set; } = "";
        public string Expected { get; set; } = "";
        public string FixHint { get; set; } = "";
        public string? Params { get; set; }
        public bool Invert { get; set; }
    }
}
