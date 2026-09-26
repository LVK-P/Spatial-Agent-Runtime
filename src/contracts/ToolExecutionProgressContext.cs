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
