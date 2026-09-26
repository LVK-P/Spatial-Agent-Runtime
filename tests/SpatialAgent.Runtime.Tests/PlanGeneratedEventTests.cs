using FluentAssertions;
using Newtonsoft.Json.Linq;
using NSubstitute;
using SpatialAgent.Runtime;
using SpatialAgent.Runtime.Contracts;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    public class PlanGeneratedEventTests
    {
        [Fact]
        public async Task Plan_generated_event_contains_step_intents_and_max_risk()
        {
            var session = Substitute.For<IAgentSession>();
            session.ThreadId.Returns("test-thread-plan");
            session.Messages.Returns(new List<Message>());
            session.PermissionLevel.Returns(AgentPermissionLevel.RequestApproval);
            session.TokenUsage.Returns(new TokenUsage());
            session.Domain.Returns(new DomainContext());
            session.Observations.Returns(new List<IObservation>());
            session.Validations.Returns(new List<ValidationResult>());
            session.Status.Returns(SessionStatus.Idle);

            var llm = Substitute.For<ILLMProvider>();
            llm.ModelName.Returns("test-model");
            llm.ThinkingEffort.Returns("");
            llm.ProviderName.Returns("test-provider");
            llm.IsConfigured.Returns(true);
            llm.Capability.Returns(new LLMCapability { ToolCalling = true, StructuredOutput = true });
            llm.ChatAsync(
                    Arg.Any<IReadOnlyList<Message>>(),
                    Arg.Any<string>(),
                    Arg.Any<IReadOnlyList<IToolDescriptor>?>(),
                    Arg.Any<double>(),
                    Arg.Any<int>())
                .Returns(new LLMResponse
                {
                    Success = true,
                    ToolCalls = new List<ToolCall>
                    {
                        new() { Id = "call_1", Name = "analysis_Buffer", Arguments = new() },
                        new() { Id = "call_2", Name = "management_DeleteField", Arguments = new() }
                    }
                });

            var tools = new ToolRegistry();
            tools.Register(
                new StubDescriptor { Name = "analysis_Buffer", Annotation = ToolAnnotation.Modify },
                new StubExecutor(new ToolResult { ToolName = "analysis_Buffer", Success = true }));
            tools.Register(
                new StubDescriptor { Name = "management_DeleteField", Annotation = ToolAnnotation.Destructive },
                new StubExecutor(new ToolResult { ToolName = "management_DeleteField", Success = true }));

            var projectReader = Substitute.For<IProjectReader>();
            projectReader.GetContextForAIAsync().Returns("项目：测试\n项目目录：G:\\test");
            projectReader.GetProjectInfoAsync().Returns("{}");

            var mapReader = Substitute.For<IMapReader>();
            mapReader.GetMapInfoAsync().Returns("{}");
            mapReader.GetAllLayerSummariesAsync().Returns("");
            mapReader.GetSpatialRefContextAsync().Returns(new SpatialRefContext());

            var skillProvider = Substitute.For<ISkillProvider>();
            skillProvider.Count.Returns(0);
            skillProvider.GetSkillsSummary().Returns("");
            skillProvider.MatchSkills(Arg.Any<string>()).Returns(new List<MatchedSkill>());
            skillProvider.MatchSkillsAsync(Arg.Any<string>()).Returns(Task.FromResult(new List<MatchedSkill>()));

            var context = new ContextManager(projectReader, mapReader, skillProvider, knowledge: null);
            var bus = new EventBus();
            var seen = new List<AgentEvent>();
            bus.Subscribe("plan.generated", e => seen.Add(e));

            var runtime = new AgentRuntime(session, llm, tools, context, bus);
            runtime.SetSafety(CreateSafetyGate(tools));
            runtime.SetPlanningServices(
                coverageValidator: new SyntheticPlanCoverageValidator());

            var response = await runtime.ProcessAsync(new TaskRequest { UserMessage = "先缓冲再删字段" });

            response.RequiresApproval.Should().BeTrue();
            seen.Should().ContainSingle();
            seen[0].Metadata.Should().ContainKey("step_count").WhoseValue.Should().Be("2");
            seen[0].Metadata.Should().ContainKey("risk_max").WhoseValue.Should().Be(ToolRiskLevel.Destructive.ToString());

            var steps = JArray.FromObject(seen[0].Data!["steps"]!);
            steps.Should().HaveCount(2);
            steps[0]!["intent"]!.ToString().Should().Be("创建缓冲区");
            steps[1]!["intent"]!.ToString().Should().Be("DeleteField");
        }

        private static ISafetyGate CreateSafetyGate(ToolRegistry tools)
        {
            var safety = Substitute.For<ISafetyGate>();
            safety.Classify(Arg.Any<string>()).Returns(ci =>
                tools.GetDescriptor(ci.Arg<string>())?.Annotation switch
                {
                    ToolAnnotation.ReadOnly => ToolRiskLevel.ReadOnly,
                    ToolAnnotation.Destructive => ToolRiskLevel.Destructive,
                    _ => ToolRiskLevel.Modify
                });
            safety.Evaluate(Arg.Any<string>(), Arg.Any<AgentPermissionLevel>())
                .Returns(ci =>
                {
                    var tool = ci.Arg<string>();
                    var perm = ci.Arg<AgentPermissionLevel>();
                    var risk = tools.GetDescriptor(tool)?.Annotation switch
                    {
                        ToolAnnotation.ReadOnly => ToolRiskLevel.ReadOnly,
                        ToolAnnotation.Destructive => ToolRiskLevel.Destructive,
                        _ => ToolRiskLevel.Modify
                    };
                    if (perm == AgentPermissionLevel.ReadOnly && risk != ToolRiskLevel.ReadOnly)
                        return new SafetyDecision { Kind = SafetyDecisionKind.Block, RiskLevel = risk, ToolName = tool, Reason = "ReadOnly mode" };
                    if (perm == AgentPermissionLevel.RequestApproval && risk == ToolRiskLevel.Destructive)
                        return new SafetyDecision { Kind = SafetyDecisionKind.RequireApproval, RiskLevel = risk, ToolName = tool, Reason = "Needs approval" };
                    return new SafetyDecision { Kind = SafetyDecisionKind.Allow, RiskLevel = risk, ToolName = tool };
                });
            return safety;
        }

        private sealed class StubDescriptor : IToolDescriptor
        {
            public string Name { get; init; } = "";
            public string Description { get; init; } = "";
            public string Category { get; init; } = "";
            public ToolAnnotation Annotation { get; init; }
            public IReadOnlyDictionary<string, ParameterProperty> Parameters { get; init; }
                = new Dictionary<string, ParameterProperty>();
            public IReadOnlyList<string> RequiredParameters { get; init; } = new List<string>();
            public VerificationRule? Verification { get; init; }
            public IReadOnlyDictionary<string, string>? DefaultParameters => null;
        }

        private sealed class StubExecutor : IToolExecutor
        {
            private readonly ToolResult _result;

            public StubExecutor(ToolResult result)
            {
                _result = result;
            }

            public Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken ct = default)
                => Task.FromResult(_result);
        }
    }
}
