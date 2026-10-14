using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SpatialAgent.Adapters.ArcGIS;
using SpatialAgent.Runtime.Contracts;
using SpatialAgent.Core;

// ContextSnapshotter.cs — Layer 4: context snapshots.
// Captures project/map/layer state at key decision points to answer:
// "What did the agent see when it made that incorrect judgment?"
//
// Trigger points (subscribed via EventBus):
//   turn.start           → baseline state before each turn
//   llm.calling           → context before LLM makes tool-call decisions
//   verification.failed   → scene of the failure (what went wrong)
//
// Output: {project}/agent_logs/snapshots_{timestamp}.jsonl
// Sparse sampling — only recorded at critical moments.
// 中文：ContextSnapshotter.cs — 第4层：上下文快照。捕捉项目/地图/图层状态，关键决策点以回答：“智能体在做出错误判断时看到了什么？”触发点（通过EventBus订阅）：turn.start →每次回合前的基线状态 llm.calling → LLM做出工具调用决策前的上下文 verification.fail → 失败场景（出了什么问题） 输出：{project}/agent_logs/snapshots_{timestamp}.jsonl 稀疏采样——仅在关键时刻记录。
namespace SpatialAgent.Services
{
    public class ContextSnapshotter : IDisposable, Runtime.Contracts.IContextSnapshotter
    {
        private readonly IMapReader _mapReader;
        private readonly IProjectReader _projectReader;
        private readonly object _lock = new();
        private string? _filePath;
        private StreamWriter? _writer;
        private bool _disposed;

        // ── ★ Phase B2（2026-07-20）：健康监控 ──
        // English：Phase B2 (2026-07-20): Health monitoring
        private Runtime.Contracts.ILoggingHealthMonitor? _healthMonitor;
        private int _writeFailures;

        public void SetLoggingHealthMonitor(Runtime.Contracts.ILoggingHealthMonitor? hm) => _healthMonitor = hm;
        public int WriteFailures => _writeFailures;

        public string? FilePath => _filePath;
        public int SnapshotCount { get; private set; }

        /// <summary>Last snapshot for delta comparison.</summary>
        /// <remarks>中文：最后一张用于差异比较的快照。</remarks>
        private SnapshotEntry? _lastSnapshot;

        public ContextSnapshotter(IMapReader? mapReader = null, IProjectReader? projectReader = null)
        {
            _mapReader = mapReader ?? new MapReader();
            _projectReader = projectReader ?? new ProjectReader();
        }

        public void Initialize(string logDir)
        {
            lock (_lock)
            {
                if (_writer != null) return;
                Directory.CreateDirectory(logDir);
                _filePath = Path.Combine(logDir, $"snapshots_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");
                _writer = new StreamWriter(_filePath, append: true, Encoding.UTF8) { AutoFlush = true };
            }
        }

        public void MigrateTo(string logDir)
        {
            lock (_lock)
            {
                if (_writer == null || _filePath == null || _disposed)
                {
                    Initialize(logDir);
                    return;
                }

                string currentDir = Path.GetDirectoryName(_filePath) ?? "";
                string targetDir = Path.GetFullPath(logDir);
                if (Path.GetFullPath(currentDir).Equals(targetDir, StringComparison.OrdinalIgnoreCase))
                    return;

                Directory.CreateDirectory(targetDir);
                string targetPath = Path.Combine(targetDir, Path.GetFileName(_filePath));
                _writer.Flush();
                _writer.Dispose();
                File.Move(_filePath, targetPath, overwrite: true);
                _filePath = targetPath;
                _writer = new StreamWriter(_filePath, append: true, Encoding.UTF8) { AutoFlush = true };
            }
        }

        /// <summary>
        /// Take a snapshot of current project/map state.
        /// Called from EventBus subscribers or directly.
        /// </summary>
        /// <remarks>中文：对当前项目和地图状态做个快照。无论是EventBus订阅者还是直接拨打。</remarks>
        public async Task SnapshotAsync(string trigger, int turn, string? detail = null)
        {
            lock (_lock)
            {
                if (_writer == null || _disposed) return;
            }

            try
            {
                var entry = new SnapshotEntry
                {
                    ts = DateTime.Now.ToString("HH:mm:ss.fff"),
                    trigger = trigger,
                    turn = turn,
                    detail = detail
                };

                // Capture map and layer state through one typed host snapshot.
                // 中文：通过一个类型的主机快照捕获地图和层状态。
                try
                {
                    MapContextSnapshot map = await _mapReader.GetMapContextAsync();
                    entry.map_name = map.IsAvailable ? map.MapName : "(unavailable)";
                    entry.crs = map.Wkid > 0
                        ? map.Wkid.ToString()
                        : map.SpatialReferenceName;
                    entry.layers = map.Layers.Select(layer => new
                    {
                        name = layer.Name,
                        type = layer.GeometryType,
                        count = layer.FeatureCount,
                        fields = layer.Fields
                    }).ToList();
                }
                catch
                {
                    entry.map_name = "(unavailable)";
                    entry.layers = new List<object>();
                }

                // Capture project info
                // 中文：捕获项目信息
                try
                {
                    string projJson = await _projectReader.GetProjectInfoAsync();
                    var projInfo = JsonConvert.DeserializeObject<dynamic>(projJson);
                    entry.project_name = (string?)projInfo?.name;
                    entry.default_gdb = (string?)projInfo?.defaultGdb;
                }
                catch { entry.project_name = "(unavailable)"; }

                // Compute delta from last snapshot
                // 中文：从上次快照计算 delta
                if (_lastSnapshot != null)
                {
                    var delta = new List<string>();
                    int prevLayerCount = (_lastSnapshot.layers as IEnumerable<object>)?.Count() ?? 0;
                    int currLayerCount = (entry.layers as IEnumerable<object>)?.Count() ?? 0;
                    if (prevLayerCount != currLayerCount)
                        delta.Add($"layers: {prevLayerCount}→{currLayerCount}");
                    else
                        delta.Add("no change");
                    entry.delta = delta;
                }

                SnapshotCount++;
                _lastSnapshot = entry;

                // ★ Phase A4：自动注入关联 ID
                // English：Phase A4: Automatically inject associated IDs
                string runId = CorrelationContext.RunId;
                string spanId = CorrelationContext.SpanId;
                if (!string.IsNullOrEmpty(runId)) entry.run_id = runId;
                if (!string.IsNullOrEmpty(spanId)) entry.span_id = spanId;

                lock (_lock)
                {
                    if (_writer != null && !_disposed)
                    {
                        string json = JsonConvert.SerializeObject(entry, Formatting.None,
                            new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
                        _writer.WriteLine(json);
                    }
                }
            }
            catch (Exception ex)
            {
                _writeFailures++;
                _healthMonitor?.ReportWriteFailure("snapshot", ex.GetType().Name);
            }
        }

        public void Flush()
        {
            lock (_lock) { try { _writer?.Flush(); } catch { } }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                try { _writer?.Flush(); _writer?.Dispose(); } catch { }
            }
        }
    }

    /// <summary>
    /// Single snapshot entry — written to snapshots.jsonl.
    /// </summary>
    /// <remarks>中文：单一快照条目——写入snapshots.jsonl。</remarks>
    internal class SnapshotEntry
    {
        [JsonProperty("ts")]
        public string ts { get; set; } = "";

        [JsonProperty("trigger")]
        public string trigger { get; set; } = "";

        [JsonProperty("turn")]
        public int turn { get; set; }

        [JsonProperty("detail")]
        public string? detail { get; set; }

        [JsonProperty("project")]
        public string? project_name { get; set; }

        [JsonProperty("gdb")]
        public string? default_gdb { get; set; }

        [JsonProperty("map")]
        public string? map_name { get; set; }

        [JsonProperty("crs")]
        public string? crs { get; set; }

        [JsonProperty("layers")]
        public object? layers { get; set; }

        [JsonProperty("delta")]
        public List<string>? delta { get; set; }

        /// <summary>★ Phase A4：关联 run ID</summary>
        /// <remarks>中文：A4阶段：关联 run ID</remarks>
        [JsonProperty("run_id")]
        public string? run_id { get; set; }

        /// <summary>★ Phase A4：关联 span ID</summary>
        /// <remarks>中文：A4阶段：关联 span ID</remarks>
        [JsonProperty("span_id")]
        public string? span_id { get; set; }
    }
}
