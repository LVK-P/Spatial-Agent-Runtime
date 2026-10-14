namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Verification rule definition — specifies what to check after tool execution.
    /// Used by PostConditionVerifier and GPToolContract.
    /// </summary>
    /// <remarks>中文：验证规则定义——规定工具执行后需要检查的内容。被PostConditionVerifier和GPToolContract使用。</remarks>
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
