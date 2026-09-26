using FluentAssertions;
using NSubstitute;
using SpatialAgent.Runtime;
using SpatialAgent.Runtime.Contracts;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    public class ApprovalEventOrderingTests
    {
        private sealed class FakeLlmProvider : ILLMProvider
        {
            private readonly Queue<LLMResponse> _responses = new();

            public string ProviderName => "fake";
            public string ModelName => "fake-model";
            public string ThinkingEffort => "";
            public LLMCapability Capability { get; } = new() { ToolCalling = true, StructuredOutput = true };
            public bool IsConfigured => true;

            public void SetApiKey(string key) { }

            public void Enqueue(params LLMResponse[] responses)
            {
                foreach (var response in responses)
                    _responses.Enqueue(response);
            }

            public Task<LLMResponse> ChatAsync(
                IReadOnlyList<Message> messages,
                string systemPrompt,
                IReadOnlyList<IToolDescriptor>? tools = null,
                double temperature = 0.7,
                int maxTokens = 4096,
                CancellationToken cancellation = default)
            {
                if (_responses.Count == 0)
                    throw new InvalidOperationException("No fake LLM response queued.");

                return Task.FromResult(_responses.Dequeue());
            }
        }

        private sealed class StubDescriptor : IToolDescriptor
        {
            public string Name { get; init; } = "";
            public string Description { get; init; } = "";
            public string Category { get; init; } = "";
            public ToolAnnotation Annotation { get; init; } = ToolAnnotation.Modify;
            public IReadOnlyDictionary<string, ParameterProperty> Parameters { get; init; }
                = new Dictionary<string, ParameterProperty>();
            public IReadOnlyList<string> RequiredParameters { get; init; } = new List<string>();
            public VerificationRule? Verification { get; init; }
            public IReadOnlyDictionary<string, string>? DefaultParameters => null;
        }

        private sealed class StubExecutor : IToolExecutor
        {
            private readonly Func<ToolCall, ToolResult> _execute;

            public StubExecutor(Func<ToolCall, ToolResult> execute) => _execute = execute;

            public Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken ct = default)
                => Task.FromResult(_execute(call));
        }

        private static ContextManager CreateContextManager()
        {
            var projectReader = Substitute.For<IProjectReader>();
            projectReader.GetContextForAIAsync().Returns("项目：测试项目\n项目目录：G:\\test");
            projectReader.GetProjectInfoAsync().Returns("{}");
            projectReader.ScanFolderAsync(Arg.Any<string>()).Returns("{}");

            var mapReader = Substitute.For<IMapReader>();
            mapReader.GetMapInfoAsync().Returns("{}");
            mapReader.ListLayersAsync().Returns("{}");
            mapReader.DescribeLayerAsync(Arg.Any<string>()).Returns("{}");
            mapReader.GetAllLayerSummariesAsync().Returns("");
            mapReader.GetSelectionAsync().Returns("{}");
            mapReader.GetProjectExtentAsync().Returns("{}");
            mapReader.GetSpatialRefContextAsync().Returns(new SpatialRefContext());

            var skillProvider = Substitute.For<ISkillProvider>();
            skillProvider.Count.Returns(0);
            skillProvider.GetSkillsSummary().Returns("");
            skillProvider.MatchSkills(Arg.Any<string>()).Returns(new List<MatchedSkill>());
            skillProvider.MatchSkillsAsync(Arg.Any<string>()).Returns(Task.FromResult(new List<MatchedSkill>()));

            return new ContextManager(projectReader, mapReader, skillProvider, knowledge: null);
        }

        private static ISafetyGate CreateAllowModifySafety(ToolRegistry tools)
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
                    var toolName = ci.Arg<string>();
                    var risk = tools.GetDescriptor(toolName)?.Annotation switch
                    {
                        ToolAnnotation.ReadOnly => ToolRiskLevel.ReadOnly,
                        ToolAnnotation.Destructive => ToolRiskLevel.Destructive,
                        _ => ToolRiskLevel.Modify
                    };

                    return new SafetyDecision
                    {
                        Kind = SafetyDecisionKind.Allow,
                        RiskLevel = risk,
                        ToolName = toolName
                    };
                });
            return safety;
        }

        private static IDataInfluenceEstimator CreateApprovingEstimator(int effectiveCount)
        {
            var estimator = Substitute.For<IDataInfluenceEstimator>();
            estimator.EstimateAsync(Arg.Any<ToolCall>(), Arg.Any<DataInfluenceContext?>())
                .Returns(Task.FromResult(new DataInfluenceEstimate
                {
                    LayerName = "parcel_layer",
                    FieldName = "TBMJ",
                    FeatureCount = effectiveCount,
                    NonNullCount = effectiveCount,
                    IsReliable = true,
                    Source = EvidenceSource.Describe
                }));
            estimator.EstimateBatchAsync(Arg.Any<IReadOnlyList<ToolCall>>(), Arg.Any<IReadOnlyList<DataInfluenceContext?>?>())
                .Returns(Task.FromResult<IReadOnlyList<DataInfluenceEstimate>>(new[]
                {
                    new DataInfluenceEstimate
                    {
                        LayerName = "parcel_layer",
                        FieldName = "TBMJ",
                        FeatureCount = effectiveCount,
                        NonNullCount = effectiveCount,
                        IsReliable = true,
                        Source = EvidenceSource.Describe
                    }
                }));
            return estimator;
        }

        private static AgentRuntime CreateRuntime(
            EventBus bus,
            FakeLlmProvider llm,
            ToolRegistry tools,
            IDataInfluenceEstimator estimator)
        {
            var session = new AgentSession("thread-ordering", bus)
            {
                PermissionLevel = AgentPermissionLevel.RequestApproval
            };

            var runtime = new AgentRuntime(session, llm, tools, CreateContextManager(), bus);
            runtime.SetSafety(CreateAllowModifySafety(tools));
            runtime.SetDataInfluenceEstimator(estimator);
            runtime.SetPlanningServices(
                coverageValidator: new SyntheticPlanCoverageValidator());
            return runtime;
        }

        private static ToolRegistry CreateModifyRegistry()
        {
            var tools = new ToolRegistry();
            tools.Register(
                new StubDescriptor { Name = "management_CalculateField", Annotation = ToolAnnotation.Modify },
                new StubExecutor(call => new ToolResult { ToolName = call.Name, Success = true }));
            return tools;
        }

        private static List<AgentEvent> RecordEvents(EventBus bus)
        {
            var events = new List<AgentEvent>();
            bus.Subscribe("*", e => events.Add(e));
            return events;
        }

        private static int IndexOfEvent(IReadOnlyList<AgentEvent> events, string eventType, string? action = null)
        {
            for (int i = 0; i < events.Count; i++)
            {
                if (!string.Equals(events[i].EventType, eventType, StringComparison.Ordinal))
                    continue;
                if (action != null && events[i].Metadata?.GetValueOrDefault("action") != action)
                    continue;
                return i;
            }

            return -1;
        }

        [Fact]
        public async Task Approval_required_then_approved_then_resume_execution_keeps_event_order()
        {
            var bus = new EventBus();
            var events = RecordEvents(bus);
            var llm = new FakeLlmProvider();
            llm.Enqueue(
                new LLMResponse
                {
                    Success = true,
                    ToolCalls = new List<ToolCall>
                    {
                        new()
                        {
                            Id = "call-1",
                            Name = "management_CalculateField",
                            Arguments = new Dictionary<string, object?>
                            {
                                ["in_table"] = "parcel_layer",
                                ["field_name"] = "TBMJ"
                            }
                        }
                    }
                },
                new LLMResponse
                {
                    Success = true,
                    Content = "已完成字段更新。"
                });

            var runtime = CreateRuntime(bus, llm, CreateModifyRegistry(), CreateApprovingEstimator(147));

            var first = await runtime.ProcessAsync(new TaskRequest { UserMessage = "更新面积字段" });
            first.RequiresApproval.Should().BeTrue();

            await runtime.ProcessAsync(new TaskRequest
            {
                UserMessage = "更新面积字段",
                AppendUserMessage = false,
                IsApprovalResumption = true,
                ApprovalDecision = new ApprovalDecision
                {
                    TicketId = first.PendingApproval!.Id,
                    Decision = ApprovalDecisionKind.Approve,
                    Scope = first.PendingApproval.Scope,
                    Reason = "ApprovedFromTest",
                    DecidedAtUtc = new DateTime(2026, 7, 21, 0, 0, 0, DateTimeKind.Utc)
                }
            });

            int approvalRequired = IndexOfEvent(events, "safety.influence_approval_required");
            int approvalResumed = IndexOfEvent(events, "safety.approval_resumed", "approved");
            int toolStarted = IndexOfEvent(events, "tool.started");
            int toolCompleted = IndexOfEvent(events, "tool.completed");

            approvalRequired.Should().BeGreaterThanOrEqualTo(0);
            approvalResumed.Should().BeGreaterThan(approvalRequired);
            toolStarted.Should().BeGreaterThan(approvalResumed);
            toolCompleted.Should().BeGreaterThan(toolStarted);

            events[approvalResumed].Metadata!.GetValueOrDefault("ticket_id").Should().Be(first.PendingApproval!.Id);
        }

        [Fact]
        public async Task Approval_required_then_rejected_then_repeated_call_is_blocked_in_order()
        {
            var bus = new EventBus();
            var events = RecordEvents(bus);
            var llm = new FakeLlmProvider();
            var repeatedCallArgs = new Dictionary<string, object?>
            {
                ["in_table"] = "parcel_layer",
                ["field_name"] = "TBMJ"
            };

            llm.Enqueue(
                new LLMResponse
                {
                    Success = true,
                    ToolCalls = new List<ToolCall>
                    {
                        new()
                        {
                            Id = "call-1",
                            Name = "management_CalculateField",
                            Arguments = repeatedCallArgs
                        }
                    }
                },
                new LLMResponse
                {
                    Success = true,
                    ToolCalls = new List<ToolCall>
                    {
                        new()
                        {
                            Id = "call-2",
                            Name = "management_CalculateField",
                            Arguments = new Dictionary<string, object?>(repeatedCallArgs)
                        }
                    }
                },
                new LLMResponse
                {
                    Success = true,
                    Content = "已放弃该写入。"
                });

            var runtime = CreateRuntime(bus, llm, CreateModifyRegistry(), CreateApprovingEstimator(147));

            var first = await runtime.ProcessAsync(new TaskRequest { UserMessage = "更新面积字段" });
            first.RequiresApproval.Should().BeTrue();

            await runtime.ProcessAsync(new TaskRequest
            {
                UserMessage = "更新面积字段",
                AppendUserMessage = false,
                IsApprovalResumption = true,
                ApprovalDecision = new ApprovalDecision
                {
                    TicketId = first.PendingApproval!.Id,
                    Decision = ApprovalDecisionKind.Reject,
                    Scope = first.PendingApproval.Scope,
                    Reason = "RejectedFromTest",
                    DecidedAtUtc = new DateTime(2026, 7, 21, 0, 0, 0, DateTimeKind.Utc)
                }
            });

            int approvalRequired = IndexOfEvent(events, "safety.influence_approval_required");
            int approvalRejected = IndexOfEvent(events, "safety.approval_resumed", "rejected");
            int blockedByUser = IndexOfEvent(events, "safety.blocked_by_user");

            approvalRequired.Should().BeGreaterThanOrEqualTo(0);
            approvalRejected.Should().BeGreaterThan(approvalRequired);
            blockedByUser.Should().BeGreaterThan(approvalRejected);

            events[approvalRejected].Metadata!.GetValueOrDefault("ticket_id").Should().Be(first.PendingApproval!.Id);
            events[blockedByUser].Metadata!.GetValueOrDefault("call_signature").Should().Be(first.PendingApproval!.CallSignature);
        }
    }
}
