using FluentAssertions;
using NSubstitute;
using SpatialAgent.Runtime;
using SpatialAgent.Runtime.Contracts;
using SpatialAgent.Services;
using SpatialAgent.Core;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    public class ApprovalLifecycleTests
    {
        private sealed class FakeLlmProvider : ILLMProvider
        {
            private readonly Queue<LLMResponse> _responses = new();

            public string ProviderName => "fake";
            public string ModelName => "fake-model";
            public string ThinkingEffort => "";
            public LLMCapability Capability { get; } = new() { ToolCalling = true, StructuredOutput = true };
            public bool IsConfigured => true;
            public int CallCount { get; private set; }
            public List<IReadOnlyList<Message>> Requests { get; } = new();

            public void Enqueue(params LLMResponse[] responses)
            {
                foreach (var response in responses)
                    _responses.Enqueue(response);
            }

            public void SetApiKey(string key) { }

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

                CallCount++;
                Requests.Add(messages.ToList().AsReadOnly());
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

        private static ISafetyGate CreateDefaultSafety(ToolRegistry tools)
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
                    var permission = ci.Arg<AgentPermissionLevel>();
                    var risk = tools.GetDescriptor(toolName)?.Annotation switch
                    {
                        ToolAnnotation.ReadOnly => ToolRiskLevel.ReadOnly,
                        ToolAnnotation.Destructive => ToolRiskLevel.Destructive,
                        _ => ToolRiskLevel.Modify
                    };

                    if (permission == AgentPermissionLevel.RequestApproval && risk == ToolRiskLevel.Destructive)
                    {
                        return new SafetyDecision
                        {
                            Kind = SafetyDecisionKind.RequireApproval,
                            RiskLevel = risk,
                            ToolName = toolName,
                            Reason = "需要用户确认删除操作"
                        };
                    }

                    return new SafetyDecision
                    {
                        Kind = SafetyDecisionKind.Allow,
                        RiskLevel = risk,
                        ToolName = toolName
                    };
                });
            return safety;
        }

        [Fact]
        public void Approval_target_uses_project_dataset_output_not_coordinate_setting()
        {
            var target = ApprovalProtocol.BuildTarget(
                new Dictionary<string, object?>
                {
                    ["in_dataset"] = "villages",
                    ["out_dataset"] = "villages_4490",
                    ["out_coor_system"] = 4490
                });

            target.Dataset.Should().Be("villages");
            target.OutputTarget.Should().Be("villages_4490");
            target.OutputTarget.Should().NotBe("4490");
        }

        [Fact]
        public void Approval_target_uses_descriptor_semantics_for_field_parameters()
        {
            var descriptor = new StubDescriptor
            {
                Name = "schema_change",
                Parameters = new Dictionary<string, ParameterProperty>
                {
                    ["source"] = new() { SemanticRole = "input_dataset" },
                    ["columns"] = new() { SemanticRole = "field", IsField = true, MultiValue = true }
                }
            };

            var target = ApprovalProtocol.BuildTarget(
                new Dictionary<string, object?>
                {
                    ["source"] = "assets",
                    ["columns"] = new[] { "legacy_code", "legacy_name" }
                },
                descriptor);

            target.Dataset.Should().Be("assets");
            target.Field.Should().Be("legacy_code, legacy_name");
        }

        [Fact]
        public async Task Scheduler_resumption_executes_only_ticket_selected_for_resume()
        {
            int executeCount = 0;
            var registry = new ToolRegistry();
            registry.Register(
                new StubDescriptor { Name = "management_DeleteField", Annotation = ToolAnnotation.Destructive },
                new StubExecutor(call =>
                {
                    executeCount++;
                    return new ToolResult { ToolName = call.Name, Success = true };
                }));

            var safety = Substitute.For<ISafetyGate>();
            safety.Classify("management_DeleteField").Returns(ToolRiskLevel.Destructive);

            var scheduler = new Scheduler(registry, eventBus: null, validators: null, safety: safety);
            scheduler.CurrentPermissionLevel = AgentPermissionLevel.RequestApproval;
            scheduler.IsApprovalResumption = true;

            var args1 = new Dictionary<string, object?> { ["in_table"] = "layer_a", ["field_name"] = "f1" };
            var args2 = new Dictionary<string, object?> { ["in_table"] = "layer_b", ["field_name"] = "f2" };
            var ticket1 = new ApprovalTicket
            {
                Id = "ticket-a",
                StepId = 1,
                Kind = ApprovalKind.Destructive,
                Scope = ApprovalScope.ExactCall,
                RequiredPermissionLevel = AgentPermissionLevel.FullAccess,
                ToolName = "management_DeleteField",
                CanonicalToolName = "management_DeleteField",
                CallSignature = ApprovalProtocol.ComputeCallSignature("management_DeleteField", args1),
                Target = new ApprovalTarget { Signature = "target:a" },
                Evidence = new ApprovalEvidence { Summary = "A" }
            };
            var ticket2 = new ApprovalTicket
            {
                Id = "ticket-b",
                StepId = 2,
                Kind = ApprovalKind.Destructive,
                Scope = ApprovalScope.ExactCall,
                RequiredPermissionLevel = AgentPermissionLevel.FullAccess,
                ToolName = "management_DeleteField",
                CanonicalToolName = "management_DeleteField",
                CallSignature = ApprovalProtocol.ComputeCallSignature("management_DeleteField", args2),
                Target = new ApprovalTarget { Signature = "target:b" },
                Evidence = new ApprovalEvidence { Summary = "B" }
            };
            scheduler.ApprovedTicketIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ticket1.Id };

            var workspace = Substitute.For<IWorkspaceService>();
            workspace.BackupAsync(Arg.Any<string>(), Arg.Any<string>()).Returns("G:\\backup\\snapshot.gdb");
            scheduler.SetWorkspace(workspace);

            var plan = new AgentPlan
            {
                Goal = "resume",
                Steps = new List<PlanStep>
                {
                    new()
                    {
                        Id = 1,
                        Description = "step-1",
                        Status = PlanStepStatus.AwaitingApproval,
                        PendingApproval = ticket1,
                        ToolCall = new ToolCall { Id = "call-1", Name = "management_DeleteField", Arguments = args1 }
                    },
                    new()
                    {
                        Id = 2,
                        Description = "step-2",
                        Status = PlanStepStatus.AwaitingApproval,
                        PendingApproval = ticket2,
                        ToolCall = new ToolCall { Id = "call-2", Name = "management_DeleteField", Arguments = args2 }
                    }
                }
            };

            var results = await scheduler.RunAsync(plan);

            results.Should().HaveCount(1);
            results[0].StepId.Should().Be(1);
            results[0].Status.Should().Be(StepResultStatus.Completed);
            executeCount.Should().Be(1);
        }

        [Fact]
        public async Task Runtime_approval_response_contains_ticket_and_resume_does_not_change_session_permission()
        {
            var session = new AgentSession("thread-approval");
            session.PermissionLevel = AgentPermissionLevel.RequestApproval;

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
                            Name = "management_DeleteField",
                            Arguments = new Dictionary<string, object?>
                            {
                                ["in_table"] = "parcel_layer",
                                ["field_name"] = "OLD_FIELD"
                            }
                        }
                    }
                },
                new LLMResponse
                {
                    Success = true,
                    Content = "删除已完成。"
                });

            var tools = new ToolRegistry();
            tools.Register(
                new StubDescriptor { Name = "management_DeleteField", Annotation = ToolAnnotation.Destructive },
                new StubExecutor(call => new ToolResult { ToolName = call.Name, Success = true }));

            var runtime = new AgentRuntime(session, llm, tools, CreateContextManager(), bus: new EventBus());
            runtime.SetSafety(CreateDefaultSafety(tools));
            runtime.SetPlanningServices(
                coverageValidator: new SyntheticPlanCoverageValidator());
            var checkpointStates = new List<AgentRunState>();
            var checkpointStore = Substitute.For<IRunCheckpointStore>();
            checkpointStore.SaveAsync(
                    Arg.Any<AgentRunState>(),
                    Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    checkpointStates.Add(call.Arg<AgentRunState>());
                    return Task.CompletedTask;
                });
            runtime.SetCheckpointStore(checkpointStore);

            var workspace = Substitute.For<IWorkspaceService>();
            workspace.BackupAsync(Arg.Any<string>(), Arg.Any<string>()).Returns("G:\\backup\\snapshot.gdb");
            runtime.SetWorkspace(workspace);

            var firstResponse = await runtime.ProcessAsync(new TaskRequest { UserMessage = "删除字段" });

            firstResponse.RequiresApproval.Should().BeTrue();
            firstResponse.PendingApproval.Should().NotBeNull();
            firstResponse.PendingApproval!.Id.Should().NotBeNullOrWhiteSpace();
            firstResponse.PendingApproval.Kind.Should().Be(ApprovalKind.Destructive);
            firstResponse.PendingApproval.CallSignature.Should().StartWith("sig:");
            session.PermissionLevel.Should().Be(AgentPermissionLevel.RequestApproval);
            checkpointStates.Last().Status.Should().Be(SessionStatus.WaitingForApproval);
            checkpointStates.Last().PendingApproval.Should().NotBeNull();

            var resumeResponse = await runtime.ProcessAsync(new TaskRequest
            {
                UserMessage = "删除字段",
                AppendUserMessage = false,
                IsApprovalResumption = true,
                ApprovalDecision = new ApprovalDecision
                {
                    TicketId = firstResponse.PendingApproval.Id,
                    Decision = ApprovalDecisionKind.Approve,
                    Scope = firstResponse.PendingApproval.Scope,
                    Reason = "ApprovedFromTest",
                    DecidedAtUtc = new DateTime(2026, 7, 21, 0, 0, 0, DateTimeKind.Utc)
                }
            });

            resumeResponse.UserReply.Should().Contain("部分完成：删除字段");
            resumeResponse.UserReply.Should().Contain(
                "尚缺证据：proof:gis.schema.field");
            resumeResponse.StopReason.Should().Be("部分完成");
            resumeResponse.UserReply.Should().NotContain("步骤");
            llm.CallCount.Should().Be(
                2,
                "审批恢复后 residual 必须重新进入同一规划闭环");
            llm.Requests.Last().Should().Contain(message =>
                message.Role == "tool"
                && message.ToolCallId == "call-1");
            session.PermissionLevel.Should().Be(AgentPermissionLevel.RequestApproval);
            checkpointStates.Last().Status.Should().Be(SessionStatus.Completed);
            checkpointStates.Last().PendingApproval.Should().BeNull();
            checkpointStates.Last().Plan.Should().BeNull();
        }

        [Fact]
        public async Task Checkpoint_store_roundtrips_pending_approval_and_blocked_signatures()
        {
            string root = Path.Combine(Path.GetTempPath(), "spatial-agent-approval-checkpoint-" + Guid.NewGuid().ToString("N"));
            var store = new FileRunCheckpointStore(root);

            var ticket = new ApprovalTicket
            {
                Id = "ticket-checkpoint",
                StepId = 3,
                Kind = ApprovalKind.DataInfluence,
                Scope = ApprovalScope.ExactCall,
                ToolName = "management_CalculateField",
                CanonicalToolName = "management_CalculateField",
                CallSignature = "sig:checkpoint",
                Target = new ApprovalTarget
                {
                    Dataset = "parcel_layer",
                    Field = "TBMJ",
                    WhereClause = "OBJECTID < 10",
                    Signature = "target:checkpoint"
                },
                Evidence = new ApprovalEvidence
                {
                    Summary = "将影响多条记录",
                    Source = "Estimator",
                    IsReliable = true,
                    InfluenceCount = 147
                }
            };

            var state = new AgentRunState
            {
                ThreadId = "thread-checkpoint",
                Turn = 2,
                Round = 1,
                UserMessage = "更新面积字段",
                Status = SessionStatus.WaitingForApproval,
                PermissionLevel = AgentPermissionLevel.RequestApproval,
                PendingApproval = ticket,
                PendingTools = new List<string> { "management_CalculateField" },
                BlockedCallSignatures = new List<string> { ticket.CallSignature }
            };

            await store.SaveAsync(state);
            var loaded = await store.LoadLatestAsync("thread-checkpoint");

            loaded.Should().NotBeNull();
            loaded!.PendingApproval.Should().NotBeNull();
            loaded.PendingApproval!.Id.Should().Be(ticket.Id);
            loaded.PendingApproval.CallSignature.Should().Be(ticket.CallSignature);
            loaded.PendingApproval.Evidence.Summary.Should().Be("将影响多条记录");
            loaded.BlockedCallSignatures.Should().ContainSingle().Which.Should().Be(ticket.CallSignature);
        }
    }
}
