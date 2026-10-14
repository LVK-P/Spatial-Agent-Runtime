using System;
using System.Threading;

namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Provider-neutral progress emitted by a tool adapter while a logical
    /// scheduler step is running. Low-level composite operations inherit the
    /// ambient reporter through async execution context without depending on
    /// Runtime or UI assemblies.
    /// </summary>
    /// <remarks>中文：逻辑 Scheduler 步骤运行期间由工具适配器发出的提供程序中立进度。底层复合操作通过异步执行上下文继承环境中的报告器，无需依赖 Runtime 或 UI 程序集。</remarks>
    public sealed class ToolExecutionProgressUpdate
    {
        public string Phase { get; init; } = "execute";
        public string Message { get; init; } = "";
        public int? Percent { get; init; }
    }

    public static class ToolExecutionProgressContext
    {
        private static readonly AsyncLocal<Action<ToolExecutionProgressUpdate>?>
            CurrentReporter = new();

        public static IDisposable Begin(
            Action<ToolExecutionProgressUpdate> reporter)
        {
            ArgumentNullException.ThrowIfNull(reporter);
            Action<ToolExecutionProgressUpdate>? previous =
                CurrentReporter.Value;
            CurrentReporter.Value = reporter;
            return new Scope(previous);
        }

        /// <summary>
        /// Captures the logical-step reporter before entering an SDK callback.
        /// Some native callbacks do not preserve AsyncLocal state themselves.
        /// </summary>
        /// <remarks>中文：进入 SDK 回调前捕获逻辑步骤报告器；某些原生回调不会自行保留 AsyncLocal 状态。</remarks>
        public static Action<ToolExecutionProgressUpdate>? CaptureReporter() =>
            CurrentReporter.Value;

        public static void Report(
            string phase,
            string message,
            int? percent = null)
        {
            CurrentReporter.Value?.Invoke(new ToolExecutionProgressUpdate
            {
                Phase = phase,
                Message = message,
                Percent = percent
            });
        }

        private sealed class Scope : IDisposable
        {
            private readonly Action<ToolExecutionProgressUpdate>? _previous;
            private int _disposed;

            public Scope(Action<ToolExecutionProgressUpdate>? previous) =>
                _previous = previous;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    CurrentReporter.Value = _previous;
            }
        }
    }
}
