using FluentAssertions;
using SpatialAgent.Runtime;
using SpatialAgent.Core;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    public class ToolNameResolverTests
    {
        private readonly ToolNameResolver _resolver = new();

        public ToolNameResolverTests()
        {
            _resolver.Register("analysis_Buffer");
            _resolver.Register("management_AddField");
            _resolver.Register("management_SelectLayerByAttribute");
            _resolver.Register("analysis_Clip");
        }

        [Fact]
        public void Register_creates_all_three_forms()
        {
            _resolver.ToDotName("analysis_Buffer").Should().Be("analysis.Buffer");
            _resolver.ToShortName("analysis_Buffer").Should().Be("Buffer");
            _resolver.ToSchemaName("analysis.Buffer").Should().Be("analysis_Buffer");
        }

        [Fact]
        public void Resolve_from_dot_form_returns_schema_name()
        {
            _resolver.Resolve("analysis.Buffer").Should().Be("analysis_Buffer");
        }

        [Fact]
        public void Resolve_from_short_name_returns_first_registered()
        {
            _resolver.Resolve("Buffer").Should().Be("analysis_Buffer");
        }

        [Fact]
        public void Resolve_unknown_name_returns_input()
        {
            _resolver.Resolve("nonexistent_Tool").Should().Be("nonexistent_Tool");
        }

        [Fact]
        public void IsRegistered_detects_valid_tools()
        {
            _resolver.IsRegistered("analysis_Buffer").Should().BeTrue();
            _resolver.IsRegistered("analysis.Buffer").Should().BeTrue();
            _resolver.IsRegistered("Buffer").Should().BeTrue();
            _resolver.IsRegistered("nonexistent").Should().BeFalse();
        }

        [Fact]
        public void Short_name_collision_preserves_first_registered()
        {
            // "analysis_Clip" was registered second — short name "Clip" maps to "analysis_Buffer" only
            // This tests that short name collisions don't overwrite
            // 中文：“analysis_Clip”是第二个注册的——短名“Clip”只映射到“analysis_Buffer”。这测试了短名称碰撞不会覆盖
            _resolver.Resolve("Clip").Should().Be("analysis_Clip");
        }

        [Fact]
        public void ToDotName_handles_multi_underscore_names()
        {
            _resolver.ToDotName("management_SelectLayerByAttribute")
                .Should().Be("management.SelectLayerByAttribute");
        }

        [Theory]
        [InlineData("analysis_Buffer", "analysis.Buffer", "Buffer", "analysis")]
        [InlineData("management_AddField", "management.AddField", "AddField", "management")]
        public void All_three_forms_are_consistent(string schema, string dot, string shortName, string toolbox)
        {
            _resolver.ToDotName(schema).Should().Be(dot);
            _resolver.ToShortName(schema).Should().Be(shortName);
            _resolver.GetToolbox(schema).Should().Be(toolbox);
            _resolver.ToSchemaName(dot).Should().Be(schema);
            _resolver.IsRegistered(shortName).Should().BeTrue();
        }

        [Fact]
        public void NormalizeSchemaName_replaces_dots()
        {
            ToolNameResolver.NormalizeSchemaName("analysis.Buffer").Should().Be("analysis_Buffer");
        }

        [Fact]
        public void ExtractShortName_handles_dot_and_underscore()
        {
            ToolNameResolver.ExtractShortName("management_AddField").Should().Be("AddField");
            ToolNameResolver.ExtractShortName("management.AddField").Should().Be("AddField");
        }

        [Fact]
        public void RegisterAll_registers_multiple_at_once()
        {
            var r = new ToolNameResolver();
            r.RegisterAll("analysis_Intersect", "analysis_Union");
            r.IsRegistered("Intersect").Should().BeTrue();
            r.IsRegistered("Union").Should().BeTrue();
        }
    }
}
