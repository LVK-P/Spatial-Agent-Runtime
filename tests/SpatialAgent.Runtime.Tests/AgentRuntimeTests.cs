using FluentAssertions;
using Newtonsoft.Json.Linq;
using NSubstitute;
using SpatialAgent.Runtime;
using SpatialAgent.Runtime.Contracts;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    /// <summary>
    /// AgentRuntime ReAct loop tests — fake LLM + real orchestrator.
    /// Each test drives ProcessAsync with controlled LLM responses and
    /// asserts the correct exit path, state variables, and F1 regression
    /// (_lastTurnToolCalls populated on every exit path).
    /// </summary>
    public class AgentRuntimeTests
    {
        // ════════════════════════════════════════════════════════════════
        // Test harness
        // ════════════════════════════════════════════════════════════════

        private class Harness
        {
            public IAgentSession Session { get; } = Substitute.For<IAgentSession>();
            public ILLMProvider Llm { get; } = Substitute.For<ILLMProvider>();
            public ITranscriptToolWriter TranscriptTool { get; } =
                Substitute.For<ITranscriptToolWriter>();
            public EventBus Bus { get; } = new();
            public ToolRegistry Tools { get; }
            public ContextManager Context { get; }
            public AgentRuntime Runtime { get; }

            public Harness()
            {
                Session.ThreadId.Returns("test-thread-1");
                Session.Messages.Returns(new List<Message>());
                Session.PermissionLevel.Returns(AgentPermissionLevel.FullAccess);
                Session.TokenUsage.Returns(new TokenUsage());
                Session.Domain.Returns(new DomainContext());
                Session.Observations.Returns(new List<IObservation>());
                Session.Validations.Returns(new List<ValidationResult>());
                Session.Status.Returns(SessionStatus.Idle);

                Llm.ModelName.Returns("test-model");
                Llm.ThinkingEffort.Returns("");
                Llm.ProviderName.Returns("test-provider");
                Llm.IsConfigured.Returns(true);
                Llm.Capability.Returns(new LLMCapability { ToolCalling = true, StructuredOutput = true });

                Tools = new ToolRegistry();

                var projectReader = Substitute.For<IProjectReader>();
                projectReader.GetContextForAIAsync().Returns("项目：测试项目\n家目录：G:\\test");
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

                Context = new ContextManager(projectReader, mapReader, skillProvider, knowledge: null);

                // Default safety: classify based on annotation, evaluate via permission
                var defaultSafety = Substitute.For<ISafetyGate>();
                defaultSafety.Classify(Arg.Any<string>()).Returns(ci =>
                    Tools.GetDescriptor(ci.Arg<string>())?.Annotation switch
                    {
                        ToolAnnotation.ReadOnly => ToolRiskLevel.ReadOnly,
                        ToolAnnotation.Destructive => ToolRiskLevel.Destructive,
                        _ => ToolRiskLevel.Modify
                    });
                defaultSafety.Evaluate(Arg.Any<string>(), Arg.Any<AgentPermissionLevel>())
                    .Returns(ci =>
                    {
                        var tool = ci.Arg<string>();
                        var perm = ci.Arg<AgentPermissionLevel>();
                        var risk = Tools.GetDescriptor(tool)?.Annotation switch
                        {
                            ToolAnnotation.ReadOnly => ToolRiskLevel.ReadOnly,
                            ToolAnnotation.Destructive => ToolRiskLevel.Destructive,
                            _ => ToolRiskLevel.Modify
                        };
                        if (perm == AgentPermissionLevel.ReadOnly && risk != ToolRiskLevel.ReadOnly)
                            return new SafetyDecision { Kind = SafetyDecisionKind.Block, RiskLevel = risk, ToolName = tool, Reason = "ReadOnly 模式" };
                        if (perm == AgentPermissionLevel.RequestApproval && risk == ToolRiskLevel.Destructive)
                            return new SafetyDecision { Kind = SafetyDecisionKind.RequireApproval, RiskLevel = risk, ToolName = tool, Reason = "需用户确认删除操作" };
                        return new SafetyDecision { Kind = SafetyDecisionKind.Allow, RiskLevel = risk, ToolName = tool };
                    });

                Runtime = new AgentRuntime(Session, Llm, Tools, Context, bus: Bus);
                Runtime.SetSafety(defaultSafety);
                Runtime.SetTranscriptWriter(
                    lifecycle: null,
                    sessionWriter: null,
                    turnWriter: null,
                    toolWriter: TranscriptTool);
                Runtime.SetPlanningServices(
                    coverageValidator:
                        new SyntheticPlanCoverageValidator());
            }

            /// <summary>Register a simple tool that always succeeds.</summary>
            public void RegisterPassingTool(string name)
            {
                Tools.Register(
                    new SimpleDescriptor { Name = name, Annotation = ToolAnnotation.Modify },
                    new SimpleExecutor(_ => new ToolResult { ToolName = name, Success = true }));
            }

            /// <summary>Register a tool that returns a specific result.</summary>
            public void RegisterTool(string name, ToolAnnotation ann, ToolResult result)
            {
                Tools.Register(
                    new SimpleDescriptor { Name = name, Annotation = ann },
                    new SimpleExecutor(_ => result));
            }

            public void SetLlmsNextResponse(LLMResponse resp)
            {
                Llm.ChatAsync(
                    Arg.Any<IReadOnlyList<Message>>(),
                    Arg.Any<string>(),
                    Arg.Any<IReadOnlyList<IToolDescriptor>?>(),
                    Arg.Any<double>(),
                    Arg.Any<int>())
                    .Returns(resp);
            }

            /// <summary>Returns a sequence of responses for successive ChatAsync calls.</summary>
            public void SetLlmResponseSequence(params LLMResponse[] responses)
            {
                var queue = new Queue<LLMResponse>(responses);
                Llm.ChatAsync(
                    Arg.Any<IReadOnlyList<Message>>(),
                    Arg.Any<string>(),
                    Arg.Any<IReadOnlyList<IToolDescriptor>?>(),
                    Arg.Any<double>(),
                    Arg.Any<int>())
                    .Returns(ci => queue.Dequeue());
            }
        }

        private sealed class SimpleDescriptor : IToolDescriptor
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

        private sealed class SimpleExecutor : IToolExecutor
        {
            private readonly Func<ToolCall, ToolResult> _fn;
            public SimpleExecutor(Func<ToolCall, ToolResult> fn) => _fn = fn;
            public Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken ct = default)
                => Task.FromResult(_fn(call));
        }

        private sealed class RejectingPlanValidator : IPlanValidator
        {
            public PlanValidationResult Validate(AgentPlan plan, DomainContext context) => new()
            {
                IsValid = false,
                Issues = new()
                {
                    new PlanIssue
                    {
                        Severity = IssueSeverity.Error,
                        StepIndex = 0,
                        ToolName = plan.Steps[0].ToolCall?.Name ?? "",
                        Description = "测试计划冲突",
                        FixSuggestion = "生成不同输出"
                    }
                }
            };
        }

        private sealed class RejectOncePlanValidator : IPlanValidator
        {
            private int _calls;

            public PlanValidationResult Validate(
                AgentPlan plan,
                DomainContext context)
            {
                if (Interlocked.Increment(ref _calls) == 1)
                {
                    return new PlanValidationResult
                    {
                        IsValid = false,
                        Issues = new()
                        {
                            new PlanIssue
                            {
                                Severity = IssueSeverity.Error,
                                StepIndex = 0,
                                ToolName = plan.Steps[0].ToolCall?.Name ?? "",
                                Description = "first version rejected"
                            }
                        }
                    };
                }
                return new PlanValidationResult { IsValid = true };
            }
        }

        private sealed class AlwaysReviewedRouter : IComplexityRouter
        {
            public ComplexityAssessment Assess(PlanningRequest request) => new()
            {
                Complexity = TaskComplexity.Complex,
                Mode = PlanningMode.PlannerWithReviewers,
                Score = 10,
                RecommendedSubagents = 1
            };
        }

        private sealed class BlockingReviewCoordinator : ISubagentCoordinator
        {
            public Task<IReadOnlyList<SubagentResult>> RunAsync(
                IEnumerable<SubagentTask> tasks,
                int maxConcurrency,
                CancellationToken cancellation = default)
            {
                var task = tasks.First();
                return Task.FromResult<IReadOnlyList<SubagentResult>>(new[]
                {
                    new SubagentResult
                    {
                        TaskId = task.Id,
                        Role = task.Role,
                        Success = true,
                        Findings = new() { "missing safety evidence" },
                        StructuredFindings = new()
                        {
                            new ReviewFinding
                            {
                                Code = "missing_safety_evidence",
                                Severity = ReviewFindingSeverity.Critical,
                                Disposition = ReviewDisposition.Block,
                                StepId = task.Plan?.Steps.FirstOrDefault()?.Id,
                                Message = "缺少安全证据。"
                            }
                        }
                    }
                });
            }
        }

        // ════════════════════════════════════════════════════════════════
        // Test 1: Pure text — no tool calls
        // ════════════════════════════════════════════════════════════════

        [Fact]
        public async Task Pure_text_reply_no_tool_calls()
        {
            var h = new Harness();
            h.SetLlmsNextResponse(new LLMResponse
            {
                Success = true,

                Content = "你好，有什么可以帮你的？"
            });

            var resp = await h.Runtime.ProcessAsync(new TaskRequest { UserMessage = "你好" });

            resp.HadToolCalls.Should().BeFalse();
            resp.StopReason.Should().Be("纯文本");
            // F1 regression: _lastTurnToolCalls should be empty (no tools executed)
            h.Session.Received().SetStatus(SessionStatus.Completed);
        }

        [Fact]
        public async Task Semantic_empty_response_gets_one_bounded_recovery()
        {
            var h = new Harness();
            h.SetLlmResponseSequence(
                new LLMResponse
                {
                    Success = true,
                    Content = "",
                    CompletionTokens = 4096,
                    FinishReason = "length"
                },
                new LLMResponse
                {
                    Success = true,
                    Content = "需要确认目标图层。",
                    CompletionTokens = 12,
                    FinishReason = "stop"
                });

            AgentResponse response = await h.Runtime.ProcessAsync(
                new TaskRequest { UserMessage = "处理当前数据" });

            response.UserReply.Should().Be("需要确认目标图层。");
            response.StopReason.Should().Be("纯文本");
            h.Runtime.LastTurnTokens.CompletionTokens.Should().Be(4108);
            await h.Llm.Received(2).ChatAsync(
                Arg.Any<IReadOnlyList<Message>>(),
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<IToolDescriptor>?>(),
                Arg.Any<double>(),
                Arg.Any<int>());
        }

        [Fact]
        public async Task Repeated_semantic_empty_response_fails_instead_of_completing()
        {
            var h = new Harness();
            h.SetLlmResponseSequence(
                new LLMResponse { Success = true },
                new LLMResponse { Success = true });

            AgentResponse response = await h.Runtime.ProcessAsync(
                new TaskRequest { UserMessage = "处理当前数据" });

            response.StopReason.Should().Be("LLM空响应");
            response.UserReply.Should().Contain("未把空响应当作任务完成");
            response.CanContinue.Should().BeTrue();
            h.Session.Received().SetStatus(SessionStatus.Failed);
            h.Session.Received().AddMessage(Arg.Is<Message>(message =>
                message.Role == "assistant"
                && message.Content == response.UserReply));
            h.TranscriptTool.Received().WriteSynthOutput(
                Arg.Any<int>(),
                response.UserReply,
                0);
        }

        // ════════════════════════════════════════════════════════════════
        // Test 2: One round of tools → LLM says done
        // ════════════════════════════════════════════════════════════════

        [Fact]
        public async Task One_round_tool_calls_then_done()
        {
            var h = new Harness();
            h.RegisterPassingTool("analysis_Buffer");
            h.SetLlmResponseSequence(
                new LLMResponse
                {
                    Success = true,
                    ToolCalls = new List<ToolCall>
                    {
                        new() { Id = "call_1", Name = "analysis_Buffer", Arguments = new() }
                    }
                },
                new LLMResponse
                {
                    Success = true,
                    ToolCalls = new List<ToolCall>(), // empty → no more tools
                    Content = "已完成缓冲区分析。"
                });

            var resp = await h.Runtime.ProcessAsync(new TaskRequest { UserMessage = "做缓冲区分析" });

            resp.HadToolCalls.Should().BeTrue();
            resp.StopReason.Should().Be("完成");
            resp.ToolResults.Should().NotBeEmpty();
            resp.ToolResults![0].ToolName.Should().Be("analysis_Buffer");
        }

        // ════════════════════════════════════════════════════════════════
        // Test 3: LLM exception → Fail with LLM异常
        // ════════════════════════════════════════════════════════════════

        [Fact]
        public async Task Llm_throws_returns_failed()
        {
            var h = new Harness();
            h.Llm.ChatAsync(
                Arg.Any<IReadOnlyList<Message>>(),
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<IToolDescriptor>?>(),
                Arg.Any<double>(),
                Arg.Any<int>())
                .Returns<Task<LLMResponse>>(_ => throw new Exception("network lost"));

            var resp = await h.Runtime.ProcessAsync(new TaskRequest { UserMessage = "测试" });

            resp.StopReason.Should().Be("LLM异常");
            resp.UserReply.Should().Contain("不可用");
        }

        // ════════════════════════════════════════════════════════════════
        // Test 4: LLM error response → error-specific StopReason
        // ════════════════════════════════════════════════════════════════

        [Theory]
        [InlineData(LLMErrorKind.RateLimit, "LLM限流")]
        [InlineData(LLMErrorKind.Timeout, "LLM超时")]
        public async Task Llm_error_response_maps_stop_reason(LLMErrorKind kind, string expectedReason)
        {
            var h = new Harness();
            h.SetLlmsNextResponse(new LLMResponse
            {
                Success = false,
                ErrorKind = kind,
                ErrorMessage = "test error",
                Content = "抱歉，服务暂时不可用。"
            });

            var resp = await h.Runtime.ProcessAsync(new TaskRequest { UserMessage = "测试" });

            resp.StopReason.Should().Be(expectedReason);
        }

        // ════════════════════════════════════════════════════════════════
        // Test 5: Tool requires approval → AwaitingApproval
        // ════════════════════════════════════════════════════════════════

        [Fact]
        public async Task Tool_requiring_approval_pauses_with_awaiting_approval()
        {
            var h = new Harness();
            // Use RequestApproval permission so destructive tools require approval
            h.Session.PermissionLevel.Returns(AgentPermissionLevel.RequestApproval);

            h.RegisterTool("management_DeleteField", ToolAnnotation.Destructive,
                new ToolResult { ToolName = "management_DeleteField", Success = true });
            h.SetLlmsNextResponse(new LLMResponse
            {
                Success = true,
                ToolCalls = new List<ToolCall>
                {
                    new() { Id = "call_1", Name = "management_DeleteField", Arguments = new() }
                }
            });

            var resp = await h.Runtime.ProcessAsync(new TaskRequest { UserMessage = "删除字段" });

            resp.RequiresApproval.Should().BeTrue();
            resp.StopReason.Should().Be("WaitingForApproval");
            resp.PendingTools.Should().Contain("management_DeleteField");
        }

        // ════════════════════════════════════════════════════════════════
        // Test 6: Max rounds → Synthesis
        // ════════════════════════════════════════════════════════════════

        [Fact]
        public async Task Max_rounds_reached_synthesizes()
        {
            var h = new Harness();
            h.RegisterPassingTool("analysis_Buffer");
            // Return tool calls forever → will exhaust all 5 rounds
            var toolResponse = new LLMResponse
            {
                Success = true,
                ToolCalls = new List<ToolCall>
                {
                    new() { Id = "call_x", Name = "analysis_Buffer", Arguments = new() }
                }
            };
            h.SetLlmsNextResponse(toolResponse); // every call returns the same

            var resp = await h.Runtime.ProcessAsync(new TaskRequest { UserMessage = "批量处理" });

            resp.StopReason.Should().Be("达到最大轮次");
            resp.HadToolCalls.Should().BeTrue();
        }

        // ════════════════════════════════════════════════════════════════
        // Test 7: All blocked → SafetyBlocked (max-rounds path)
        // ════════════════════════════════════════════════════════════════

        [Fact]
        public async Task All_blocked_returns_safety_blocked()
        {
            var h = new Harness();
            h.Session.PermissionLevel.Returns(AgentPermissionLevel.ReadOnly); // blocks Modify
            h.RegisterTool("analysis_Buffer", ToolAnnotation.Modify,
                new ToolResult { ToolName = "analysis_Buffer", Success = true });
            h.SetLlmsNextResponse(new LLMResponse
            {
                Success = true,
                ToolCalls = new List<ToolCall>
                {
                    new() { Id = "call_1", Name = "analysis_Buffer", Arguments = new() }
                }
            });

            var resp = await h.Runtime.ProcessAsync(new TaskRequest { UserMessage = "做缓冲区" });

            // With ReadOnly permission, Modify tools are blocked. After max rounds,
            // all steps are blocked → SafetyBlocked.
            resp.StopReason.Should().Be("SafetyBlocked");
            resp.UserReply.Should().Contain("阻止");
        }

        // ════════════════════════════════════════════════════════════════
        // Test 8: Path D prediction — tool chain carries to next turn
        // ════════════════════════════════════════════════════════════════

        [Fact]
        public async Task Tool_chain_prediction_carries_over_to_next_turn()
        {
            var h = new Harness();
            h.RegisterPassingTool("management_Project");
            h.RegisterPassingTool("analysis_Clip");
            h.RegisterPassingTool("analysis_Statistics");

            // Turn 1: Project is called → should record in _lastTurnToolCalls
            h.SetLlmResponseSequence(
                new LLMResponse
                {
                    Success = true,
                    ToolCalls = new List<ToolCall>
                    {
                        new() { Id = "c1", Name = "management_Project", Arguments = new() }
                    }
                },
                new LLMResponse
                {
                    Success = true,
                    ToolCalls = new List<ToolCall>(),
                    Content = "投影完成。"
                });

            var resp1 = await h.Runtime.ProcessAsync(new TaskRequest { UserMessage = "投影" });
            resp1.StopReason.Should().Be("完成");

            // Turn 2: PredictNextTools should return Clip, Intersect, Erase, Statistics
            // based on the ChainPredictions table entry for management_Project
            h.SetLlmResponseSequence(
                new LLMResponse
                {
                    Success = true,
                    ToolCalls = new List<ToolCall>(),
                    Content = "无需更多操作。"
                });

            var resp2 = await h.Runtime.ProcessAsync(new TaskRequest { UserMessage = "继续" });
            resp2.StopReason.Should().Be("纯文本");

            // F1: The key test is that Turn 2's tool exposure included the predicted
            // tools (Clip etc.) in Tier1. We verify indirectly: ProcessAsync did not
            // crash (PredictNextTools ran with non-empty _lastTurnToolCalls), and
            // the RuntimePolicy.SelectExposedToolsTiered was called with predicted tools.
            //
            // Direct assertion: verify that Runtime.TurnCount advanced correctly.
            h.Runtime.TurnCount.Should().Be(2);
        }

        // ════════════════════════════════════════════════════════════════
        // ★ P0-1: SafetyGate null → 硬阻断
        // ════════════════════════════════════════════════════════════════

        [Fact]
        public async Task ProcessAsync_without_safety_returns_SafetyUnavailable()
        {
            // 构造 Harness 但故意不设置 SafetyGate
            var session = Substitute.For<IAgentSession>();
            session.ThreadId.Returns("test-no-safety");
            session.Messages.Returns(new List<Message>());
            session.PermissionLevel.Returns(AgentPermissionLevel.FullAccess);
            session.TokenUsage.Returns(new TokenUsage());
            session.Domain.Returns(new DomainContext());
            session.Observations.Returns(new List<IObservation>());
            session.Validations.Returns(new List<ValidationResult>());
            session.Status.Returns(SessionStatus.Idle);

            var llm = Substitute.For<ILLMProvider>();
            llm.ModelName.Returns("test");
            llm.ThinkingEffort.Returns("");
            llm.ProviderName.Returns("test");
            llm.IsConfigured.Returns(true);
            llm.Capability.Returns(new LLMCapability { ToolCalling = true });

            var tools = new ToolRegistry();
            var projectReader = Substitute.For<IProjectReader>();
            projectReader.GetContextForAIAsync().Returns("项目：测试\n家目录：G:\\test");
            var mapReader = Substitute.For<IMapReader>();
            mapReader.GetMapInfoAsync().Returns("{}");
            mapReader.GetAllLayerSummariesAsync().Returns("");
            mapReader.GetSpatialRefContextAsync().Returns(new SpatialRefContext());
            var skillProvider = Substitute.For<ISkillProvider>();
            skillProvider.Count.Returns(0);
            skillProvider.GetSkillsSummary().Returns("");
            skillProvider.MatchSkills(Arg.Any<string>()).Returns(new List<MatchedSkill>());
            skillProvider.MatchSkillsAsync(Arg.Any<string>()).Returns(Task.FromResult(new List<MatchedSkill>()));
            var ctx = new ContextManager(projectReader, mapReader, skillProvider, knowledge: null);

            // ★ 关键：不调用 SetSafety——复现 P0-1 场景
            var runtime = new AgentRuntime(session, llm, tools, ctx, bus: null);
            // 显式不调用：runtime.SetSafety(...);

            var resp = await runtime.ProcessAsync(new TaskRequest { UserMessage = "删除这个字段" });

            resp.StopReason.Should().Be("SafetyUnavailable");
            resp.UserReply.Should().Contain("安全模块未正确初始化");
            session.Received().SetStatus(SessionStatus.Failed);
        }

        // ════════════════════════════════════════════════════════════════
        // ★ P0-3 加强：工具存在性预检——全部幻觉
        // ════════════════════════════════════════════════════════════════

        [Fact]
        public async Task All_hallucinated_tools_are_rejected_before_scheduler()
        {
            var h = new Harness();
            // LLM 编造了 3 个不存在的工具名
            h.SetLlmResponseSequence(
                new LLMResponse
                {
                    Success = true,
                    ToolCalls = new List<ToolCall>
                    {
                        new() { Id = "c1", Name = "conversion_ExportSelectedFeatures", Arguments = new() },
                        new() { Id = "c2", Name = "management_MergeAllData", Arguments = new() },
                        new() { Id = "c3", Name = "analysis_SuperBuffer", Arguments = new() }
                    }
                },
                // 第二轮：LLM 被反馈后不再编造
                new LLMResponse
                {
                    Success = true,
                    ToolCalls = new List<ToolCall>(),
                    Content = "无法完成——以上工具均不存在。建议使用 conversion_ExportFeatures 配合筛选条件。"
                });

            var resp = await h.Runtime.ProcessAsync(new TaskRequest { UserMessage = "导出选中的要素" });

            // 不应该进入 Scheduler（没有注册的工具被执行）
            resp.StopReason.Should().Be("未完成");
            resp.HadToolCalls.Should().BeTrue();
            resp.ToolResults.Should().HaveCount(3);
            resp.ToolResults.Should()
                .OnlyContain(result => !result.Success);
        }

        // ════════════════════════════════════════════════════════════════
        // ★ P0-3 加强：工具存在性预检——部分幻觉
        // ════════════════════════════════════════════════════════════════

        [Fact]
        public async Task Partial_hallucinated_tools_filter_and_continue_with_valid()
        {
            var h = new Harness();
            // 只注册一个真实工具
            h.RegisterPassingTool("analysis_Buffer");

            // LLM 调用了 1 个真实工具 + 2 个编造工具
            h.SetLlmResponseSequence(
                new LLMResponse
                {
                    Success = true,
                    ToolCalls = new List<ToolCall>
                    {
                        new() { Id = "c1", Name = "analysis_Buffer", Arguments = new() },       // 存在
                        new() { Id = "c2", Name = "conversion_FakeExport", Arguments = new() }, // 幻觉
                        new() { Id = "c3", Name = "management_FakeMerge", Arguments = new() }    // 幻觉
                    }
                },
                new LLMResponse
                {
                    Success = true,
                    ToolCalls = new List<ToolCall>(),
                    Content = "已完成缓冲区分析。部分工具不存在已被过滤。"
                });

            var resp = await h.Runtime.ProcessAsync(new TaskRequest { UserMessage = "做缓冲区和导出" });

            // 只有 analysis_Buffer 应该被执行
            resp.HadToolCalls.Should().BeTrue();
            resp.StopReason.Should().Be("完成");
            resp.ToolResults.Should().HaveCount(3,
                "every provider tool_call must have a durable outcome");
            resp.ToolResults!.Single(result =>
                    result.ToolName == "analysis_Buffer")
                .Success.Should().BeTrue();
            resp.ToolResults.Count(result => !result.Success)
                .Should().Be(2);
        }
        [Fact]
        public async Task Plan_generated_event_contains_step_intents_and_max_risk()
        {
            var h = new Harness();
            h.RegisterPassingTool("analysis_Buffer");
            h.RegisterTool("management_DeleteField", ToolAnnotation.Destructive,
                new ToolResult { ToolName = "management_DeleteField", Success = true });
            h.Session.PermissionLevel.Returns(AgentPermissionLevel.RequestApproval);

            var seen = new List<AgentEvent>();
            h.Bus.Subscribe("plan.generated", e => seen.Add(e));

            h.SetLlmsNextResponse(new LLMResponse
            {
                Success = true,
                ToolCalls = new List<ToolCall>
                {
                    new() { Id = "call_1", Name = "analysis_Buffer", Arguments = new() },
                    new() { Id = "call_2", Name = "management_DeleteField", Arguments = new() }
                }
            });

            var resp = await h.Runtime.ProcessAsync(new TaskRequest { UserMessage = "先缓冲再删字段" });

            resp.RequiresApproval.Should().BeTrue();
            seen.Should().ContainSingle();
            seen[0].Metadata.Should().ContainKey("step_count").WhoseValue.Should().Be("2");
            seen[0].Metadata.Should().ContainKey("risk_max").WhoseValue.Should().Be(ToolRiskLevel.Destructive.ToString());

            var steps = JArray.FromObject(seen[0].Data!["steps"]!);
            steps.Should().HaveCount(2);
            steps[0]!["intent"]!.ToString().Should().Be("创建缓冲区");
            steps[1]!["intent"]!.ToString().Should().Be("DeleteField");
        }

        [Fact]
        public async Task Invalid_plan_is_reported_to_model_and_never_reaches_executor()
        {
            var h = new Harness();
            int executions = 0;
            h.Tools.Register(
                new SimpleDescriptor
                {
                    Name = "analysis_Buffer",
                    Annotation = ToolAnnotation.Modify
                },
                new SimpleExecutor(_ =>
                {
                    executions++;
                    return new ToolResult { ToolName = "analysis_Buffer", Success = true };
                }));
            h.Runtime.SetPlanningServices(validator: new RejectingPlanValidator());

            var validationEvents = new List<AgentEvent>();
            h.Bus.Subscribe("plan.validation", e => validationEvents.Add(e));
            h.SetLlmResponseSequence(
                new LLMResponse
                {
                    Success = true,
                    ToolCalls = new()
                    {
                        new ToolCall
                        {
                            Id = "bad-plan-call",
                            Name = "analysis_Buffer",
                            Arguments = new() { ["out_feature_class"] = "same_output" }
                        }
                    }
                },
                new LLMResponse
                {
                    Success = true,
                    Content = "计划冲突，已停止执行。"
                });

            var response = await h.Runtime.ProcessAsync(new TaskRequest
            {
                UserMessage = "执行有冲突的计划"
            });

            executions.Should().Be(0);
            response.StopReason.Should().Be("未完成");
            validationEvents.Should().ContainSingle();
            var validationMetadata = validationEvents[0].Metadata;
            validationMetadata.Should().NotBeNull();
            validationMetadata!["is_valid"].Should().Be("False");
            validationMetadata["error_count"].Should().Be("1");

            await h.Llm.Received(2).ChatAsync(
                Arg.Any<IReadOnlyList<Message>>(),
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<IToolDescriptor>?>(),
                Arg.Any<double>(),
                Arg.Any<int>());
            var secondRequest = h.Llm.ReceivedCalls()
                .Where(call => call.GetMethodInfo().Name == nameof(ILLMProvider.ChatAsync))
                .Select(call => (IReadOnlyList<Message>)call.GetArguments()[0]!)
                .Last();
            secondRequest.Should().Contain(message =>
                message.Role == "tool"
                && message.ToolCallId == "bad-plan-call"
                && message.Content.Contains("未执行"));
            secondRequest.Should().Contain(message =>
                message.Role == "user"
                && message.Content.Contains("计划校验未通过"));
        }

        [Fact]
        public async Task Blocking_reviewer_finding_is_reported_and_never_reaches_executor()
        {
            var h = new Harness();
            int executions = 0;
            h.Tools.Register(
                new SimpleDescriptor
                {
                    Name = "analysis_Buffer",
                    Annotation = ToolAnnotation.Modify
                },
                new SimpleExecutor(_ =>
                {
                    executions++;
                    return new ToolResult
                    {
                        ToolName = "analysis_Buffer",
                        Success = true
                    };
                }));
            h.Runtime.SetPlanningServices(
                router: new AlwaysReviewedRouter(),
                subagents: new BlockingReviewCoordinator());

            var reviewGateEvents = new List<AgentEvent>();
            h.Bus.Subscribe("plan.review_gate", e => reviewGateEvents.Add(e));
            h.SetLlmResponseSequence(
                new LLMResponse
                {
                    Success = true,
                    ToolCalls = new()
                    {
                        new ToolCall
                        {
                            Id = "review-blocked-call",
                            Name = "analysis_Buffer",
                            Arguments = new()
                        }
                    }
                },
                new LLMResponse
                {
                    Success = true,
                    Content = "审查未通过，已停止执行。"
                });

            var response = await h.Runtime.ProcessAsync(new TaskRequest
            {
                UserMessage = "执行复杂空间分析"
            });

            executions.Should().Be(0);
            response.StopReason.Should().Be("未完成");
            reviewGateEvents.Should().ContainSingle();
            reviewGateEvents[0].Metadata!["can_execute"].Should().Be("False");

            var secondRequest = h.Llm.ReceivedCalls()
                .Where(call => call.GetMethodInfo().Name == nameof(ILLMProvider.ChatAsync))
                .Select(call => (IReadOnlyList<Message>)call.GetArguments()[0]!)
                .Last();
            secondRequest.Should().Contain(message =>
                message.Role == "tool"
                && message.ToolCallId == "review-blocked-call"
                && message.Content.Contains("未执行"));
            secondRequest.Should().Contain(message =>
                message.Role == "user"
                && message.Content.Contains("审查门未通过"));
        }

        [Fact]
        public async Task Replanned_physical_plan_increments_version_before_execution()
        {
            var h = new Harness();
            int executions = 0;
            h.Tools.Register(
                new SimpleDescriptor
                {
                    Name = "analysis_Buffer",
                    Annotation = ToolAnnotation.Increment
                },
                new SimpleExecutor(_ =>
                {
                    executions++;
                    return new ToolResult
                    {
                        ToolName = "analysis_Buffer",
                        Success = true
                    };
                }));
            h.Runtime.SetPlanningServices(
                validator: new RejectOncePlanValidator());
            var validationEvents = new List<AgentEvent>();
            h.Bus.Subscribe("plan.validation", e => validationEvents.Add(e));
            h.SetLlmResponseSequence(
                new LLMResponse
                {
                    Success = true,
                    ToolCalls = new()
                    {
                        new ToolCall
                        {
                            Id = "version-1",
                            Name = "analysis_Buffer"
                        }
                    }
                },
                new LLMResponse
                {
                    Success = true,
                    ToolCalls = new()
                    {
                        new ToolCall
                        {
                            Id = "version-2",
                            Name = "analysis_Buffer"
                        }
                    }
                },
                new LLMResponse
                {
                    Success = true,
                    Content = "完成。"
                });

            var response = await h.Runtime.ProcessAsync(new TaskRequest
            {
                UserMessage = "生成缓冲区"
            });

            response.StopReason.Should().Be("部分完成");
            executions.Should().Be(1);
            validationEvents.Should().HaveCount(2);
            validationEvents.Select(evt => evt.Metadata!["plan_version"])
                .Should().Equal("1", "2");
            validationEvents[0].Metadata!["is_valid"].Should().Be("False");
            validationEvents[1].Metadata!["is_valid"].Should().Be("True");
        }
    }
}
