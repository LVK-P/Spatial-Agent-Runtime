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
// 中文：================================================================ CorrelationContext.cs — 通过 AsyncLocal 实现跨层请求追踪。提供一个run_id（每个会话/请求一个）和span_id（每个逻辑操作一个：LLM调用、工具执行、验证、合成器），这些调用自动通过异步调用链传播，且方法签名无更改。使用方法：使用 var run = CorrelationContext.BeginRun（“pro-20260720-...”）;使用 （var span = CorrelationContext.BeginSpan（“llm_request”）） { // 所有 Transcript/Diagnostics 在此编写，自动标记 // run_id 和 span_id } 线程安全。AsyncLocal 会在 wait 边界之间保留值。================================================================
namespace SpatialAgent.Core
{
    public static class CorrelationContext
    {
        private static readonly AsyncLocal<string?> _runId = new();
        private static readonly AsyncLocal<string?> _spanId = new();
        private static readonly AsyncLocal<int> _spanDepth = new();      // 嵌套计数器。 / Nesting counter.
        private static readonly AsyncLocal<int> _toolCallSeq = new();    // 每次运行递增的计数器。 / Per-run incrementing counter.

        /// <summary>Current run (session/request) identifier.</summary>
        /// <remarks>中文：当前运行（会话/请求）标识符。</remarks>
        public static string RunId => _runId.Value ?? "";

        /// <summary>Current span (operation) identifier.</summary>
        /// <remarks>中文：当前范围（操作）标识符。</remarks>
        public static string SpanId => _spanId.Value ?? "";

        /// <summary>Nesting depth — 0 = no active span, 1 = top-level, etc.</summary>
        /// <remarks>中文：嵌套深度 — 0 = 无活动跨度，1 = 顶层，依此类推。</remarks>
        public static int SpanDepth => _spanDepth.Value;

        /// <summary>
        /// Begin a new run scope. If <paramref name="runId"/> is null,
        /// generates a ULID-style identifier.
        /// Disposing the returned token clears the run context.
        /// </summary>
        /// <remarks>中文：开始新的运行范围。如果 为空，则生成 ULID 风格的标识符。丢弃返回的令牌会清除运行上下文。</remarks>
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
        /// <remarks>中文：在当前运行中开始新的一段。跨 ID 是层级的：parent.child。</remarks>
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
        /// <remarks>中文：为本次运行分配下一个工具调用序列号。返回（runId， spanId， seq）元组，用于直接注入日志。</remarks>
        public static (string runId, string spanId, int seq) NextToolCall(string toolName)
        {
            // AsyncLocal<T>.Value is a property, not a field — Interlocked not usable.
            // Thread safety is handled by the caller's run-scoped isolation.
            // 中文：AsyncLocal 的 Value 是属性而非字段，无法使用 Interlocked。线程安全由调用方隔离各运行范围来保证。
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
            // 中文：受ULID启发：时间戳+随机，URL安全
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
