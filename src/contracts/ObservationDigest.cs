namespace SpatialAgent.Runtime.Contracts
{
    public class ObservationDigest
    {
        public int StepId { get; set; }
        public string ToolName { get; set; } = "";
        public string Status { get; set; } = "";
        public bool Success { get; set; }
        public string Summary { get; set; } = "";
        public string? RawExcerpt { get; set; }
        public string? OutputPath { get; set; }
        public string? GeometryType { get; set; }
        public string? CRS { get; set; }
        public long? FeatureCount { get; set; }
        public int? RowCount { get; set; }
        public List<string> Fields { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public List<string> Artifacts { get; set; } = new();
    }

    public interface IObservationCompressor
    {
        ObservationDigest Compress(StepExecutionResult step, IObservation? observation = null, int maxRawChars = 240);
        string Format(ObservationDigest digest);
    }
}
