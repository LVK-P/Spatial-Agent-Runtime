using FluentAssertions;
using NSubstitute;
using SpatialAgent.Runtime;
using SpatialAgent.Runtime.Contracts;
using SpatialAgent.Services;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    public class SafetyArchitectureTests
    {
        [Fact]
        public void SafetyGate_prefers_runtime_safety_profile_over_prompt_annotation()
        {
            var registry = new ToolRegistry();
            registry.Register(new SafetyAwareDescriptor
            {
                Name = "custom_safe_tool",
                Annotation = ToolAnnotation.ReadOnly,
                SafetyProfile = new ToolSafetyProfile
                {
                    DeclaredRisk = ToolRiskLevel.Destructive,
                    MaxInternalRisk = ToolRiskLevel.Destructive,
                    Capabilities = ToolSafetyCapability.DeletesData
                }
            }, new NoOpExecutor());

            var gate = new SafetyGate();
            gate.SetToolRegistry(registry);

            gate.Classify("custom_safe_tool").Should().Be(ToolRiskLevel.Destructive);
        }

        [Fact]
        public async Task Scheduler_uses_runtime_safety_profile_for_influence_gate_instead_of_annotation()
        {
            var registry = new ToolRegistry();
            registry.Register(new SafetyAwareDescriptor
            {
                Name = "custom_batch_update",
                Annotation = ToolAnnotation.Increment,
                SafetyProfile = new ToolSafetyProfile
                {
                    DeclaredRisk = ToolRiskLevel.Modify,
                    MaxInternalRisk = ToolRiskLevel.Modify,
                    Capabilities = ToolSafetyCapability.ReadsData
                        | ToolSafetyCapability.MutatesExistingData
                        | ToolSafetyCapability.RequiresReliableScope
                }
            }, new NoOpExecutor());

            var safety = Substitute.For<ISafetyGate>();
            safety.Classify("custom_batch_update").Returns(ToolRiskLevel.Modify);
            safety.Evaluate("custom_batch_update", AgentPermissionLevel.RequestApproval)
                .Returns(new SafetyDecision { Kind = SafetyDecisionKind.Allow, RiskLevel = ToolRiskLevel.Modify });

            var influence = Substitute.For<IDataInfluenceEstimator>();
            influence.EstimateAsync(Arg.Any<ToolCall>(), Arg.Any<DataInfluenceContext?>())
                .Returns(Task.FromResult(new DataInfluenceEstimate
                {
                    LayerName = "large_layer",
                    FieldName = "TARGET",
                    FeatureCount = 500,
                    NonNullCount = 500,
                    IsReliable = true
                }));

            var scheduler = new Scheduler(registry, validators: null, safety: safety);
            scheduler.SetDataInfluenceEstimator(influence);
            scheduler.CurrentPermissionLevel = AgentPermissionLevel.RequestApproval;

            var results = await scheduler.RunAsync(new AgentPlan
            {
                Goal = "test",
                Steps = new List<PlanStep>
                {
                    new()
                    {
                        Id = 1,
                        Description = "custom_batch_update",
                        ToolCall = new ToolCall
                        {
                            Id = "call_1",
                            Name = "custom_batch_update",
                            Arguments = new Dictionary<string, object?>
                            {
                                ["in_table"] = "large_layer",
                                ["field_name"] = "TARGET"
                            }
                        }
                    }
                }
            });

            results.Should().HaveCount(1);
            results[0].Status.Should().Be(StepResultStatus.AwaitingApproval);
            results[0].ApprovalKind.Should().Be(ApprovalKind.DataInfluence);
        }

        private sealed class SafetyAwareDescriptor : IToolDescriptor, IToolSafetyDescriptor
        {
            public string Name { get; init; } = "";
            public string Description { get; init; } = "";
            public string Category { get; init; } = "";
            public ToolAnnotation Annotation { get; init; } = ToolAnnotation.Modify;
            public IReadOnlyDictionary<string, ParameterProperty> Parameters { get; init; }
                = new Dictionary<string, ParameterProperty>();
            public IReadOnlyList<string> RequiredParameters { get; init; } = Array.Empty<string>();
            public VerificationRule? Verification => null;
            public IReadOnlyDictionary<string, string>? DefaultParameters => null;
            public ToolSafetyProfile SafetyProfile { get; init; } = new();
        }

        private sealed class NoOpExecutor : IToolExecutor
        {
            public Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken ct = default)
                => Task.FromResult(new ToolResult { ToolName = call.Name, Success = true });
        }
    }
}
