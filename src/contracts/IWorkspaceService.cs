namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Workspace helper for project-level agent file geodatabases.
    /// Creates and manages agentbackup.gdb (pre-delete snapshots) and
    /// agentworkspace.gdb (all agent-generated outputs, flat at GDB root).
    /// </summary>
    public interface IWorkspaceService
    {
        string WorkspaceRoot { get; }
        string BackupGdb { get; }
        string WorkspaceGdb { get; }

        /// <summary>
        /// Initialize the workspace under the project folder.
        /// Creates agentbackup.gdb and agentworkspace.gdb. Outputs are stored
        /// flat at the GDB root — feature datasets were removed on purpose
        /// (ADR 0044): a dataset pins one spatial reference at creation time
        /// (rejects reprojected outputs) and cannot hold standalone tables.
        /// </summary>
        Task<string> InitializeAsync(string projectFolder, int spatialRefWkid = 0);

        /// <summary>
        /// Back up a source dataset into agentbackup.gdb before a destructive
        /// operation. Returns the backup path, or null on failure.
        /// </summary>
        Task<string?> BackupAsync(
            string srcPath,
            string opName,
            CancellationToken cancellation = default);

        /// <summary>
        /// Resolve a simple output name to a full path at the agentworkspace.gdb
        /// root. Example: "tmp_道路重叠" → "…\agentworkspace.gdb\tmp_道路重叠".
        /// Bare names already containing a path separator, ".gdb" or "in_memory"
        /// are returned as-is; unconfigured workspace returns the name unchanged.
        /// </summary>
        string ResolveOutputPath(string simpleName, string toolName = "", string geometryType = "");

        /// <summary>
        /// 从当前地图中移除 agentbackup.gdb 内的图层。备份完成后调用，
        /// 避免 Pro 自动发现备份 GDB 后将其图层加入地图导致套娃循环。
        /// </summary>
        Task RemoveFromMapAsync(
            string path,
            CancellationToken cancellation = default);
    }
}
