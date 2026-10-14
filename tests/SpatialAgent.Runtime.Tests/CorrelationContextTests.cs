using FluentAssertions;
using SpatialAgent.Runtime;
using SpatialAgent.Core;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    /// <summary>
    /// CorrelationContext unit tests — AsyncLocal propagation, nesting,
    /// thread isolation, and scope disposal correctness.
    /// </summary>
    /// <remarks>中文：CorrelationContext单元测试——异步本地传播、嵌套、线程隔离和作用域处置正确性。</remarks>
    public class CorrelationContextTests
    {
        [Fact]
        public void BeginRun_sets_run_id_and_resets_tool_seq()
        {
            using (CorrelationContext.BeginRun("test-run-001"))
            {
                CorrelationContext.RunId.Should().Be("test-run-001");
                CorrelationContext.SpanId.Should().BeEmpty();
            }
            // 作用域退出后清空
            // English：After exiting the scope, clear it
            CorrelationContext.RunId.Should().BeEmpty();
        }

        [Fact]
        public void BeginRun_generates_id_when_null()
        {
            using (CorrelationContext.BeginRun(null))
            {
                CorrelationContext.RunId.Should().StartWith("run-");
                CorrelationContext.RunId.Length.Should().BeGreaterThan(10);
            }
        }

        [Fact]
        public void BeginSpan_creates_hierarchical_span_id()
        {
            using (CorrelationContext.BeginRun("r1"))
            {
                using (CorrelationContext.BeginSpan("llm_request"))
                {
                    CorrelationContext.SpanId.Should().Be("root/llm_request");
                    CorrelationContext.SpanDepth.Should().Be(1);

                    using (CorrelationContext.BeginSpan("tool_exec"))
                    {
                        CorrelationContext.SpanId.Should().Be("root/llm_request/tool_exec");
                        CorrelationContext.SpanDepth.Should().Be(2);
                    }

                    // 内层退出后恢复外层
                    // English：After the inner layer is removed, the outer layer is restored
                    CorrelationContext.SpanId.Should().Be("root/llm_request");
                    CorrelationContext.SpanDepth.Should().Be(1);
                }

                CorrelationContext.SpanId.Should().BeEmpty();
                CorrelationContext.SpanDepth.Should().Be(0);
            }
        }

        [Fact]
        public void NextToolCall_increments_sequence()
        {
            using (CorrelationContext.BeginRun("r1"))
            {
                using (CorrelationContext.BeginSpan("plan"))
                {
                    var (run1, span1, seq1) = CorrelationContext.NextToolCall("Buffer");
                    seq1.Should().Be(1);
                    span1.Should().Contain("Buffer#1");

                    var (run2, span2, seq2) = CorrelationContext.NextToolCall("Clip");
                    seq2.Should().Be(2);
                    span2.Should().Contain("Clip#2");

                    run1.Should().Be("r1");
                    run2.Should().Be("r1");
                }
            }
        }

        [Fact]
        public void Nested_runs_dont_interfere()
        {
            using (CorrelationContext.BeginRun("outer"))
            {
                CorrelationContext.RunId.Should().Be("outer");

                using (CorrelationContext.BeginRun("inner"))
                {
                    CorrelationContext.RunId.Should().Be("inner");
                }

                CorrelationContext.RunId.Should().Be("outer");
            }

            CorrelationContext.RunId.Should().BeEmpty();
        }

        [Fact]
        public async Task AsyncLocal_preserves_across_await()
        {
            using (CorrelationContext.BeginRun("async-test"))
            {
                CorrelationContext.RunId.Should().Be("async-test");

                await Task.Delay(10);

                // AsyncLocal<T> 应在 await 后保持
                CorrelationContext.RunId.Should().Be("async-test");
            }
        }
    }
}
