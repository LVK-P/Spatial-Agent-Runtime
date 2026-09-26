namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Structured observation of a single tool execution.
    /// Not a string log — a typed struct that Validation can reason about.
    /// </summary>
    public interface IObservation
    {
        string ToolName { get; }
        string ToolCategory { get; }
        TimeSpan Elapsed { get; }
        ObservationOutput Output { get; }
        IReadOnlyList<string> Warnings { get; }
        IReadOnlyList<string> Artifacts { get; }
    }

    /// <summary>
    /// Typed output from tool execution.
    /// Optional fields — not all tools produce all outputs.
    /// </summary>
    public class ObservationOutput
    {
        /// <summary>Resource name echoed by a read probe, when available.</summary>
        public string? ResourceName { get; set; }

        /// <summary>Feature count if tool produced a feature layer</summary>
        public long? FeatureCount { get; set; }

        /// <summary>Geometry type: "Point", "Polyline", "Polygon", null if not applicable</summary>
        public string? GeometryType { get; set; }

        /// <summary>Coordinate system WKID, e.g. "4490" for CGCS2000</summary>
        public string? CRS { get; set; }

        /// <summary>Bounding box: xmin, ymin, xmax, ymax</summary>
        public ExtentInfo? Extent { get; set; }

        /// <summary>Row count if tool produced a table</summary>
        public int? RowCount { get; set; }

        /// <summary>Field names if tool modified a table schema</summary>
        public List<string>? Fields { get; set; }

        /// <summary>Raw output for debugging (truncated)</summary>
        public string? Raw { get; set; }
    }

    /// <summary>
    /// Bounding box info.
    /// </summary>
    public class ExtentInfo
    {
        public double XMin { get; set; }
        public double YMin { get; set; }
        public double XMax { get; set; }
        public double YMax { get; set; }
    }
}
