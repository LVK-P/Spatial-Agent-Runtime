using FluentAssertions;
using SpatialAgent.Tools.Core;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    public sealed class JsonToolResultFactoryTests
    {
        [Fact]
        public void Error_envelope_is_a_failed_tool_result()
        {
            var result = JsonToolResultFactory.FromJson(
                "agent_describe_layer",
                """{"error":"图层不存在"}""");

            result.Success.Should().BeFalse();
            result.Error.Should().Be("图层不存在");
            result.Output.Should().Contain("\"error\"");
        }

        [Fact]
        public void Ordinary_json_remains_successful()
        {
            var result = JsonToolResultFactory.FromJson(
                "agent_describe_layer",
                """{"name":"乡镇边界","feature_count":12}""");

            result.Success.Should().BeTrue();
            result.Error.Should().BeNull();
        }
    }
}
