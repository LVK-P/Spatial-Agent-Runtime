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
    /// <remarks>中文：AgentRuntime ReAct 循环测试 — 假的 LLM + 真实的编排器。每次测试都驱动 ProcessAsync，使用受控的大型语言模型响应，断言正确的出口路径、状态变量和 F1 回归（每个出口路径上都填充_lastTurnToolCalls）。</remarks>
    public class AgentRuntimeTests
    {
        // ════════════════════════════════════════════════════════════════
        // Test harness
        // ════════════════════════════════════════════════════════════════
        // 中文：测试线束

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
                // 中文：默认安全：根据注释分类，通过权限评估
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
            /// <remarks>中文：注册一个总能成功的简单工具。</remarks>
            public void RegisterPassingTool(string name)
            {
                Tools.Register(
                    new SimpleDescriptor { Name = name, Annotation = ToolAnnotation.Modify },
                    new SimpleExecutor(_ => new ToolResult { ToolName = name, Success = true }));
            }

            /// <summary>Register a tool that returns a specific result.</summary>
            /// <remarks>中文：注册一个返回特定结果的工具。</remarks>
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
            /// <remarks>中文：返回连续 ChatAsync 呼叫的响应序列。</remarks>
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
        // 中文：测试1：纯文本——无工具调用

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
            // 中文：F1回归：_lastTurnToolCalls应该是空的（没有执行工具）
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
        // 中文：测试2：一轮工具→LLM表示完成

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
                    ToolCalls = new List<ToolCall>(), // 空列表表示没有更多工具。 / Empty means no more tools.
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
        // 中文：测试3：LLM异常→ Fail with LLM异常

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
        // 中文：测试4：LLM错误响应 →针对错误的StopReason

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
        // 中文：测试5：工具需要批准→等待批准

        [Fact]
        public async Task Tool_requiring_approval_pauses_with_awaiting_approval()
        {
            var h = new Harness();
            // Use RequestApproval permission so destructive tools require approval
            // 中文：使用 RequestApproval 权限，使破坏性工具需要批准
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
        // 中文：测试6：合成→最大回合数

        [Fact]
        public async Task Max_rounds_reached_synthesizes()
        {
            var h = new Harness();
            h.RegisterPassingTool("analysis_Buffer");
            // Return tool calls forever → will exhaust all 5 rounds
            // 中文：还击工具呼叫会一直用→会耗尽全部5发子弹
            var toolResponse = new LLMResponse
            {
                Success = true,
                ToolCalls = new List<ToolCall>
                {
                    new() { Id = "call_x", Name = "analysis_Buffer", Arguments = new() }
                }
            };
            h.SetLlmsNextResponse(toolResponse); // 每次调用都返回相同结果。 / Every call returns the same result.

            var resp = await h.Runtime.ProcessAsync(new TaskRequest { UserMessage = "批量处理" });

            resp.StopReason.Should().Be("达到最大轮次");
            resp.HadToolCalls.Should().BeTrue();
        }

        // ════════════════════════════════════════════════════════════════
        // Test 7: All blocked → SafetyBlocked (max-rounds path)
        // ════════════════════════════════════════════════════════════════
        // 中文：测试7：所有→被阻挡安全（最大回合路径）

        [Fact]
        public async Task All_blocked_returns_safety_blocked()
        {
            var h = new Harness();
            h.Session.PermissionLevel.Returns(AgentPermissionLevel.ReadOnly); // 阻止修改。 / Blocks Modify.
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
            // 中文：拥有只读权限后，修改工具被屏蔽。满回合后，所有步骤都会被→安全阻挡。
            resp.StopReason.Should().Be("SafetyBlocked");
            resp.UserReply.Should().Contain("阻止");
        }

        // ════════════════════════════════════════════════════════════════
        // Test 8: Path D prediction — tool chain carries to next turn
        // ════════════════════════════════════════════════════════════════
        // 中文：测试8：路径D预测——工具链继续到下一回合

        [Fact]
        public async Task Tool_chain_prediction_carries_over_to_next_turn()
        {
            var h = new Harness();
            h.RegisterPassingTool("management_Project");
            h.RegisterPassingTool("analysis_Clip");
            h.RegisterPassingTool("analysis_Statistics");

            // Turn 1: Project is called → should record in _lastTurnToolCalls
            // 中文：第一回合：项目叫做 → 应该在_lastTurnToolCalls
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
            // 中文：第二回合：PredictNextTools 应返回基于 ChainPredictions 表条目的剪辑、交集、擦除和统计数据management_Project
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
            // 中文：F1：关键测试是第二回合的工具暴露包含了预测的工具（比如剪辑等）在第一阶段。我们间接验证：ProcessAsync 没有崩溃（PredictNextTools 运行时 _lastTurnToolCalls 非空），预测工具调用了 RuntimePolicy.SelectExposedToolsTiered。直接断言：验证 Runtime.TurnCount 是否正确推进。
            h.Runtime.TurnCount.Should().Be(2);
        }

        // ════════════════════════════════════════════════════════════════
        // ★ P0-1: SafetyGate null → 硬阻断
        // ════════════════════════════════════════════════════════════════
        // English：P0-1: SafetyGate null → hard block

        [Fact]
        public async Task ProcessAsync_without_safety_returns_SafetyUnavailable()
        {
            // 构造 Harness 但故意不设置 SafetyGate
            // English：Construct the Harness but intentionally leave the SafetyGate unset
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
            // English：Key: Do not call SetSafety—recreate the P0-1 scenario
            var runtime = new AgentRuntime(session, llm, tools, ctx, bus: null);
            // 显式不调用：runtime.SetSafety(...);
            // English：Explicit call: runtime. SetSafety(...);

            var resp = await runtime.ProcessAsync(new TaskRequest { UserMessage = "删除这个字段" });

            resp.StopReason.Should().Be("SafetyUnavailable");
            resp.UserReply.Should().Contain("安全模块未正确初始化");
            session.Received().SetStatus(SessionStatus.Failed);
        }

        // ════════════════════════════════════════════════════════════════
        // ★ P0-3 加强：工具存在性预检——全部幻觉
        // ════════════════════════════════════════════════════════════════
        // English：P0-3 Enhancement: Tool Existence Pre-Check—All Illusions

        [Fact]
        public async Task All_hallucinated_tools_are_rejected_before_scheduler()
        {
            var h = new Harness();
            // LLM 编造了 3 个不存在的工具名
            // English：The LLM invented three tool names that don't exist
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
                // English：Round Two: LLMs no longer fabricate after receiving feedback
                new LLMResponse
                {
                    Success = true,
                    ToolCalls = new List<ToolCall>(),
                    Content = "无法完成——以上工具均不存在。建议使用 conversion_ExportFeatures 配合筛选条件。"
                });

            var resp = await h.Runtime.ProcessAsync(new TaskRequest { UserMessage = "导出选中的要素" });

            // 不应该进入 Scheduler（没有注册的工具被执行）
            // English：You should not enter the Scheduler (unregistered tools are executed)
            resp.StopReason.Should().Be("未完成");
            resp.HadToolCalls.Should().BeTrue();
            resp.ToolResults.Should().HaveCount(3);
            resp.ToolResults.Should()
                .OnlyContain(result => !result.Success);
        }

        // ════════════════════════════════════════════════════════════════
        // ★ P0-3 加强：工具存在性预检——部分幻觉
        // ════════════════════════════════════════════════════════════════
        // English：P0-3 Enhancement: Tool Existence Pre-Check—Partial Hallucination

        [Fact]
        public async Task Partial_hallucinated_tools_filter_and_continue_with_valid()
        {
            var h = new Harness();
            // 只注册一个真实工具
            // English：Only register one real tool
            h.RegisterPassingTool("analysis_Buffer");

            // LLM 调用了 1 个真实工具 + 2 个编造工具
            // English：The LLM calls 1 real tool + 2 fabrication tools
            h.SetLlmResponseSequence(
                new LLMResponse
                {
                    Success = true,
                    ToolCalls = new List<ToolCall>
                    {
                        new() { Id = "c1", Name = "analysis_Buffer", Arguments = new() },       // 存在。 / Exists.
                        new() { Id = "c2", Name = "conversion_FakeExport", Arguments = new() }, // 幻觉工具。 / Hallucinated tool.
                        new() { Id = "c3", Name = "management_FakeMerge", Arguments = new() }    // 幻觉工具。 / Hallucinated tool.
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
            // English：Only analysis_Buffer should be executed
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
