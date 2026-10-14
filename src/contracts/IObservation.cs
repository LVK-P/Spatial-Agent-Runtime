namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Structured observation of a single tool execution.
    /// Not a string log — a typed struct that Validation can reason about.
    /// </summary>
    /// <remarks>中文：对单个工具执行进行结构化观察。不是字符串日志——而是Validation可以推理的类型结构体。</remarks>
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
    /// <remarks>中文：工具执行时的打字输出。可选字段——并非所有工具都能产生所有输出。</remarks>
    public class ObservationOutput
    {
        /// <summary>Resource name echoed by a read probe, when available.</summary>
        /// <remarks>中文：资源名称由读取探针回应（如有）。</remarks>
        public string? ResourceName { get; set; }

        /// <summary>Feature count if tool produced a feature layer</summary>
        /// <remarks>中文：如果工具生成了特征图层，特征计数</remarks>
        public long? FeatureCount { get; set; }

        /// <summary>Geometry type: "Point", "Polyline", "Polygon", null if not applicable</summary>
        /// <remarks>中文：几何类型：“点”、“多边形”、“多边形”，若不适用则为空</remarks>
        public string? GeometryType { get; set; }

        /// <summary>Coordinate system WKID, e.g. "4490" for CGCS2000</summary>
        /// <remarks>中文：坐标系WKID，例如“4490”代表CGCS2000</remarks>
        public string? CRS { get; set; }

        /// <summary>Bounding box: xmin, ymin, xmax, ymax</summary>
        /// <remarks>中文：边界框：xmin、ymin、xmax、ymax</remarks>
        public ExtentInfo? Extent { get; set; }

        /// <summary>Row count if tool produced a table</summary>
        /// <remarks>中文：如果工具产生了表格，行数</remarks>
        public int? RowCount { get; set; }

        /// <summary>Field names if tool modified a table schema</summary>
        /// <remarks>中文：如果工具修改了表模式，字段名称</remarks>
        public List<string>? Fields { get; set; }

        /// <summary>Raw output for debugging (truncated)</summary>
        /// <remarks>中文：用于调试的原始输出（截断）</remarks>
        public string? Raw { get; set; }
    }

    /// <summary>
    /// Bounding box info.
    /// </summary>
    /// <remarks>中文：边界盒信息。</remarks>
    public class ExtentInfo
    {
        public double XMin { get; set; }
        public double YMin { get; set; }
        public double XMax { get; set; }
        public double YMax { get; set; }
    }
}
