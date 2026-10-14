using System.Threading.Tasks;

namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Structured project context — parsed once by ProjectReader, consumed directly
    /// by ViewModel. Eliminates the text-line-parsing protocol that coupled the two
    /// layers through hardcoded key names ("家目录："/"项目目录：" etc.).
    /// </summary>
    public sealed class ProjectContext
    {
        public bool IsAvailable { get; init; }
        public bool IsSaved { get; init; }
        public string ProjectName { get; init; } = "";
        /// <summary>Project home folder (only valid when IsSaved=true).</summary>
        /// <remarks>中文：项目主文件夹（仅在 IsSaved=true 时有效）。</remarks>
        public string? HomeFolder { get; init; }
        /// <summary>Temporary scratch folder assigned by Pro for unsaved projects.</summary>
        /// <remarks>中文：Pro为未保存项目分配的临时文件夹。</remarks>
        public string? TempFolder { get; init; }
        public string? DefaultGdbName { get; init; }
        public string? DefaultGdbPath { get; init; }
    }

    /// <summary>
    /// Structured snapshot of the live map. Text summaries remain available for
    /// prompting, but runtime policy, coverage checks and audit snapshots must
    /// consume this DTO instead of parsing presentation text.
    /// </summary>
    /// <remarks>中文：实时地图的结构化快照。文本摘要仍可用于提示词，但 Runtime 策略、覆盖检查和审计快照必须使用此 DTO，而不能解析展示文本。</remarks>
    public sealed class MapContextSnapshot
    {
        public bool IsAvailable { get; init; }
        public string MapName { get; init; } = "";
        public string SpatialReferenceName { get; init; } = "";
        public int Wkid { get; init; }
        public IReadOnlyList<LayerInfo> Layers { get; init; } = Array.Empty<LayerInfo>();
    }

    public interface IProjectReader
    {
        /// <summary>Plain-text project summary for LLM context and VAV fingerprinting.</summary>
        /// <remarks>中文：用于LLM上下文和VAV指纹识别的纯文本项目摘要。</remarks>
        Task<string> GetContextForAIAsync();
        /// <summary>Structured project metadata — consumed by ViewModel, no text parsing.</summary>
        /// <remarks>中文：结构化项目元数据——由ViewModel使用，无需解析文本。</remarks>
        Task<ProjectContext> GetProjectContextAsync();
        Task<string> GetProjectInfoAsync();
        Task<string> ScanFolderAsync(string path);
    }

    public interface IMapReader
    {
        /// <summary>
        /// Returns typed map/layer metadata in one host-dispatch operation.
        /// The default preserves compatibility with non-ArcGIS test adapters.
        /// </summary>
        /// <remarks>中文：在一次宿主调度操作中返回类型化的地图/图层元数据。默认实现用于保持与非 ArcGIS 测试适配器的兼容性。</remarks>
        Task<MapContextSnapshot> GetMapContextAsync() =>
            Task.FromResult(new MapContextSnapshot());
        Task<string> GetMapInfoAsync();
        Task<string> ListLayersAsync();
        Task<string> DescribeLayerAsync(string layerName);
        Task<string> GetAllLayerSummariesAsync();
        Task<string> GetSelectionAsync();
        /// <summary>
        /// Returns an opaque, non-content-bearing revision token for backing
        /// datasets. Hosts should include edits that do not change row counts
        /// (for example geometry or attribute edits). Empty means unavailable.
        /// </summary>
        /// <remarks>中文：返回底层数据集的不透明、无内容修订 Token。宿主应把不改变行数的编辑（如几何或属性编辑）纳入修订；空字符串表示不可用。</remarks>
        Task<string> GetContentRevisionAsync() => Task.FromResult("");
        Task<string> ReadRowsAsync(
            string layerName,
            IReadOnlyList<string> fields,
            string? whereClause,
            int maxRows,
            bool selectedOnly = false);
        Task<string> GetProjectExtentAsync();

        /// <summary>
        /// Collect spatial references from the active map and all layers.
        /// Detects mixed CRS and cross-datum scenarios.
        /// Added for P0-4: coordinate system context injection.
        /// </summary>
        /// <remarks>中文：收集活动地图及所有图层的空间参考，检测混合 CRS 和跨基准面场景。该方法为 P0-4 的坐标系上下文注入而增加。</remarks>
        Task<SpatialRefContext> GetSpatialRefContextAsync();
    }
}
