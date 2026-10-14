using FluentAssertions;
using SpatialAgent.Runtime;
using SpatialAgent.Runtime.Contracts;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    public class RuntimePolicyTests
    {
        [Fact]
        public void AlwaysOnToolNames_is_a_small_read_only_probe_kernel()
        {
            var names = RuntimePolicy.AlwaysOnToolNames;
            names.Should().Contain("agent_get_map_info");
            names.Should().Contain("agent_list_layers");
            names.Should().Contain("cc_read_rows");
            names.Should().NotContain("analysis_Buffer");
            names.Should().NotContain("management_AddField");
            names.Count.Should().BeLessOrEqualTo(8);
        }

        [Fact]
        public void MaxReactRounds_is_positive()
        {
            RuntimePolicy.MaxReactRounds.Should().BeGreaterOrEqualTo(1);
        }

        [Fact]
        public void SelectExposedTools_merges_always_on_with_relevant()
        {
            var all = new List<IToolDescriptor>
            {
                StubTool("agent_get_map_info"),
                StubTool("analysis_Buffer"),
                StubTool("conversion_FeatureClassToShapefile"),
                StubTool("management_CalculateField"),
            };

            var result = RuntimePolicy.SelectExposedTools(all, new[] { "conversion_FeatureClassToShapefile" });

            result.Should().Contain(t => t.Name == "conversion_FeatureClassToShapefile");
            result.Should().Contain(t => t.Name == "agent_get_map_info");   // 始终启用。 / Always on.
            result.Should().NotContain(t => t.Name == "analysis_Buffer");
            result.Should().NotContain(t => t.Name == "management_CalculateField");
        }

        [Fact]
        public void Tiered_exposure_enforces_full_and_lightweight_budgets()
        {
            var all = Enumerable.Range(0, 50)
                .Select(index => StubTool($"tool_{index:D2}"))
                .Concat(RuntimePolicy.AlwaysOnToolNames.Select(StubTool))
                .ToList();
            var required = Enumerable.Range(0, 15)
                .Select(index => $"tool_{index:D2}")
                .ToList();
            var relevant = Enumerable.Range(0, 50)
                .Select(index => $"tool_{index:D2}")
                .ToList();

            var (tier1, tier2) = RuntimePolicy.SelectExposedToolsTiered(
                all,
                relevant,
                required);

            tier1.Count.Should().BeLessOrEqualTo(
                RuntimePolicy.AlwaysOnToolNames.Count + 8);
            tier2.Count.Should().BeLessOrEqualTo(RuntimePolicy.MaxTier2Tools);
            tier1.Select(tool => tool.Name).Should().Contain("tool_00");
            tier2.Should().OnlyContain(tool => !all.Contains(tool));
        }

        [Fact]
        public void BuildContinuationPrompt_contains_round_info()
        {
            var steps = new List<StepExecutionResult>
            {
                new() { ToolName = "analysis_Buffer", Status = StepResultStatus.Completed }
            };
            var blocked = new HashSet<string>();

            var prompt = RuntimePolicy.BuildContinuationPrompt(0, steps, blocked, "test query");

            prompt.Should().Contain("第1/");
            prompt.Should().Contain("test query");
            prompt.Should().Contain("✅");
        }

        [Fact]
        public void BuildContinuationPrompt_warns_on_blocked_tools()
        {
            var steps = new List<StepExecutionResult>();
            var blocked = new HashSet<string> { "management_DeleteField" };

            var prompt = RuntimePolicy.BuildContinuationPrompt(0, steps, blocked, "query");

            prompt.Should().Contain("management_DeleteField");
            prompt.Should().Contain("已被安全策略阻止");
        }

        [Theory]
        [InlineData(StepResultStatus.Completed, "✅")]
        [InlineData(StepResultStatus.Failed, "❌")]
        [InlineData(StepResultStatus.AwaitingApproval, "⏸️")]
        [InlineData(StepResultStatus.Blocked, "🚫")]
        public void StepStatusText_returns_emoji_for_each_status(StepResultStatus status, string expectedEmoji)
        {
            RuntimePolicy.StepStatusText(status).Should().Be(expectedEmoji);
        }

        private static IToolDescriptor StubTool(string name) => new StubToolDescriptor(name);

        private class StubToolDescriptor : IToolDescriptor
        {
            public string Name { get; }
            public string Description => "";
            public string Category => "";
            public ToolAnnotation Annotation => ToolAnnotation.Modify;
            public IReadOnlyDictionary<string, ParameterProperty> Parameters =>
                new Dictionary<string, ParameterProperty>();
            public IReadOnlyList<string> RequiredParameters => Array.Empty<string>();
            public VerificationRule? Verification => null;
            public IReadOnlyDictionary<string, string>? DefaultParameters => null;
            public StubToolDescriptor(string name) => Name = name;
        }
    }
}
