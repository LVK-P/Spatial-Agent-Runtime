using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ArcGIS.Desktop.Core.Geoprocessing;
using ArcGIS.Desktop.Mapping;
using SpatialAgent.Adapters.ArcGIS;
using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Services
{
    public class WorkspaceTool : IWorkspaceService
    {
        private readonly SpatialAgent.Adapters.ArcGIS.MapReader _mapReader = new();
        private IGisDispatcher _dispatcher = new ProGisDispatcher();

        public string WorkspaceRoot { get; private set; } = "";
        public string BackupGdb { get; private set; } = "";
        public string WorkspaceGdb { get; private set; } = "";

        /// <summary>Inject a custom GIS dispatcher (optional; defaults to ProGisDispatcher).</summary>
        public void SetDispatcher(IGisDispatcher? dispatcher)
        {
            _dispatcher = dispatcher ?? new ProGisDispatcher();
        }

        /// <summary>
        /// Set workspace paths without touching the GIS (sync, testable).
        /// InitializeAsync calls this before creating the GDBs.
        /// </summary>
        public void Configure(string projectFolder)
        {
            WorkspaceRoot = projectFolder;
            BackupGdb = Path.Combine(projectFolder, "agentbackup.gdb");
            WorkspaceGdb = Path.Combine(projectFolder, "agentworkspace.gdb");
        }

        /// <summary>
        /// Initialize the workspace: agentbackup.gdb and agentworkspace.gdb
        /// at the project root. Outputs live flat at the GDB root — feature
        /// datasets were removed on purpose (ADR 0044): a dataset pins one
        /// spatial reference at creation time (breaks reprojection outputs)
        /// and cannot hold standalone tables (breaks Statistics outputs).
        /// </summary>
        public async Task<string> InitializeAsync(string projectFolder, int spatialRefWkid = 0)
        {
            Configure(projectFolder);
            _ = spatialRefWkid; // kept for interface compatibility; no dataset needs it now

            var log = new StringBuilder();
            try
            {
                if (!Directory.Exists(BackupGdb))
                {
                    if (await CreateGdbAsync(BackupGdb))
                        log.AppendLine("agentbackup.gdb created");
                    else
                        log.AppendLine("⚠ agentbackup.gdb creation failed");
                }

                if (!Directory.Exists(WorkspaceGdb))
                {
                    if (await CreateGdbAsync(WorkspaceGdb))
                        log.AppendLine("agentworkspace.gdb created");
                    else
                        log.AppendLine("⚠ agentworkspace.gdb creation failed");
                }
            }
            catch (Exception ex) { log.AppendLine($"⚠ {ex.Message}"); }
            return log.ToString();
        }

        /// <summary>
        /// Back up a source dataset into agentbackup.gdb before a destructive operation.
        /// srcPath 可以是完整路径（如 G:\proj\data.gdb\roads）或图层简称（如 乡镇边界_420300）。
        /// 简称会通过活动地图解析为完整数据源路径后再执行 Copy_management。
        /// Copy is used instead of CopyFeatures so standalone tables, feature
        /// classes and other geodatabase datasets share one fail-closed path.
        /// </summary>
        public async Task<string?> BackupAsync(
            string srcPath,
            string opName,
            CancellationToken cancellation = default)
        {
            try
            {
                cancellation.ThrowIfCancellationRequested();
                System.Diagnostics.Debug.WriteLine($"[WorkspaceTool.BackupAsync] START srcPath={srcPath} opName={opName} BackupGdb={BackupGdb}");

                if (string.IsNullOrWhiteSpace(srcPath) || string.IsNullOrWhiteSpace(BackupGdb))
                {
                    System.Diagnostics.Debug.WriteLine("[WorkspaceTool.BackupAsync] FAIL: srcPath or BackupGdb empty");
                    return null;
                }

                // ★ 确保备份 GDB 存在（初始化时可能创建失败，此处兜底）
                if (!Directory.Exists(BackupGdb))
                {
                    System.Diagnostics.Debug.WriteLine($"[WorkspaceTool.BackupAsync] BackupGdb missing, creating: {BackupGdb}");
                    bool created = await CreateGdbAsync(BackupGdb, cancellation);
                    if (!created)
                    {
                        System.Diagnostics.Debug.WriteLine("[WorkspaceTool.BackupAsync] FAIL: CreateGdbAsync returned false");
                        return null;
                    }
                    System.Diagnostics.Debug.WriteLine($"[WorkspaceTool.BackupAsync] BackupGdb created OK: {BackupGdb}");
                }

                // 不是完整路径 → 尝试从当前活动地图解析层名
                string? resolvedPath = srcPath;
                if (!LooksLikeFilePath(srcPath))
                {
                    resolvedPath = await _mapReader.ResolveLayerPathAsync(srcPath);
                    System.Diagnostics.Debug.WriteLine($"[WorkspaceTool.BackupAsync] Layer name resolved: {srcPath} -> {resolvedPath ?? "NULL"}");
                    if (string.IsNullOrWhiteSpace(resolvedPath))
                    {
                        System.Diagnostics.Debug.WriteLine("[WorkspaceTool.BackupAsync] FAIL: ResolveLayerPathAsync returned null");
                        return null;
                    }
                }

                var name = Path.GetFileNameWithoutExtension(srcPath);
                var backupName = $"backup_{SanitizeName(opName)}_{SanitizeName(name)}_{DateTime.Now:HHmmssfff}";
                var backupPath = Path.Combine(BackupGdb, backupName);
                System.Diagnostics.Debug.WriteLine($"[WorkspaceTool.BackupAsync] Copy_management src={resolvedPath} dst={backupPath}");
                bool ok = await RunGPToolAsync(
                    cancellation,
                    "Copy_management",
                    resolvedPath,
                    backupPath);
                System.Diagnostics.Debug.WriteLine($"[WorkspaceTool.BackupAsync] Copy_management returned: {ok}");
                return ok ? backupPath : null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WorkspaceTool.BackupAsync] EXCEPTION: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>包含 \ 或 .gdb → 认为是完整路径而非图层简称。</summary>
        private static bool LooksLikeFilePath(string value) =>
            value.Contains('\\') || value.Contains('/') || value.Contains(".gdb", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Resolve a simple output name to agentworkspace.gdb/<name> (flat at
        /// GDB root). No feature-dataset routing: datasets pin a spatial
        /// reference and reject standalone tables — both broke real tasks
        /// (ERROR 000210 on Statistics, silent loss of reprojected outputs).
        /// </summary>
        public string ResolveOutputPath(string simpleName, string toolName = "", string geometryType = "")
        {
            if (string.IsNullOrWhiteSpace(simpleName))
                return simpleName;

            // Already a full path or in-memory — leave unchanged.
            if (simpleName.Contains('\\') || simpleName.Contains('/')
                || simpleName.Contains(".gdb") || simpleName.StartsWith("in_memory"))
                return simpleName;

            if (string.IsNullOrWhiteSpace(WorkspaceGdb))
                return simpleName; // workspace not configured — let GP use its default

            return Path.Combine(WorkspaceGdb, simpleName);
        }

        public async Task AddToMapAsync(string path, string? layerName = null)
        {
            // ★ 备份 GDB 内的数据不加入地图——否则 LLM 会把备份图层也当作
            // 待处理目标，形成"备份→加入地图→LLM 看到备份→再次备份备份"的套娃循环。
            if (!string.IsNullOrWhiteSpace(BackupGdb) &&
                path.StartsWith(BackupGdb, StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                await _dispatcher.RunAsync(() =>
                {
                    var map = MapView.Active?.Map;
                    if (map == null) return Task.CompletedTask;
                    var uri = new Uri(path);
                    var layer = LayerFactory.Instance.CreateLayer(uri, map);
                    if (layer != null && !string.IsNullOrWhiteSpace(layerName))
                        layer.SetName(layerName);
                    return Task.CompletedTask;
                });
            }
            catch { /* non-critical — map display is best-effort */ }
        }

        /// <summary>
        /// 从当前地图中移除指定路径的图层。备份完成后调用，避免 Pro 自动
        /// 发现 agentbackup.gdb 后将其图层加入地图。
        /// </summary>
        public async Task RemoveFromMapAsync(
            string path,
            CancellationToken cancellation = default)
        {
            try
            {
                cancellation.ThrowIfCancellationRequested();
                await _dispatcher.RunAsync(() =>
                {
                    var map = MapView.Active?.Map;
                    if (map == null) return Task.CompletedTask;
                    for (int i = map.Layers.Count - 1; i >= 0; i--)
                    {
                        if (map.Layers[i] is FeatureLayer fl)
                        {
                            var ds = fl.GetFeatureClass()?.GetDatastore()?.GetPath()?.ToString() ?? "";
                            if (ds.Contains("agentbackup.gdb", StringComparison.OrdinalIgnoreCase))
                                map.RemoveLayer(map.Layers[i]);
                        }
                    }
                    return Task.CompletedTask;
                }, cancellation);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* best-effort */ }
        }

        private async Task<bool> CreateGdbAsync(
            string path,
            CancellationToken cancellation = default)
        {
            var dir = Path.GetDirectoryName(path) ?? "";
            var name = Path.GetFileName(path);
            return await RunGPToolAsync(
                cancellation,
                "CreateFileGDB_management",
                dir,
                name);
        }

        private async Task<bool> RunGPToolAsync(string tool, params string[] args)
        {
            return await RunGPToolAsync(CancellationToken.None, tool, args);
        }

        private async Task<bool> RunGPToolAsync(CancellationToken ct, string tool, params string[] args)
        {
            ct.ThrowIfCancellationRequested();
            var vals = await _dispatcher.RunAsync(
                () => Task.FromResult(Geoprocessing.MakeValueArray(args)),
                ct);
            Action<ToolExecutionProgressUpdate>? reporter =
                ToolExecutionProgressContext.CaptureReporter();
            var result = await Geoprocessing.ExecuteToolAsync(
                tool,
                vals,
                null,
                ct,
                (eventName, payload) =>
                {
                    if (reporter == null)
                        return;
                    string? message = eventName switch
                    {
                        "OnValidate" => "正在验证安全备份参数",
                        "OnBeginExecute" => "正在创建安全备份",
                        "OnProgressMessage" => payload?.ToString(),
                        "OnEndExecute" => "安全备份已完成",
                        _ => null
                    };
                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        reporter(new ToolExecutionProgressUpdate
                        {
                            Phase = "backup",
                            Message = message
                        });
                    }
                },
                GPToolRunner.InfrastructureFlags);
            return result != null
                && !result.IsFailed
                && !result.ErrorMessages.Any();
        }

        private static string SanitizeName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "unnamed";

            var chars = value.Trim()
                .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
                .ToArray();
            string result = new string(chars);
            while (result.Contains("__", StringComparison.Ordinal))
                result = result.Replace("__", "_");
            return result.Trim('_');
        }
    }
}
