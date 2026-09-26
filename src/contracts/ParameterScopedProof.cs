namespace SpatialAgent.Runtime.Contracts
{
    public enum MeasurementProofKind
    {
        Unknown,
        Length,
        Area
    }

    /// <summary>
    /// Adapter-neutral request to prove that numeric values satisfy the same
    /// capability and parameter contract that governed execution.
    /// </summary>
    public sealed class ParameterScopedProofRequirement
    {
        public string ProofObligationId { get; set; } = "";
        public string CapabilityId { get; set; } = "";
        public MeasurementProofKind MeasurementKind { get; set; }
        public string Layer { get; set; } = "";
        public string DatasetId { get; set; } = "";
        public string Field { get; set; } = "";
        public string Unit { get; set; } = "";
        public string MeasurementMethod { get; set; } = "";
        public int DecimalPlaces { get; set; }
        public string RoundingMode { get; set; } = "TO_EVEN";
        public int SampleSize { get; set; } = 64;
    }

    public sealed class ParameterScopedProofResult
    {
        public bool Supported { get; set; }
        public bool Passed { get; set; }
        public int SampledCount { get; set; }
        public int MismatchCount { get; set; }
        public double? MaximumAbsoluteError { get; set; }
        public double? MaximumAllowedAbsoluteError { get; set; }
        public string Detail { get; set; } = "";
    }
}
