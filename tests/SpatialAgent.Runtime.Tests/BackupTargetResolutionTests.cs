using FluentAssertions;
using NSubstitute;
using SpatialAgent.Runtime;
using SpatialAgent.Runtime.Contracts;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    public sealed class BackupTargetResolutionTests
    {
        private sealed class DataflowDescriptor : IToolDescriptor, IToolDataflowDescriptor
        {
            public string Name { get; init; } = "management_DangerousTool";
            public string Description => "";
            public string Category => "management";
            public ToolAnnotation Annotation => ToolAnnotation.Destructive;
            public IReadOnlyDictionary<string, ParameterProperty> Parameters { get; init; }
                = new Dictionary<string, ParameterProperty>(StringComparer.OrdinalIgnoreCase);
            public IReadOnlyList<string> RequiredParameters => InputParameters;
            public VerificationRule? Verification => null;
            public IReadOnlyDictionary<string, string>? DefaultParameters => null;
            public IReadOnlyList<string> InputParameters { get; set; } = Array.Empty<string>();
            public IReadOnlyList<string> OutputParameters => Array.Empty<string>();
            public string? PrimaryOutputParameter => null;
        }

        private sealed class PassingExecutor : IToolExecutor
        {
            public int Count { get; private set; }

            public Task<ToolResult> ExecuteAsync(
                ToolCall call,
                CancellationToken ct = default)
            {
                Count++;
                return Task.FromResult(new ToolResult
                {
                    ToolName = call.Name,
                    Success = true
                });
            }
        }

        [Fact]
        public async Task Configuration_string_is_never_selected_before_real_dataset()
        {
            var descriptor = Descriptor(
                ("overlay_type", "value", "String"),
                ("in_features", "input_dataset", "Feature Layer"));
            descriptor.InputParameters = new[] { "overlay_type", "in_features" };
            var (scheduler, workspace, executor) = Harness(descriptor);
            workspace.BackupAsync("ActualTarget", Arg.Any<string>())
                .Returns("G:/backup/ActualTarget");

            var result = await scheduler.RunAsync(Plan(new Dictionary<string, object?>
            {
                ["overlay_type"] = "Parcels",
                ["in_features"] = "ActualTarget"
            }));

            result.Should().ContainSingle();
            result[0].Status.Should().Be(StepResultStatus.Completed);
            executor.Count.Should().Be(1);
            await workspace.Received(1).BackupAsync("ActualTarget", descriptor.Name);
            await workspace.DidNotReceive().BackupAsync("Parcels", Arg.Any<string>());
        }

        [Fact]
        public async Task Multi_value_mutation_target_creates_one_backup_per_dataset()
        {
            var descriptor = Descriptor(
                ("in_features", "input_dataset", "Feature Layer"));
            descriptor.InputParameters = new[] { "in_features" };
            var (scheduler, workspace, _) = Harness(descriptor);
            workspace.BackupAsync(Arg.Any<string>(), Arg.Any<string>())
                .Returns(call => $"G:/backup/{call.ArgAt<string>(0)}");

            var result = await scheduler.RunAsync(Plan(new Dictionary<string, object?>
            {
                ["in_features"] = new[] { "LayerA", "LayerB", "LayerA" }
            }));

            result[0].Status.Should().Be(StepResultStatus.Completed);
            result[0].BackupPaths.Should().Equal(
                "G:/backup/LayerA",
                "G:/backup/LayerB");
            result[0].BackupPath.Should().Be("G:/backup/LayerA");
            await workspace.Received(1).BackupAsync("LayerA", descriptor.Name);
            await workspace.Received(1).BackupAsync("LayerB", descriptor.Name);
        }

        [Fact]
        public async Task Configuration_only_inputs_fail_closed_without_attempting_backup()
        {
            var descriptor = Descriptor(
                ("overlay_type", "value", "String"),
                ("where_clause", "where_clause", "SQL Expression"));
            descriptor.InputParameters = new[] { "overlay_type", "where_clause" };
            var (scheduler, workspace, executor) = Harness(descriptor);

            var result = await scheduler.RunAsync(Plan(new Dictionary<string, object?>
            {
                ["overlay_type"] = "INTERSECT",
                ["where_clause"] = "OBJECTID > 0"
            }));

            result[0].Status.Should().Be(StepResultStatus.Blocked);
            result[0].BlockReason.Should().Contain("无法识别");
            executor.Count.Should().Be(0);
            await workspace.DidNotReceive().BackupAsync(
                Arg.Any<string>(),
                Arg.Any<string>());
        }

        private static DataflowDescriptor Descriptor(
            params (string Name, string Role, string DataType)[] parameters) =>
            new()
            {
                Parameters = parameters.ToDictionary(
                    item => item.Name,
                    item => new ParameterProperty
                    {
                        SemanticRole = item.Role,
                        DataType = item.DataType
                    },
                    StringComparer.OrdinalIgnoreCase)
            };

        private static (Scheduler Scheduler, IWorkspaceService Workspace, PassingExecutor Executor)
            Harness(DataflowDescriptor descriptor)
        {
            var registry = new ToolRegistry();
            var executor = new PassingExecutor();
            registry.Register(descriptor, executor);

            var safety = Substitute.For<ISafetyGate>();
            safety.Classify(Arg.Any<string>()).Returns(ToolRiskLevel.Destructive);
            safety.Evaluate(Arg.Any<string>(), Arg.Any<AgentPermissionLevel>())
                .Returns(new SafetyDecision
                {
                    Kind = SafetyDecisionKind.Allow,
                    RiskLevel = ToolRiskLevel.Destructive
                });

            var workspace = Substitute.For<IWorkspaceService>();
            var scheduler = new Scheduler(registry, safety: safety)
            {
                CurrentPermissionLevel = AgentPermissionLevel.FullAccess
            };
            scheduler.SetWorkspace(workspace);
            return (scheduler, workspace, executor);
        }

        private static AgentPlan Plan(Dictionary<string, object?> arguments) => new()
        {
            Steps = new()
            {
                new PlanStep
                {
                    Id = 1,
                    ToolCall = new ToolCall
                    {
                        Id = "backup-test",
                        Name = "management_DangerousTool",
                        Arguments = arguments
                    }
                }
            }
        };
    }
}
