using FluentAssertions;
using SpatialAgent.Runtime;
using SpatialAgent.Runtime.Contracts;
using SpatialAgent.Runtime.Planning;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    public sealed class GisPlanValidatorTests
    {
        private sealed class ProjectDescriptor : IToolDescriptor, IToolDataflowDescriptor
        {
            public string Name => "management_Project";
            public string Description => "Project";
            public string Category => "management";
            public ToolAnnotation Annotation => ToolAnnotation.Increment;
            public IReadOnlyDictionary<string, ParameterProperty> Parameters { get; }
                = new Dictionary<string, ParameterProperty>();
            public IReadOnlyList<string> RequiredParameters { get; }
                = new[] { "in_dataset", "out_dataset", "out_coor_system" };
            public VerificationRule? Verification => null;
            public IReadOnlyDictionary<string, string>? DefaultParameters => null;
            public IReadOnlyList<string> InputParameters { get; } = new[] { "in_dataset" };
            public IReadOnlyList<string> OutputParameters { get; } = new[] { "out_dataset" };
            public string? PrimaryOutputParameter => "out_dataset";
        }

        private sealed class PassingExecutor : IToolExecutor
        {
            public Task<ToolResult> ExecuteAsync(
                ToolCall call,
                CancellationToken ct = default) =>
                Task.FromResult(new ToolResult { ToolName = call.Name, Success = true });
        }

        [Fact]
        public void Duplicate_output_paths_are_blocking_case_insensitively()
        {
            var plan = new AgentPlan
            {
                Steps = new()
                {
                    Step(1, "analysis_Buffer", ("out_feature_class", "G:/work/result")),
                    Step(2, "analysis_Clip", ("out_feature_class", @"g:\work\result"))
                }
            };

            var result = new GisPlanValidator().Validate(plan, new DomainContext());

            result.IsValid.Should().BeFalse();
            result.Issues.Should().ContainSingle(issue =>
                issue.Severity == IssueSeverity.Error
                && issue.Description.Contains("输出路径重复"));
        }

        [Fact]
        public void Missing_and_forward_dependencies_are_blocking()
        {
            var plan = new AgentPlan
            {
                Steps = new()
                {
                    Step(1, "analysis_Buffer", dependencies: new[] { 2 }),
                    Step(2, "analysis_Clip", dependencies: new[] { 99 })
                }
            };

            var result = new GisPlanValidator().Validate(plan, new DomainContext());

            result.IsValid.Should().BeFalse();
            result.Issues.Should().Contain(issue => issue.Description.Contains("没有位于当前步骤之前"));
            result.Issues.Should().Contain(issue => issue.Description.Contains("依赖不存在"));
        }

        [Fact]
        public void Multiple_added_fields_are_tracked_per_layer()
        {
            var plan = new AgentPlan
            {
                Steps = new()
                {
                    Step(1, "management_AddField",
                        ("in_table", "Parcels"), ("field_name", "AREA")),
                    Step(2, "management_AddField",
                        ("in_table", "Parcels"), ("field_name", "CODE")),
                    Step(3, "management_CalculateField",
                        ("in_table", "Parcels"), ("field_name", "AREA"))
                }
            };

            var result = new GisPlanValidator().Validate(plan, new DomainContext());

            result.IsValid.Should().BeTrue();
            result.Issues.Should().BeEmpty();
        }

        [Fact]
        public void Shared_coordinate_system_is_not_a_duplicate_batch_output()
        {
            var tools = new ToolRegistry();
            tools.Register(new ProjectDescriptor(), new PassingExecutor());
            var plan = new AgentPlan
            {
                Steps = Enumerable.Range(1, 6)
                    .Select(index => Step(
                        index,
                        "management_Project",
                        ("in_dataset", $"layer_{index}"),
                        ("out_dataset", $"layer_{index}_4490"),
                        ("out_coor_system", 4490)))
                    .ToList()
            };

            var result = new GisPlanValidator(tools).Validate(
                plan,
                new DomainContext());

            result.IsValid.Should().BeTrue();
            result.Issues.Should().BeEmpty();
        }

        private static PlanStep Step(
            int id,
            string tool,
            params (string Key, object? Value)[] arguments) =>
            new()
            {
                Id = id,
                ToolCall = new ToolCall
                {
                    Name = tool,
                    Arguments = arguments.ToDictionary(item => item.Key, item => item.Value)
                }
            };

        private static PlanStep Step(
            int id,
            string tool,
            int[] dependencies) =>
            new()
            {
                Id = id,
                ToolCall = new ToolCall { Name = tool },
                DependsOn = dependencies.ToList()
            };
    }
}
