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
        public string? HomeFolder { get; init; }
        /// <summary>Temporary scratch folder assigned by Pro for unsaved projects.</summary>
        public string? TempFolder { get; init; }
        public string? DefaultGdbName { get; init; }
        public string? DefaultGdbPath { get; init; }
    }

    /// <summary>
    /// Structured snapshot of the live map. Text summaries remain available for
    /// prompting, but runtime policy, coverage checks and audit snapshots must
    /// consume this DTO instead of parsing presentation text.
    /// </summary>
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
        Task<string> GetContextForAIAsync();
        /// <summary>Structured project metadata — consumed by ViewModel, no text parsing.</summary>
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
        Task<SpatialRefContext> GetSpatialRefContextAsync();
    }
}
