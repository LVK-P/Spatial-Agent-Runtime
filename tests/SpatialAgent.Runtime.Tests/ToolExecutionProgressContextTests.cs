using FluentAssertions;
using SpatialAgent.Runtime.Contracts;
using Xunit;

namespace SpatialAgent.Runtime.Tests;

public sealed class ToolExecutionProgressContextTests
{
    [Fact]
    public void Begin_routes_updates_and_restores_outer_reporter()
    {
        var outer = new List<string>();
        var inner = new List<string>();

        using (ToolExecutionProgressContext.Begin(
                   update => outer.Add(update.Phase)))
        {
            ToolExecutionProgressContext.Report("outer", "outer");
            using (ToolExecutionProgressContext.Begin(
                       update => inner.Add(update.Phase)))
            {
                ToolExecutionProgressContext.Report("inner", "inner", 50);
            }

            ToolExecutionProgressContext.Report("outer_again", "outer");
        }

        outer.Should().Equal("outer", "outer_again");
        inner.Should().Equal("inner");
        ToolExecutionProgressContext.CaptureReporter().Should().BeNull();
    }

    [Fact]
    public async Task Reporter_flows_across_async_boundaries()
    {
        ToolExecutionProgressUpdate? observed = null;
        using (ToolExecutionProgressContext.Begin(update => observed = update))
        {
            await Task.Yield();
            ToolExecutionProgressContext.Report("gp_execute", "working", 25);
        }

        observed.Should().NotBeNull();
        observed!.Phase.Should().Be("gp_execute");
        observed.Percent.Should().Be(25);
    }
}
