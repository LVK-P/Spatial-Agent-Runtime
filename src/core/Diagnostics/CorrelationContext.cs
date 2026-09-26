using System;
using System.Threading;

// ================================================================
// CorrelationContext.cs — Cross-layer request tracing via AsyncLocal.
//
// Provides run_id (one per session/request) and span_id (one per
// logical operation: LLM call, tool execution, verification, synth)
// that propagate automatically through async call chains with zero
// method-signature changes.
//
// Usage:
//   using var run = CorrelationContext.BeginRun("pro-20260720-...");
//   using (var span = CorrelationContext.BeginSpan("llm_request"))
//   {
//       // all Transcript/Diagnostics written here auto-tag with
//       // run_id and span_id
//   }
//
// Thread-safe. AsyncLocal<T> preserves values across await boundaries.
// ================================================================
namespace SpatialAgent.Core
{
    public static class CorrelationContext
    {
        private static readonly AsyncLocal<string?> _runId = new();
        private static readonly AsyncLocal<string?> _spanId = new();
        private static readonly AsyncLocal<int> _spanDepth = new();      // nesting counter
        private static readonly AsyncLocal<int> _toolCallSeq = new();    // per-run incrementing counter

        /// <summary>Current run (session/request) identifier.</summary>
        public static string RunId => _runId.Value ?? "";

        /// <summary>Current span (operation) identifier.</summary>
        public static string SpanId => _spanId.Value ?? "";

        /// <summary>Nesting depth — 0 = no active span, 1 = top-level, etc.</summary>
        public static int SpanDepth => _spanDepth.Value;

        /// <summary>
        /// Begin a new run scope. If <paramref name="runId"/> is null,
        /// generates a ULID-style identifier.
        /// Disposing the returned token clears the run context.
        /// </summary>
        public static IDisposable BeginRun(string? runId = null)
        {
            var previous = _runId.Value;
            _runId.Value = runId ?? GenerateRunId();
            _toolCallSeq.Value = 0;
            return new RunScope(previous);
        }

        /// <summary>
        /// Begin a new span within the current run.
        /// Span IDs are hierarchical: parent.child.
        /// </summary>
        public static IDisposable BeginSpan(string operation)
        {
            var previousSpan = _spanId.Value;
            var previousDepth = _spanDepth.Value;

            string parent = string.IsNullOrEmpty(previousSpan) ? "root" : previousSpan;
            _spanId.Value = $"{parent}/{operation}";
            _spanDepth.Value = previousDepth + 1;

            return new SpanScope(previousSpan, previousDepth);
        }

        /// <summary>
        /// Allocate the next tool-call sequence number for this run.
        /// Returns (runId, spanId, seq) tuple for direct injection into log entries.
        /// </summary>
        public static (string runId, string spanId, int seq) NextToolCall(string toolName)
        {
            // AsyncLocal<T>.Value is a property, not a field — Interlocked not usable.
            // Thread safety is handled by the caller's run-scoped isolation.
            int current = _toolCallSeq.Value;
            int next = current + 1;
            _toolCallSeq.Value = next;
            string span = string.IsNullOrEmpty(_spanId.Value)
                ? $"tool/{toolName}#{next}"
                : $"{_spanId.Value}/tool/{toolName}#{next}";
            return (_runId.Value ?? "", span, next);
        }

        private static string GenerateRunId()
        {
            // ULID-inspired: timestamp + random, URL-safe
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var rand = Guid.NewGuid().ToString("N")[..10];
            return $"run-{now:x8}-{rand}";
        }

        private sealed class RunScope : IDisposable
        {
            private readonly string? _previous;
            public RunScope(string? previous) => _previous = previous;
            public void Dispose()
            {
                _runId.Value = _previous;
                _toolCallSeq.Value = 0;
            }
        }

        private sealed class SpanScope : IDisposable
        {
            private readonly string? _previousSpan;
            private readonly int _previousDepth;
            public SpanScope(string? previousSpan, int previousDepth)
            {
                _previousSpan = previousSpan;
                _previousDepth = previousDepth;
            }
            public void Dispose()
            {
                _spanId.Value = _previousSpan;
                _spanDepth.Value = _previousDepth;
            }
        }
    }
}
