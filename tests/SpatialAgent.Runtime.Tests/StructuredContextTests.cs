using FluentAssertions;
using NSubstitute;
using SpatialAgent.Core;
using SpatialAgent.Runtime;
using SpatialAgent.Runtime.Contracts;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    public class StructuredContextTests
    {
        [Fact]
        public async Task Gather_uses_typed_map_name_and_layers_not_summary_first_line()
        {
            var project = Substitute.For<IProjectReader>();
            project.GetContextForAIAsync().Returns("Project: Demo");
            var map = Substitute.For<IMapReader>();
            map.GetAllLayerSummariesAsync().Returns("• roads (Polyline)");
            map.GetMapContextAsync().Returns(new MapContextSnapshot
            {
                IsAvailable = true,
                MapName = "Operational Map",
                Layers = new[]
                {
                    new LayerInfo
                    {
                        Name = "roads",
                        GeometryType = "Polyline",
                        FeatureCount = 9
                    }
                }
            });
            map.GetProjectExtentAsync().Returns("");
            map.GetSpatialRefContextAsync().Returns(new SpatialRefContext());
            map.GetMapInfoAsync().Returns("{}");
            map.ListLayersAsync().Returns("{}");
            var skills = Substitute.For<ISkillProvider>();
            skills.GetSkillsSummary().Returns("");
            skills.MatchSkillsAsync(Arg.Any<string>())
                .Returns(Task.FromResult(new List<MatchedSkill>()));

            var manager = new ContextManager(project, map, skills);
            AgentContext context = await manager.GatherAsync("inspect");

            context.MapName.Should().Be("Operational Map");
            context.Layers.Should().ContainSingle();
            context.Layers[0].FeatureCount.Should().Be(9);
        }
    }
}
