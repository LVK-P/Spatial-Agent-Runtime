namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Workspace helper for project-level agent file geodatabases.
    /// Creates and manages agentbackup.gdb (pre-delete snapshots) and
    /// agentworkspace.gdb (all agent-generated outputs, flat at GDB root).
    /// </summary>
    /// <remarks>中文：项目级代理文件地理数据库工作区助手。创建和管理 agentbackup.gdb（删除前快照）和 agentworkspace.gdb（所有代理生成的输出，均为 GDB 根节点的平坦输出）。</remarks>
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
        /// <remarks>中文：在项目文件夹下初始化工作区。创建 agentbackup.gdb 和 agentworkspace.gdb。输出在GDB根节点平放存储——特征数据集是有意移除的（ADR 0044）：数据集在创建时固定一个空间参考（拒绝重投影输出），无法保存独立表。</remarks>
        Task<string> InitializeAsync(string projectFolder, int spatialRefWkid = 0);

        /// <summary>
        /// Back up a source dataset into agentbackup.gdb before a destructive
        /// operation. Returns the backup path, or null on failure.
        /// </summary>
        /// <remarks>中文：在进行破坏性操作之前，先将源数据集备份到 agentbackup.gdb。返回备份路径，失败时返回空。</remarks>
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
