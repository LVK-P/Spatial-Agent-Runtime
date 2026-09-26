using FluentAssertions;
using NSubstitute;
using SpatialAgent.Runtime;
using SpatialAgent.Runtime.Contracts;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    public sealed class DataRevisionApprovalTests
    {
        private sealed class Descriptor : IToolDescriptor
        {
            public string Name => "management_DeleteRows";
            public string Description => "delete rows";
            public string Category => "management";
            public ToolAnnotation Annotation => ToolAnnotation.Destructive;
            public IReadOnlyDictionary<string, ParameterProperty> Parameters { get; }
                = new Dictionary<string, ParameterProperty>
                {
                    ["in_table"] = new()
                    {
                        SemanticRole = "input_dataset",
                        DataType = "Table View"
                    }
                };
            public IReadOnlyList<string> RequiredParameters => new[] { "in_table" };
            public VerificationRule? Verification => null;
            public IReadOnlyDictionary<string, string>? DefaultParameters => null;
        }

        private sealed class CountingExecutor : IToolExecutor
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
        public async Task Selection_identity_change_invalidates_approval_before_execution()
        {
            string selectionState =
                """{"total":1,"selections":[{"layer":"Parcels","count":1,"oid_digest":"aaa"}]}""";
            var session = Session();
            var llm = Llm(new LLMResponse
            {
                Success = true,
                ToolCalls = new()
                {
                    new ToolCall
                    {
                        Id = "delete-call",
                        Name = "management_DeleteRows",
                        Arguments = new() { ["in_table"] = "Parcels" }
                    }
                }
            });
            var projectReader = Substitute.For<IProjectReader>();
            projectReader.GetContextForAIAsync()
                .Returns("项目：Test\n家目录：G:\\Test\n默认数据库路径：G:\\Test\\data.gdb");
            projectReader.GetProjectInfoAsync().Returns("{}");
            var mapReader = Substitute.For<IMapReader>();
            mapReader.GetMapInfoAsync()
                .Returns("""{"name":"Map","wkid":"4490"}""");
            mapReader.GetAllLayerSummariesAsync()
                .Returns("· Parcels：Polygon，10 个要素，字段：OBJECTID、NAME");
            mapReader.ListLayersAsync()
                .Returns("""{"count":1,"layers":[{"name":"Parcels","type":"FeatureLayer"}]}""");
            mapReader.GetSelectionAsync().Returns(_ => selectionState);
            mapReader.GetProjectExtentAsync().Returns("{}");
            mapReader.GetSpatialRefContextAsync().Returns(new SpatialRefContext());
            var skills = Substitute.For<ISkillProvider>();
            skills.GetSkillsSummary().Returns("");
            skills.MatchSkillsAsync(Arg.Any<string>())
                .Returns(Task.FromResult(new List<MatchedSkill>()));

            var tools = new ToolRegistry();
            var executor = new CountingExecutor();
            tools.Register(new Descriptor(), executor);
            var bus = new EventBus();
            var invalidations = new List<AgentEvent>();
            bus.Subscribe("safety.approval_invalidated", invalidations.Add);
            var runtime = new AgentRuntime(
                session,
                llm,
                tools,
                new ContextManager(projectReader, mapReader, skills),
                bus);
            runtime.SetSafety(Safety());
            runtime.SetPlanningServices(
                coverageValidator: new SyntheticPlanCoverageValidator());

            var pending = await runtime.ProcessAsync(new TaskRequest
            {
                UserMessage = "删除选择的记录"
            });

            pending.RequiresApproval.Should().BeTrue();
            pending.PendingApproval.Should().NotBeNull();
            pending.PendingApproval!.DataRevisionFingerprint.Should().NotBeNullOrWhiteSpace();
            session.Plan.Should().NotBeNull();
            session.Plan!.DataRevision.Should().NotBeNull();

            selectionState =
                """{"total":1,"selections":[{"layer":"Parcels","count":1,"oid_digest":"bbb"}]}""";
            var resumed = await runtime.ProcessAsync(new TaskRequest
            {
                UserMessage = "删除选择的记录",
                AppendUserMessage = false,
                IsApprovalResumption = true,
                Approved = true,
                ApprovalDecision = new ApprovalDecision
                {
                    TicketId = pending.PendingApproval.Id,
                    Decision = ApprovalDecisionKind.Approve,
                    Scope = pending.PendingApproval.Scope
                }
            });

            resumed.StopReason.Should().Be("ApprovalInvalidated");
            resumed.UserReply.Should().Contain("选择集");
            resumed.CanContinue.Should().BeTrue();
            executor.Count.Should().Be(0);
            invalidations.Should().ContainSingle();
            session.Plan.Should().BeNull();
        }

        private static IAgentSession Session()
        {
            var session = Substitute.For<IAgentSession>();
            session.ThreadId.Returns("revision-test");
            session.Messages.Returns(new List<Message>());
            session.PermissionLevel.Returns(AgentPermissionLevel.RequestApproval);
            session.TokenUsage.Returns(new TokenUsage());
            session.Domain.Returns(new DomainContext());
            session.Observations.Returns(new List<IObservation>());
            session.Validations.Returns(new List<ValidationResult>());
            session.Status.Returns(SessionStatus.Idle);
            return session;
        }

        private static ILLMProvider Llm(LLMResponse response)
        {
            var llm = Substitute.For<ILLMProvider>();
            llm.ModelName.Returns("test-model");
            llm.ThinkingEffort.Returns("");
            llm.ProviderName.Returns("test");
            llm.IsConfigured.Returns(true);
            llm.Capability.Returns(new LLMCapability { ToolCalling = true });
            llm.ChatAsync(
                    Arg.Any<IReadOnlyList<Message>>(),
                    Arg.Any<string>(),
                    Arg.Any<IReadOnlyList<IToolDescriptor>?>(),
                    Arg.Any<double>(),
                    Arg.Any<int>())
                .Returns(response);
            return llm;
        }

        private static ISafetyGate Safety()
        {
            var safety = Substitute.For<ISafetyGate>();
            safety.Classify(Arg.Any<string>())
                .Returns(ToolRiskLevel.Destructive);
            safety.Evaluate(
                    Arg.Any<string>(),
                    AgentPermissionLevel.RequestApproval)
                .Returns(new SafetyDecision
                {
                    Kind = SafetyDecisionKind.RequireApproval,
                    RiskLevel = ToolRiskLevel.Destructive,
                    Reason = "delete approval"
                });
            return safety;
        }
    }
}
