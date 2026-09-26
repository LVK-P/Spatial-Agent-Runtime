using FluentAssertions;
using SpatialAgent.Runtime;
using SpatialAgent.Runtime.Contracts;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    public class ObservationCompressionTests
    {
        [Fact]
        public void Compress_produces_short_digest_with_key_fields()
        {
            var compressor = new DefaultObservationCompressor();
            var step = new StepExecutionResult
            {
                StepId = 7,
                ToolName = "analysis_Buffer",
                Status = StepResultStatus.Completed,
                ToolResult = new ToolResult
                {
                    Success = true,
                    Output = new string('X', 600)
                }
            };

            var observation = new Observation
            {
                ToolName = "analysis_Buffer",
                Output = new ObservationOutput
                {
                    FeatureCount = 12,
                    GeometryType = "Polygon",
                    CRS = "4490",
                    Raw = new string('A', 600)
                }
            };

            var digest = compressor.Compress(step, observation, maxRawChars: 80);

            digest.ToolName.Should().Be("analysis_Buffer");
            digest.FeatureCount.Should().Be(12);
            digest.RawExcerpt.Should().NotBeNullOrWhiteSpace();
            digest.RawExcerpt!.Length.Should().BeLessThanOrEqualTo(80);
            compressor.Format(digest).Should().Contain("analysis_Buffer");
            compressor.Format(digest).Should().Contain("feature_count");
        }

        [Fact]
        public void Format_does_not_drop_fields_after_eighth_position()
        {
            var compressor = new DefaultObservationCompressor();
            var step = new StepExecutionResult
            {
                StepId = 1,
                ToolName = "agent_describe_layer",
                Status = StepResultStatus.Completed
            };
            var observation = new Observation
            {
                ToolName = "agent_describe_layer",
                Output = new ObservationOutput
                {
                    Fields = new()
                    {
                        "OBJECTID", "SHAPE", "NAME", "CODE",
                        "TYPE", "COUNT", "AREA", "LENGTH", "TQMJ"
                    }
                }
            };

            string formatted = compressor.Format(
                compressor.Compress(step, observation));

            formatted.Should().Contain("TQMJ");
        }
    }
}
