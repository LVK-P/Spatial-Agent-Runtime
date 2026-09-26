using System.Net;
using FluentAssertions;
using NSubstitute;
using SpatialAgent.Adapters.LLM;
using SpatialAgent.Core;
using SpatialAgent.Runtime;
using SpatialAgent.Runtime.Contracts;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    public sealed class GlobalClosureContractTests
    {
        [Fact]
        public void Tool_message_protocol_requires_one_response_per_call_id()
        {
            var messages = new List<Message>
            {
                AssistantCalls("call-1", "call-2"),
                new() { Role = "tool", ToolCallId = "call-1", Content = "{}" },
                new() { Role = "user", Content = "continue" }
            };

            ToolMessageProtocolResult result =
                ToolMessageProtocol.Validate(messages);

            result.IsValid.Should().BeFalse();
            result.Error.Should().Contain("call-2");
        }

        [Fact]
        public void Tool_message_protocol_accepts_a_fully_closed_batch()
        {
            var messages = new List<Message>
            {
                AssistantCalls("call-1", "call-2"),
                new() { Role = "tool", ToolCallId = "call-2", Content = "{}" },
                new() { Role = "tool", ToolCallId = "call-1", Content = "{}" },
                new() { Role = "user", Content = "continue" }
            };

            ToolMessageProtocol.Validate(messages).IsValid.Should().BeTrue();
        }

        [Fact]
        public void Tool_call_batch_rejects_duplicate_provider_ids()
        {
            ToolMessageProtocolResult result =
                ToolMessageProtocol.ValidateToolCallBatch(new[]
                {
                    new ToolCall { Id = "duplicate", Name = "first" },
                    new ToolCall { Id = "duplicate", Name = "second" }
                });

            result.IsValid.Should().BeFalse();
            result.Error.Should().Contain("Duplicate");
        }

        [Fact]
        public async Task Provider_rejects_an_open_tool_batch_before_http()
        {
            var handler = new CountingHandler();
            using var provider = new DeepSeekProvider("test", handler);
            provider.SetApiKey("test-key");

            LLMResponse response = await provider.ChatAsync(
                new List<Message>
                {
                    AssistantCalls("call-1", "call-2"),
                    new()
                    {
                        Role = "tool",
                        ToolCallId = "call-1",
                        Content = "{}"
                    }
                },
                "system");

            response.Success.Should().BeFalse();
            response.ErrorKind.Should().Be(LLMErrorKind.InvalidRequest);
            handler.CallCount.Should().Be(0);
        }

        [Fact]
        public void File_datastore_uri_is_normalized_for_system_io()
        {
            var uri = new Uri(
                "file:///G:/VScode%20projects/project/Default.gdb");

            string path = FileSystemDataSourcePath.Normalize(uri);

            path.Should().Be(uri.LocalPath);
            path.Should().NotStartWith("file:");
        }

        [Fact]
        public async Task Scheduler_settles_every_call_when_nonfirst_step_is_blocked()
        {
            int executions = 0;
            var registry = new ToolRegistry();
            registry.Register(
                Descriptor("read_tool", ToolAnnotation.ReadOnly),
                new DelegateExecutor(_ =>
                {
                    executions++;
                    return Success("read_tool");
                }));
            registry.Register(
                Descriptor("modify_tool", ToolAnnotation.Modify),
                new DelegateExecutor(_ =>
                {
                    executions++;
                    return Success("modify_tool");
                }));

            var safety = Substitute.For<ISafetyGate>();
            safety.Classify("read_tool").Returns(ToolRiskLevel.ReadOnly);
            safety.Classify("modify_tool").Returns(ToolRiskLevel.Modify);
            safety.Evaluate(
                    Arg.Any<string>(),
                    Arg.Any<AgentPermissionLevel>())
                .Returns(call => call.ArgAt<string>(0) == "modify_tool"
                    ? new SafetyDecision
                    {
                        Kind = SafetyDecisionKind.Block,
                        ToolName = "modify_tool",
                        RiskLevel = ToolRiskLevel.Modify,
                        Reason = "read-only permission"
                    }
                    : new SafetyDecision
                    {
                        Kind = SafetyDecisionKind.Allow,
                        ToolName = "read_tool",
                        RiskLevel = ToolRiskLevel.ReadOnly
                    });
            var scheduler = new Scheduler(
                registry,
                validators: null,
                safety: safety)
            {
                CurrentPermissionLevel = AgentPermissionLevel.ReadOnly
            };

            IReadOnlyList<StepExecutionResult> results =
                await scheduler.RunAsync(new AgentPlan
                {
                    Steps = new()
                    {
                        Step(1, "call-read", "read_tool"),
                        Step(2, "call-modify", "modify_tool")
                    }
                });

            results.Should().HaveCount(2);
            results.Select(result => result.ToolCallId)
                .Should().Equal("call-read", "call-modify");
            results[0].Status.Should().Be(StepResultStatus.Skipped);
            results[1].Status.Should().Be(StepResultStatus.Blocked);
            executions.Should().Be(0);
        }

        [Fact]
        public async Task Scheduler_skips_dependents_after_prerequisite_failure()
        {
            int dependentExecutions = 0;
            var registry = new ToolRegistry();
            registry.Register(
                Descriptor("first_tool", ToolAnnotation.Modify),
                new DelegateExecutor(_ => new ToolResult
                {
                    ToolName = "first_tool",
                    Success = false,
                    Error = "failed"
                }));
            registry.Register(
                Descriptor("dependent_tool", ToolAnnotation.Modify),
                new DelegateExecutor(_ =>
                {
                    dependentExecutions++;
                    return Success("dependent_tool");
                }));
            var safety = AllowAllSafety();
            var scheduler = new Scheduler(
                registry,
                validators: null,
                safety: safety)
            {
                CurrentPermissionLevel = AgentPermissionLevel.FullAccess
            };
            PlanStep dependent =
                Step(2, "call-dependent", "dependent_tool");
            dependent.DependsOn.Add(1);
            dependent.RequiresSuccessfulSteps.Add(1);

            IReadOnlyList<StepExecutionResult> results =
                await scheduler.RunAsync(new AgentPlan
                {
                    Steps = new()
                    {
                        Step(1, "call-first", "first_tool"),
                        dependent
                    }
                });

            results.Should().HaveCount(2);
            results[0].Status.Should().Be(StepResultStatus.Failed);
            results[1].Status.Should().Be(StepResultStatus.Skipped);
            results[1].BlockReason.Should().Contain("1");
            dependentExecutions.Should().Be(0);
        }

        [Fact]
        public async Task Scheduler_continues_ordered_but_independent_writes()
        {
            int secondExecutions = 0;
            var registry = new ToolRegistry();
            registry.Register(
                Descriptor("first_tool", ToolAnnotation.Modify),
                new DelegateExecutor(_ => new ToolResult
                {
                    ToolName = "first_tool",
                    Success = false,
                    Error = "failed"
                }));
            registry.Register(
                Descriptor("second_tool", ToolAnnotation.Modify),
                new DelegateExecutor(_ =>
                {
                    secondExecutions++;
                    return Success("second_tool");
                }));
            var scheduler = new Scheduler(
                registry,
                validators: null,
                safety: AllowAllSafety())
            {
                CurrentPermissionLevel = AgentPermissionLevel.FullAccess
            };
            PlanStep second = Step(2, "call-second", "second_tool");
            second.DependsOn.Add(1);

            IReadOnlyList<StepExecutionResult> results =
                await scheduler.RunAsync(new AgentPlan
                {
                    Steps = new()
                    {
                        Step(1, "call-first", "first_tool"),
                        second
                    }
                });

            results.Select(result => result.Status)
                .Should().Equal(
                    StepResultStatus.Failed,
                    StepResultStatus.Completed);
            secondExecutions.Should().Be(1);
        }

        [Fact]
        public async Task Runtime_closes_all_six_calls_after_host_guard_stops_batch()
        {
            RuntimeFixture fixture = RuntimeFixture.Create(
                "content:planned",
                "unreliable:test-host-state");
            fixture.Register("management_Project");
            fixture.QueueResponses(
                ToolResponse(Enumerable.Range(1, 6)
                    .Select(index => new ToolCall
                    {
                        Id = $"call-{index}",
                        Name = "management_Project",
                        Arguments = new()
                        {
                            ["in_dataset"] = $"layer-{index}",
                            ["out_dataset"] = $"output-{index}"
                        }
                    })
                    .ToArray()),
                new LLMResponse
                {
                    Success = true,
                    Content = "stopped"
                });

            await fixture.Runtime.ProcessAsync(new TaskRequest
            {
                UserMessage = "process all layers"
            });

            fixture.Requests.Should().HaveCount(2);
            IReadOnlyList<Message> continuation = fixture.Requests[1];
            ToolMessageProtocol.Validate(continuation)
                .IsValid.Should().BeTrue();
            continuation.Where(message => message.Role == "tool")
                .Select(message => message.ToolCallId)
                .Should().Equal(Enumerable.Range(1, 6)
                    .Select(index => $"call-{index}"));
            var toolMessages = continuation
                .Where(message => message.Role == "tool")
                .ToList();
            toolMessages[0].Content.Should().Contain("Blocked");
            toolMessages.Skip(1).Should()
                .OnlyContain(message =>
                    message.Content.Contains("Skipped"));
        }

        [Fact]
        public async Task Runtime_answers_hallucinated_and_valid_calls_by_original_id()
        {
            RuntimeFixture fixture = RuntimeFixture.Create(
                "content:stable",
                "content:stable",
                "content:stable");
            fixture.Register("valid_tool");
            fixture.QueueResponses(
                ToolResponse(
                    new ToolCall
                    {
                        Id = "unknown-id",
                        Name = "unknown_tool"
                    },
                    new ToolCall
                    {
                        Id = "valid-id",
                        Name = "valid_tool"
                    }),
                new LLMResponse
                {
                    Success = true,
                    Content = "done"
                });

            await fixture.Runtime.ProcessAsync(new TaskRequest
            {
                UserMessage = "run mixed batch"
            });

            fixture.Requests.Should().HaveCount(2);
            IReadOnlyList<Message> continuation = fixture.Requests[1];
            ToolMessageProtocol.Validate(continuation)
                .IsValid.Should().BeTrue();
            continuation.Where(message => message.Role == "tool")
                .Select(message => message.ToolCallId)
                .Should().Equal("unknown-id", "valid-id");
        }

        [Fact]
        public async Task Runtime_closes_and_records_an_all_unregistered_batch()
        {
            RuntimeFixture fixture = RuntimeFixture.Create(
                "content:stable");
            fixture.QueueResponses(
                ToolResponse(
                    new ToolCall
                    {
                        Id = "unknown-1",
                        Name = "missing_one"
                    },
                    new ToolCall
                    {
                        Id = "unknown-2",
                        Name = "missing_two"
                    }),
                new LLMResponse
                {
                    Success = true,
                    Content = "not available"
                });

            AgentResponse response =
                await fixture.Runtime.ProcessAsync(new TaskRequest
                {
                    UserMessage = "run unavailable tools"
                });

            fixture.Requests.Should().HaveCount(2);
            ToolMessageProtocol.Validate(fixture.Requests[1])
                .IsValid.Should().BeTrue();
            fixture.Requests[1]
                .Where(message => message.Role == "tool")
                .Select(message => message.ToolCallId)
                .Should().Equal("unknown-1", "unknown-2");
            response.HadToolCalls.Should().BeTrue();
            response.ToolResults.Should().HaveCount(2);
            response.ToolResults.Should()
                .OnlyContain(result => !result.Success);
        }

        private static Message AssistantCalls(params string[] ids) => new()
        {
            Role = "assistant",
            ToolCalls = ids.Select(id => new ToolCall
            {
                Id = id,
                Name = "tool"
            }).ToList()
        };

        private static LLMResponse ToolResponse(params ToolCall[] calls) =>
            new()
            {
                Success = true,
                ToolCalls = calls.ToList()
            };

        private static PlanStep Step(
            int id,
            string callId,
            string toolName) => new()
        {
            Id = id,
            ToolCall = new ToolCall
            {
                Id = callId,
                Name = toolName
            }
        };

        private static IToolDescriptor Descriptor(
            string name,
            ToolAnnotation annotation) => new TestDescriptor
        {
            Name = name,
            Annotation = annotation
        };

        private static ToolResult Success(string name) => new()
        {
            ToolName = name,
            Success = true
        };

        private static ISafetyGate AllowAllSafety()
        {
            var safety = Substitute.For<ISafetyGate>();
            safety.Classify(Arg.Any<string>())
                .Returns(ToolRiskLevel.Modify);
            safety.Evaluate(
                    Arg.Any<string>(),
                    Arg.Any<AgentPermissionLevel>())
                .Returns(call => new SafetyDecision
                {
                    Kind = SafetyDecisionKind.Allow,
                    ToolName = call.ArgAt<string>(0),
                    RiskLevel = ToolRiskLevel.Modify
                });
            return safety;
        }

        private sealed class TestDescriptor : IToolDescriptor
        {
            public string Name { get; init; } = "";
            public string Description { get; init; } = "";
            public string Category { get; init; } = "";
            public ToolAnnotation Annotation { get; init; }
            public IReadOnlyDictionary<string, ParameterProperty> Parameters
                { get; init; } =
                new Dictionary<string, ParameterProperty>();
            public IReadOnlyList<string> RequiredParameters { get; init; } =
                Array.Empty<string>();
            public VerificationRule? Verification { get; init; }
            public IReadOnlyDictionary<string, string>? DefaultParameters =>
                null;
        }

        private sealed class DelegateExecutor : IToolExecutor
        {
            private readonly Func<ToolCall, ToolResult> _execute;

            public DelegateExecutor(Func<ToolCall, ToolResult> execute)
            {
                _execute = execute;
            }

            public Task<ToolResult> ExecuteAsync(
                ToolCall call,
                CancellationToken ct = default) =>
                Task.FromResult(_execute(call));
        }

        private sealed class CountingHandler : HttpMessageHandler
        {
            public int CallCount { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                CallCount++;
                return Task.FromResult(new HttpResponseMessage(
                    HttpStatusCode.OK));
            }
        }

        private sealed class RuntimeFixture
        {
            private readonly Queue<LLMResponse> _responses = new();

            public AgentRuntime Runtime { get; }
            public ToolRegistry Tools { get; }
            public List<IReadOnlyList<Message>> Requests { get; } = new();

            private RuntimeFixture(
                IAgentSession session,
                ILLMProvider llm,
                ToolRegistry tools,
                ContextManager context)
            {
                Tools = tools;
                Runtime = new AgentRuntime(
                    session,
                    llm,
                    tools,
                    context);
                Runtime.SetSafety(AllowAllSafety());
                Runtime.SetPlanningServices(
                    coverageValidator:
                        new SyntheticPlanCoverageValidator());

                llm.ChatAsync(
                        Arg.Any<IReadOnlyList<Message>>(),
                        Arg.Any<string>(),
                        Arg.Any<IReadOnlyList<IToolDescriptor>?>(),
                        Arg.Any<double>(),
                        Arg.Any<int>())
                    .Returns(call =>
                    {
                        var request = call.ArgAt<IReadOnlyList<Message>>(0)
                            .Select(CloneMessage)
                            .ToList()
                            .AsReadOnly();
                        Requests.Add(request);
                        return _responses.Dequeue();
                    });
            }

            public static RuntimeFixture Create(
                params string[] contentRevisions)
            {
                var session = Substitute.For<IAgentSession>();
                session.ThreadId.Returns("closure-test");
                session.Messages.Returns(new List<Message>());
                session.PermissionLevel.Returns(
                    AgentPermissionLevel.FullAccess);
                session.TokenUsage.Returns(new TokenUsage());
                session.Domain.Returns(new DomainContext());
                session.Observations.Returns(new List<IObservation>());
                session.Validations.Returns(new List<ValidationResult>());
                session.Status.Returns(SessionStatus.Idle);

                var llm = Substitute.For<ILLMProvider>();
                llm.ModelName.Returns("test");
                llm.ProviderName.Returns("test");
                llm.ThinkingEffort.Returns("");
                llm.IsConfigured.Returns(true);
                llm.Capability.Returns(new LLMCapability
                {
                    ToolCalling = true
                });

                var project = Substitute.For<IProjectReader>();
                project.GetContextForAIAsync().Returns("project");
                project.GetProjectInfoAsync().Returns("{}");
                var map = Substitute.For<IMapReader>();
                map.GetMapInfoAsync().Returns("map");
                map.GetAllLayerSummariesAsync().Returns("layers");
                map.GetSelectionAsync().Returns("selection");
                map.GetSpatialRefContextAsync()
                    .Returns(new SpatialRefContext());
                var revisions = new Queue<string>(contentRevisions);
                map.GetContentRevisionAsync().Returns(_ =>
                    revisions.Count > 1
                        ? revisions.Dequeue()
                        : revisions.Peek());
                var skills = Substitute.For<ISkillProvider>();
                skills.Count.Returns(0);
                skills.GetSkillsSummary().Returns("");
                skills.MatchSkills(Arg.Any<string>())
                    .Returns(new List<MatchedSkill>());
                skills.MatchSkillsAsync(Arg.Any<string>())
                    .Returns(Task.FromResult(new List<MatchedSkill>()));

                return new RuntimeFixture(
                    session,
                    llm,
                    new ToolRegistry(),
                    new ContextManager(project, map, skills));
            }

            public void Register(string name)
            {
                Tools.Register(
                    Descriptor(name, ToolAnnotation.Modify),
                    new DelegateExecutor(_ => Success(name)));
            }

            public void QueueResponses(params LLMResponse[] responses)
            {
                foreach (LLMResponse response in responses)
                    _responses.Enqueue(response);
            }

            private static Message CloneMessage(Message source) => new()
            {
                Role = source.Role,
                Content = source.Content,
                ReasoningContent = source.ReasoningContent,
                ToolCallId = source.ToolCallId,
                ToolCalls = source.ToolCalls?.Select(call => new ToolCall
                {
                    Id = call.Id,
                    Name = call.Name,
                    Arguments =
                        new Dictionary<string, object?>(call.Arguments)
                }).ToList()
            };
        }
    }
}
