using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArcGIS.Desktop.Core.Geoprocessing;
using Newtonsoft.Json;
using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Adapters.ArcGIS
{
    /// <summary>
    /// Executes ArcGIS Pro GP tools. Wraps the Pro SDK Geoprocessing API.
    /// Takes positional string arguments, returns JSON result.
    ///
    /// P0-1 hardening: Uses IGisDispatcher for MCT thread marshaling
    /// instead of direct QueuedTask.Run().
    /// P0-3 hardening: Accepts CancellationToken for timeout/cancellation.
    /// </summary>
    public class GPToolRunner
    {
        private readonly IGisDispatcher _dispatcher;
        private static readonly object WarmUpLock = new();
        private static Task? _warmUpTask;

        /// <summary>
        /// Preserve the product behavior of adding user outputs to the map,
        /// but run the GP engine on its dedicated worker and make every side
        /// effect explicit instead of relying on Default.
        /// </summary>
        public static GPExecuteToolFlags UserOutputFlags =>
            GPExecuteToolFlags.AddOutputsToMap
            | GPExecuteToolFlags.RefreshProjectItems
            | GPExecuteToolFlags.GPThread;

        /// <summary>
        /// Runtime infrastructure (backup/warm-up) must never add artifacts to
        /// the map or refresh project items while optimistic concurrency is
        /// being evaluated.
        /// </summary>
        public static GPExecuteToolFlags InfrastructureFlags =>
            GPExecuteToolFlags.GPThread;

        /// <summary>
        /// Set once at startup before any GPToolRunner is instantiated.
        /// Allows existing callers (SchemaGPToolAdapter, CalculateAreaTool, etc.)
        /// to use the parameterless constructor without a compile-time dependency on Adapters.
        /// </summary>
        public static IGisDispatcher? DefaultDispatcher { get; set; }

        public GPToolRunner(IGisDispatcher? dispatcher = null)
        {
            _dispatcher = dispatcher ?? DefaultDispatcher
                ?? throw new InvalidOperationException(
                    "GPToolRunner requires an IGisDispatcher. " +
                    "Set GPToolRunner.DefaultDispatcher at startup, " +
                    "or pass an IGisDispatcher to the constructor.");
        }

        /// <summary>
        /// Starts a process-wide, single-flight GP engine warm-up. The probe is
        /// read-only and uses infrastructure flags, so it cannot add map output
        /// or refresh project items. Normal executions join an in-flight probe
        /// instead of competing with ArcGIS Pro's cold initialization.
        /// </summary>
        public Task WarmUpAsync(
            string probeDataset,
            Action<ToolExecutionProgressUpdate>? reporter = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(probeDataset))
                return Task.CompletedTask;

            lock (WarmUpLock)
            {
                if (_warmUpTask == null
                    || _warmUpTask.IsFaulted
                    || _warmUpTask.IsCanceled)
                {
                    _warmUpTask = RunWarmUpAsync(
                        probeDataset,
                        reporter,
                        ct);
                }

                return _warmUpTask;
            }
        }

        /// <summary>
        /// Execute a GP tool by name with positional arguments.
        /// Tool name format: "management.AddField" or "AddField_management" (auto-normalized).
        /// </summary>
        public async Task<string> ExecuteAsync(
            string toolName,
            IReadOnlyList<string> arguments,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            await JoinWarmUpAsync(ct);

            // Normalize tool name: underscore → dot format
            string normalizedName = toolName.Contains('_') && !toolName.Contains('.')
                ? toolName[(toolName.LastIndexOf('_') + 1)..] + "." + toolName[..toolName.LastIndexOf('_')]
                : toolName;

            // String-only MakeValueArray is fast, but marshal it through the
            // host dispatcher so this runner remains safe if a future adapter
            // supplies ArcGIS objects. Do not hold the MCT while GP executes.
            var values = await _dispatcher.RunAsync(
                () => Task.FromResult(
                    Geoprocessing.MakeValueArray(arguments.ToArray())),
                ct);

            Action<ToolExecutionProgressUpdate>? reporter =
                ToolExecutionProgressContext.CaptureReporter();
            int lastPercent = -1;
            object progressLock = new();
            void Report(string phase, string message, int? percent = null)
            {
                if (reporter == null)
                    return;

                if (percent.HasValue)
                {
                    lock (progressLock)
                    {
                        int bounded = Math.Clamp(percent.Value, 0, 100);
                        if (bounded != 100
                            && lastPercent >= 0
                            && bounded - lastPercent < 5)
                            return;
                        lastPercent = bounded;
                        percent = bounded;
                    }
                }

                reporter(new ToolExecutionProgressUpdate
                {
                    Phase = phase,
                    Message = message,
                    Percent = percent
                });
            }

            var result = await Geoprocessing.ExecuteToolAsync(
                normalizedName,
                values,
                null,
                ct,
                (eventName, payload) =>
                {
                    switch (eventName)
                    {
                        case "OnValidate":
                            Report("gp_validate", "ArcGIS 参数验证完成");
                            break;
                        case "OnBeginExecute":
                            Report("gp_execute", "ArcGIS 地理处理已开始");
                            break;
                        case "OnProgressMessage":
                            Report(
                                "gp_execute",
                                payload?.ToString() ?? "ArcGIS 地理处理中");
                            break;
                        case "OnProgressPos" when payload is int percent:
                            Report("gp_execute", "ArcGIS 地理处理中", percent);
                            break;
                        case "OnEndExecute":
                            Report("gp_complete", "ArcGIS 地理处理已结束", 100);
                            break;
                    }
                },
                UserOutputFlags);

            var errors = new List<string>();
            var warnings = new List<string>();
            var allMessages = new List<string>();

            foreach (var msg in result.Messages)
            {
                string text = msg.Text;
                allMessages.Add($"[{msg.Type}] {text}");
                if (msg.Type == GPMessageType.Error) errors.Add(text);
                else if (msg.Type == GPMessageType.Warning) warnings.Add(text);
            }

            // ArcGIS' IsFailed flag is authoritative. Some failures expose
            // details only through ErrorMessages, and a few expose no text
            // at all; neither case may be serialized as success.
            if (result.IsFailed)
            {
                foreach (string error in result.ErrorMessages
                             .Select(message => message.Text)
                             .Where(text => !string.IsNullOrWhiteSpace(text)))
                {
                    if (!errors.Contains(error, StringComparer.Ordinal))
                        errors.Add(error);
                }

                if (errors.Count == 0)
                    errors.Add("ArcGIS 地理处理报告失败，但未提供错误详情。");
            }

            bool hasRealErrors = errors.Count > 0;
            bool succeeded = !result.IsFailed && !hasRealErrors;

            return JsonConvert.SerializeObject(new
            {
                success = succeeded,
                toolName = normalizedName,
                errorCount = errors.Count,
                warningCount = warnings.Count,
                errors,
                warnings,
                allMessages,
                isFailed = result.IsFailed,
                summary = succeeded ? "执行成功" : string.Join("; ", errors)
            });
        }

        private async Task JoinWarmUpAsync(CancellationToken ct)
        {
            Task? warmUp;
            lock (WarmUpLock)
                warmUp = _warmUpTask;

            if (warmUp == null || warmUp.IsCompletedSuccessfully)
                return;

            try
            {
                await warmUp.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // A canceled warm-up is never allowed to poison user work.
            }
            catch when (!ct.IsCancellationRequested)
            {
                // Warm-up is a latency optimization. Execute normally if the
                // read-only probe is unsupported by the current host state.
            }
        }

        private async Task RunWarmUpAsync(
            string probeDataset,
            Action<ToolExecutionProgressUpdate>? reporter,
            CancellationToken ct)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            reporter?.Invoke(new ToolExecutionProgressUpdate
            {
                Phase = "starting",
                Message = "正在预热 ArcGIS 地理处理引擎"
            });

            var values = await _dispatcher.RunAsync(
                () => Task.FromResult(
                    Geoprocessing.MakeValueArray(probeDataset)),
                ct);
            var result = await Geoprocessing.ExecuteToolAsync(
                "management.GetCount",
                values,
                null,
                ct,
                (eventName, payload) =>
                {
                    if (eventName == "OnBeginExecute")
                    {
                        reporter?.Invoke(new ToolExecutionProgressUpdate
                        {
                            Phase = "executing",
                            Message = "ArcGIS 地理处理引擎正在初始化"
                        });
                    }
                },
                InfrastructureFlags);

            stopwatch.Stop();
            reporter?.Invoke(new ToolExecutionProgressUpdate
            {
                Phase = result.IsFailed ? "failed" : "ready",
                Message = result.IsFailed
                    ? "ArcGIS 地理处理预热未完成，首次任务将按正常路径启动"
                    : $"ArcGIS 地理处理引擎已就绪（{stopwatch.Elapsed.TotalSeconds:F1} 秒）",
                Percent = result.IsFailed ? null : 100
            });
        }
    }
}
