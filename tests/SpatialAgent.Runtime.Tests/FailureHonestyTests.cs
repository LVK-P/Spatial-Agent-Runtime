using FluentAssertions;
using NSubstitute;
using SpatialAgent.Runtime;
using SpatialAgent.Runtime.Contracts;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    /// <summary>
    /// "失败不编造"端到端测试 — 验证产品核心可信原则：
    /// 工具失败/验证不通过/安全阻断时，最终回复不会谎称成功。
    ///
    /// 断言策略：语义特征匹配而非精确文本匹配。
    /// LLM 的措辞是非确定性的，但"是否诚实汇报失败"可以通过
    /// 正向（含"失败"/"阻止"等信号）和负向（不含"已完成"/"成功"等
    /// 虚假信号）来验证。
    /// </summary>
    public class FailureHonestyTests
    {
        /// <summary>Setup harness matching AgentRuntimeTests pattern.</summary>
        private static (AgentRuntime runtime, IAgentSession session, ILLMProvider llm, ToolRegistry tools)
            Setup(Action<ToolRegistry, IAgentSession, ILLMProvider> configure)
        {
            var session = Substitute.For<IAgentSession>();
            session.ThreadId.Returns("test-failure");
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
            projectReader.GetContextForAIAsync().Returns("项目：测试项目\n家目录：G:\\test");
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
                        return new SafetyDecision { Kind = SafetyDecisionKind.Block, RiskLevel = risk, ToolName = tool, Reason = "ReadOnly 模式不允许修改操作" };
                    if (perm == AgentPermissionLevel.RequestApproval && risk == ToolRiskLevel.Destructive)
                        return new SafetyDecision { Kind = SafetyDecisionKind.RequireApproval, RiskLevel = risk, ToolName = tool, Reason = "需用户确认" };
                    return new SafetyDecision { Kind = SafetyDecisionKind.Allow, RiskLevel = risk, ToolName = tool };
                });

            configure(tools, session, llm);

            var runtime = new AgentRuntime(session, llm, tools, ctx, bus: null);
            runtime.SetSafety(safety);
            runtime.SetPlanningServices(
                coverageValidator: new SyntheticPlanCoverageValidator());
            return (runtime, session, llm, tools);
        }

        private static void RegisterTool(ToolRegistry tools, string name, ToolAnnotation ann, ToolResult result)
        {
            tools.Register(
                new StubDesc { Name = name, Annotation = ann },
                new StubExec(_ => result));
        }

        private sealed class StubDesc : IToolDescriptor
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

        private sealed class StubExec : IToolExecutor
        {
            private readonly Func<ToolCall, ToolResult> _fn;
            public StubExec(Func<ToolCall, ToolResult> fn) => _fn = fn;
            public Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken ct = default)
                => Task.FromResult(_fn(call));
        }

        private static void SetLlmResponse(ILLMProvider llm, LLMResponse r)
        {
            llm.ChatAsync(
                Arg.Any<IReadOnlyList<Message>>(),
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<IToolDescriptor>?>(),
                Arg.Any<double>(),
                Arg.Any<int>())
                .Returns(r);
        }

        private static void SetLlmSequence(ILLMProvider llm, params LLMResponse[] responses)
        {
            var q = new Queue<LLMResponse>(responses);
            llm.ChatAsync(
                Arg.Any<IReadOnlyList<Message>>(),
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<IToolDescriptor>?>(),
                Arg.Any<double>(),
                Arg.Any<int>())
                .Returns(ci => q.Dequeue());
        }

        // ════════════════════════════════════════════════════════════════
        // F1: 工具执行失败 → 回复中如实汇报失败
        // ════════════════════════════════════════════════════════════════

        [Fact]
        public async Task Tool_execution_failure_is_reported_honestly()
        {
            var (runtime, session, llm, tools) = Setup((t, s, l) =>
            {
                RegisterTool(t, "analysis_Statistics", ToolAnnotation.ReadOnly,
                    new ToolResult { ToolName = "analysis_Statistics", Success = false, Error = "输入图层不存在" });
                SetLlmSequence(l,
                    new LLMResponse
                    {
                        Success = true,
                        ToolCalls = new List<ToolCall>
                        {
                            new() { Id = "c1", Name = "analysis_Statistics", Arguments = new() }
                        }
                    },
                    new LLMResponse
                    {
                        Success = true,
                        ToolCalls = new List<ToolCall>(),
                        Content = "已完成统计分析。" // ← 这是 LLM 可能伪造的回复
                    });
            });

            var resp = await runtime.ProcessAsync(new TaskRequest { UserMessage = "统计" });

            // 语义断言：ExecutionSummary 应如实反映失败状态
            resp.ExecutionSummary.Should().NotBeNull();
            // Summary 中 analysis_Statistics 应当标记为 ❌（失败）而非 ✅
            resp.ExecutionSummary!.Should().Contain("❌");
            resp.ExecutionSummary.Should().NotContain("✅ analysis_Statistics");
            // ★ P1-E：LLM 在工具执行失败后的回复不应与 ExecutionSummary 矛盾。
            // ExecutionSummary 中 analysis_Statistics 标记为 ❌，
            // UserReply 不应声称该工具"成功"或"通过"。
            // 注：LLM 措辞非确定性，"已完成统计分析" 这类模糊表述是已知行为——
            // 当前 "no tool calls → done" 路径直接透传 LLM 原始回复，未经过
            // Synth() 的 failure-aware 合成。这是待修复的结构性问题，但测试
            // 至少确保不出现显式的矛盾声明。
            resp.UserReply.Should().NotContain("分析成功");
            resp.UserReply.Should().NotContain("统计成功");
        }

        [Fact]
        public async Task Final_plaintext_reply_is_overridden_when_steps_failed()
        {
            var (runtime, session, llm, tools) = Setup((t, s, l) =>
            {
                RegisterTool(t, "analysis_Statistics", ToolAnnotation.ReadOnly,
                    new ToolResult { ToolName = "analysis_Statistics", Success = false, Error = "输入图层不存在" });
                SetLlmSequence(l,
                    new LLMResponse
                    {
                        Success = true,
                        ToolCalls = new List<ToolCall>
                        {
                            new() { Id = "c1", Name = "analysis_Statistics", Arguments = new() }
                        }
                    },
                    new LLMResponse
                    {
                        Success = true,
                        ToolCalls = new List<ToolCall>(),
                        Content = "已完成统计分析。"
                    });
            });

            var resp = await runtime.ProcessAsync(new TaskRequest { UserMessage = "统计" });

            resp.HadToolCalls.Should().BeTrue();
            resp.ExecutionSummary.Should().Contain("❌");
            resp.UserReply.Should().Contain("未完成：统计");
            resp.UserReply.Should().Contain("0/1 项成功");
            resp.UserReply.Should().Contain("1 项未成功");
            resp.UserReply.Should().NotContain("已完成统计分析。");
        }

        // ════════════════════════════════════════════════════════════════
        // F2: 部分成功 → 区分完成与未完成
        // ════════════════════════════════════════════════════════════════

        [Fact]
        public async Task Partial_success_distinguishes_completed_from_failed()
        {
            var (runtime, session, llm, tools) = Setup((t, s, l) =>
            {
                RegisterTool(t, "analysis_Buffer", ToolAnnotation.Modify,
                    new ToolResult { ToolName = "analysis_Buffer", Success = true });
                RegisterTool(t, "analysis_Statistics", ToolAnnotation.ReadOnly,
                    new ToolResult { ToolName = "analysis_Statistics", Success = false, Error = "统计失败" });
                SetLlmSequence(l,
                    new LLMResponse
                    {
                        Success = true,
                        ToolCalls = new List<ToolCall>
                        {
                            new() { Id = "c1", Name = "analysis_Buffer", Arguments = new() },
                            new() { Id = "c2", Name = "analysis_Statistics", Arguments = new() }
                        }
                    },
                    new LLMResponse
                    {
                        Success = true,
                        ToolCalls = new List<ToolCall>(),
                        Content = "分析完成。"
                    });
            });

            var resp = await runtime.ProcessAsync(new TaskRequest { UserMessage = "分析" });

            resp.ExecutionSummary.Should().NotBeNull();
            // 成功和失败应同时出现在摘要中
            resp.ExecutionSummary!.Should().Contain("✅");
            resp.ExecutionSummary.Should().Contain("❌");
            // ★ P1-E：UserReply 不应声称全部成功
            resp.UserReply.Should().NotContain("全部完成");
            resp.UserReply.Should().NotContain("所有操作均已成功");
        }

        // ════════════════════════════════════════════════════════════════
        // F3: 安全阻断 → 解释阻断原因，不冒充成功
        // ════════════════════════════════════════════════════════════════

        [Fact]
        public async Task Safety_blocked_is_explained_not_faked()
        {
            var (runtime, session, llm, tools) = Setup((t, s, l) =>
            {
                // ReadOnly permission blocks Modify tools
                s.PermissionLevel.Returns(AgentPermissionLevel.ReadOnly);
                RegisterTool(t, "management_DeleteField", ToolAnnotation.Destructive,
                    new ToolResult { ToolName = "management_DeleteField", Success = true });
                SetLlmSequence(l,
                    new LLMResponse
                    {
                        Success = true,
                        ToolCalls = new List<ToolCall>
                        {
                            new() { Id = "c1", Name = "management_DeleteField", Arguments = new() }
                        }
                    },
                    // After blocking, LLM keeps returning same → exhaust rounds
                    new LLMResponse { Success = true, ToolCalls = new List<ToolCall> { new() { Id = "c2", Name = "management_DeleteField", Arguments = new() } } },
                    new LLMResponse { Success = true, ToolCalls = new List<ToolCall> { new() { Id = "c3", Name = "management_DeleteField", Arguments = new() } } },
                    new LLMResponse { Success = true, ToolCalls = new List<ToolCall> { new() { Id = "c4", Name = "management_DeleteField", Arguments = new() } } },
                    new LLMResponse { Success = true, ToolCalls = new List<ToolCall> { new() { Id = "c5", Name = "management_DeleteField", Arguments = new() } } });
            });

            var resp = await runtime.ProcessAsync(new TaskRequest { UserMessage = "删除字段" });

            // SafetyBlocked means all steps were blocked and agent stops
            resp.StopReason.Should().Be("SafetyBlocked");
            // Should explain blocking, not pretend success
            resp.UserReply.Should().Contain("阻止");
            resp.UserReply.Should().NotContain("已删除");
        }

        // ════════════════════════════════════════════════════════════════
        // F4: 后置验证不通过 → 回复中指出验证失败
        // ════════════════════════════════════════════════════════════════

        [Fact]
        public async Task Verification_failure_is_reported()
        {
            var (runtime, session, llm, tools) = Setup((t, s, l) =>
            {
                RegisterTool(t, "management_CalculateField", ToolAnnotation.Modify,
                    new ToolResult { ToolName = "management_CalculateField", Success = true });

                SetLlmSequence(l,
                    new LLMResponse
                    {
                        Success = true,
                        ToolCalls = new List<ToolCall>
                        {
                            new() { Id = "c1", Name = "management_CalculateField", Arguments = new() }
                        }
                    },
                    new LLMResponse
                    {
                        Success = true,
                        ToolCalls = new List<ToolCall>(),
                        Content = "CalculateField 执行完毕，但后置验证未通过——字段「用地代码」中全部值为空，本次操作未能产生有效结果。"
                    });
            });

            // Inject a failing validator after Setup (runtime is now available)
            var validator = Substitute.For<IValidationRule>();
            validator.Name.Returns("verify_field_populated");
            validator.AppliesTo(Arg.Any<string>(), Arg.Any<IObservation>()).Returns(true);
            validator.ValidateAsync(Arg.Any<IObservation>())
                .Returns(Task.FromResult(new ValidationResult
                {
                    RuleName = "verify_field_populated",
                    Verdict = ValidationVerdict.FAIL,
                    Detail = "字段「用地代码」中 100% 的值为空"
                }));
            runtime.SetValidators(new List<IValidationRule> { validator });

            var resp = await runtime.ProcessAsync(new TaskRequest { UserMessage = "计算字段" });

            // The execution summary should show the validation failure
            resp.ExecutionSummary.Should().NotBeNull();
            // Even if LLM said "已完成", the summary produced by BuildSummary
            // uses StepExecutionResult.Status which accounts for validation.
            // Tool succeeded but validation failed → step status is Failed.
            resp.ExecutionSummary!.Should().Contain("❌");
            // ★ P1-E：LLM 合成回复中不应声称字段计算完成（验证发现 100% 为空 = 数据被清空）
            resp.UserReply.Should().NotContain("字段计算已完成");
            resp.UserReply.Should().NotContain("计算成功");
        }
    }
}
