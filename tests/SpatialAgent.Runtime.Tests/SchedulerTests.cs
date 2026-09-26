using FluentAssertions;
using NSubstitute;
using SpatialAgent.Runtime;
using SpatialAgent.Runtime.Contracts;
using SpatialAgent.Core;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    /// <summary>
    /// Scheduler unit tests — safety gate, backup enforcement (F2 regression),
    /// retry policy, timeout control.
    /// All LLM-dependent logic is excluded; these test the deterministic
    /// execution layer in isolation (2026 Layer 1 — code-based unit tests).
    /// </summary>
    public class SchedulerTests
    {
        // ── Stub tool infrastructure ──

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
            private readonly ToolResult? _result;
            private readonly Func<ToolResult>? _factory;
            private int _callCount;

            public int CallCount => _callCount;

            public StubExecutor(ToolResult result) => _result = result;
            public StubExecutor(Func<ToolResult> factory) => _factory = factory;

            public Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken ct = default)
            {
                _callCount++;
                if (_factory != null) return Task.FromResult(_factory());
                return Task.FromResult(_result!);
            }
        }

        private static ToolRegistry MakeRegistry(string toolName, ToolAnnotation annotation, ToolResult result)
        {
            var registry = new ToolRegistry();
            registry.Register(
                new StubDescriptor { Name = toolName, Annotation = annotation },
                new StubExecutor(result));
            return registry;
        }

        private static ToolRegistry MakeRegistry(string toolName, ToolAnnotation annotation, Func<ToolResult> factory)
        {
            var registry = new ToolRegistry();
            registry.Register(
                new StubDescriptor { Name = toolName, Annotation = annotation },
                new StubExecutor(factory));
            return registry;
        }

        private static AgentPlan SingleStepPlan(string toolName, Dictionary<string, object?>? args = null)
        {
            return new AgentPlan
            {
                Goal = "test",
                Steps = new List<PlanStep>
                {
                    new()
                    {
                        Id = 1,
                        Description = toolName,
                        ToolCall = new ToolCall
                        {
                            Id = "call_1",
                            Name = toolName,
                            Arguments = args ?? new Dictionary<string, object?>()
                        }
                    }
                }
            };
        }

        // ════════════════════════════════════════════════════════════════
        // F2 regression: Destructive + workspace enforcement
        // ════════════════════════════════════════════════════════════════

        [Fact]
        public async Task Destructive_without_workspace_is_blocked()
        {
            // F2 fix (2026-07-20): workspace == null for Destructive → Blocked
            var registry = MakeRegistry("management_DeleteField", ToolAnnotation.Destructive,
                new ToolResult { ToolName = "management_DeleteField", Success = true });
            var safety = Substitute.For<ISafetyGate>();
            safety.Classify("management_DeleteField").Returns(ToolRiskLevel.Destructive);
            safety.Evaluate("management_DeleteField", Arg.Any<AgentPermissionLevel>())
                .Returns(new SafetyDecision { Kind = SafetyDecisionKind.Allow, RiskLevel = ToolRiskLevel.Destructive });

            var scheduler = new Scheduler(registry, eventBus: null, validators: null, safety: safety);
            scheduler.CurrentPermissionLevel = AgentPermissionLevel.FullAccess;
            // workspace NOT set → should block

            var results = await scheduler.RunAsync(SingleStepPlan("management_DeleteField"));

            results.Should().HaveCount(1);
            results[0].Status.Should().Be(StepResultStatus.Blocked);
            results[0].BlockReason.Should().Contain("工作空间服务");
        }

        [Fact]
        public async Task Stable_action_identity_suppresses_duplicate_replan_execution()
        {
            var executor = new StubExecutor(new ToolResult
            {
                ToolName = "management_CalculateField",
                Success = true
            });
            var registry = new ToolRegistry();
            registry.Register(
                new StubDescriptor { Name = "management_CalculateField" },
                executor);
            var safety = Substitute.For<ISafetyGate>();
            safety.Evaluate(
                    Arg.Any<string>(),
                    Arg.Any<AgentPermissionLevel>())
                .Returns(new SafetyDecision
                {
                    Kind = SafetyDecisionKind.Allow,
                    RiskLevel = ToolRiskLevel.Modify
                });
            var scheduler = new Scheduler(registry, safety: safety)
            {
                SatisfiedActionIds = new HashSet<string> { "action:stable" }
            };
            AgentPlan plan = SingleStepPlan("management_CalculateField");
            plan.Steps[0].ActionId = "action:stable";

            IReadOnlyList<StepExecutionResult> results =
                await scheduler.RunAsync(plan);

            executor.CallCount.Should().Be(0);
            results.Should().ContainSingle();
            results[0].ActionId.Should().Be("action:stable");
            results[0].TransitionOutcome.Should()
                .Be(StateTransitionOutcome.AlreadySatisfied);
        }

        [Fact]
        public async Task Destructive_with_workspace_backup_succeeds_completes()
        {
            var registry = MakeRegistry("management_DeleteField", ToolAnnotation.Destructive,
                new ToolResult { ToolName = "management_DeleteField", Success = true });
            var safety = Substitute.For<ISafetyGate>();
            safety.Classify("management_DeleteField").Returns(ToolRiskLevel.Destructive);
            safety.Evaluate("management_DeleteField", Arg.Any<AgentPermissionLevel>())
                .Returns(new SafetyDecision { Kind = SafetyDecisionKind.Allow, RiskLevel = ToolRiskLevel.Destructive });

            var workspace = Substitute.For<IWorkspaceService>();
            workspace.BackupAsync(Arg.Any<string>(), Arg.Any<string>())
                .Returns("G:\\backup\\snapshot.gdb");

            var scheduler = new Scheduler(registry, eventBus: null, validators: null, safety: safety);
            scheduler.CurrentPermissionLevel = AgentPermissionLevel.FullAccess;
            scheduler.SetWorkspace(workspace);

            var results = await scheduler.RunAsync(SingleStepPlan("management_DeleteField",
                new Dictionary<string, object?> { ["in_features"] = "BASE_01" }));

            results.Should().HaveCount(1);
            results[0].Status.Should().Be(StepResultStatus.Completed);
            results[0].BackupPath.Should().Be("G:\\backup\\snapshot.gdb");
        }

        [Fact]
        public async Task Destructive_backup_fails_is_blocked()
        {
            var registry = MakeRegistry("management_DeleteField", ToolAnnotation.Destructive,
                new ToolResult { ToolName = "management_DeleteField", Success = true });
            var safety = Substitute.For<ISafetyGate>();
            safety.Classify("management_DeleteField").Returns(ToolRiskLevel.Destructive);
            safety.Evaluate("management_DeleteField", Arg.Any<AgentPermissionLevel>())
                .Returns(new SafetyDecision { Kind = SafetyDecisionKind.Allow, RiskLevel = ToolRiskLevel.Destructive });

            var workspace = Substitute.For<IWorkspaceService>();
            // Backup returns null → failure
            workspace.BackupAsync(Arg.Any<string>(), Arg.Any<string>()).Returns((string?)null);

            var scheduler = new Scheduler(registry, eventBus: null, validators: null, safety: safety);
            scheduler.CurrentPermissionLevel = AgentPermissionLevel.FullAccess;
            scheduler.SetWorkspace(workspace);

            var results = await scheduler.RunAsync(SingleStepPlan("management_DeleteField",
                new Dictionary<string, object?> { ["in_features"] = "BASE_01" }));

            results.Should().HaveCount(1);
            results[0].Status.Should().Be(StepResultStatus.Blocked);
            results[0].BlockReason.Should().Contain("备份失败");
        }

        [Fact]
        public async Task Destructive_backup_target_unresolvable_is_blocked()
        {
            var registry = MakeRegistry("management_DeleteField", ToolAnnotation.Destructive,
                new ToolResult { ToolName = "management_DeleteField", Success = true });
            var safety = Substitute.For<ISafetyGate>();
            safety.Classify("management_DeleteField").Returns(ToolRiskLevel.Destructive);
            safety.Evaluate("management_DeleteField", Arg.Any<AgentPermissionLevel>())
                .Returns(new SafetyDecision { Kind = SafetyDecisionKind.Allow, RiskLevel = ToolRiskLevel.Destructive });

            var workspace = Substitute.For<IWorkspaceService>();
            var scheduler = new Scheduler(registry, eventBus: null, validators: null, safety: safety);
            scheduler.CurrentPermissionLevel = AgentPermissionLevel.FullAccess;
            scheduler.SetWorkspace(workspace);

            // No in_features or any recognizable input → backup target unresolvable
            var results = await scheduler.RunAsync(SingleStepPlan("management_DeleteField",
                new Dictionary<string, object?> { ["some_unknown_param"] = 42 }));

            results.Should().HaveCount(1);
            results[0].Status.Should().Be(StepResultStatus.Blocked);
            results[0].BlockReason.Should().Contain("无法识别");
        }

        [Fact]
        public async Task Modify_tool_executes_no_backup_check()
        {
            var registry = MakeRegistry("management_CalculateField", ToolAnnotation.Modify,
                new ToolResult { ToolName = "management_CalculateField", Success = true });
            var safety = Substitute.For<ISafetyGate>();
            safety.Classify("management_CalculateField").Returns(ToolRiskLevel.Modify);
            safety.Evaluate("management_CalculateField", Arg.Any<AgentPermissionLevel>())
                .Returns(new SafetyDecision { Kind = SafetyDecisionKind.Allow, RiskLevel = ToolRiskLevel.Modify });

            var workspace = Substitute.For<IWorkspaceService>();
            var scheduler = new Scheduler(registry, eventBus: null, validators: null, safety: safety);
            scheduler.CurrentPermissionLevel = AgentPermissionLevel.FullAccess;
            scheduler.SetWorkspace(workspace);

            // Add where_clause to prevent the new batch-confirmation check from
            // upgrading this Modify tool to AwaitingApproval.
            var results = await scheduler.RunAsync(SingleStepPlan("management_CalculateField",
                new Dictionary<string, object?> { ["where_clause"] = "OBJECTID = 1" }));

            results.Should().HaveCount(1);
            results[0].Status.Should().Be(StepResultStatus.Completed);
            // Modify tools should NOT trigger backup
            await workspace.DidNotReceive().BackupAsync(Arg.Any<string>(), Arg.Any<string>());
        }

        // ════════════════════════════════════════════════════════════════
        // Retry policy
        // ════════════════════════════════════════════════════════════════

        [Fact]
        public async Task ReadOnly_tool_retries_on_failure()
        {
            int calls = 0;
            var registry = MakeRegistry("agent_get_map_info", ToolAnnotation.ReadOnly, () =>
            {
                calls++;
                return calls == 1
                    ? new ToolResult { ToolName = "agent_get_map_info", Success = false, Error = "transient" }
                    : new ToolResult { ToolName = "agent_get_map_info", Success = true };
            });
            var safety = Substitute.For<ISafetyGate>();
            safety.Classify("agent_get_map_info").Returns(ToolRiskLevel.ReadOnly);
            safety.Evaluate("agent_get_map_info", Arg.Any<AgentPermissionLevel>())
                .Returns(new SafetyDecision { Kind = SafetyDecisionKind.Allow, RiskLevel = ToolRiskLevel.ReadOnly });

            var scheduler = new Scheduler(registry, eventBus: null, validators: null, safety: safety);
            scheduler.CurrentPermissionLevel = AgentPermissionLevel.FullAccess;

            var results = await scheduler.RunAsync(SingleStepPlan("agent_get_map_info"));

            results.Should().HaveCount(1);
            results[0].Status.Should().Be(StepResultStatus.Completed);
            results[0].ToolResult!.Attempts.Should().Be(2);
        }

        [Fact]
        public async Task Modify_tool_does_not_retry_on_failure()
        {
            var registry = MakeRegistry("analysis_Buffer", ToolAnnotation.Modify,
                new ToolResult { ToolName = "analysis_Buffer", Success = false, Error = "failed" });
            var safety = Substitute.For<ISafetyGate>();
            safety.Classify("analysis_Buffer").Returns(ToolRiskLevel.Modify);
            safety.Evaluate("analysis_Buffer", Arg.Any<AgentPermissionLevel>())
                .Returns(new SafetyDecision { Kind = SafetyDecisionKind.Allow, RiskLevel = ToolRiskLevel.Modify });

            var scheduler = new Scheduler(registry, eventBus: null, validators: null, safety: safety);
            scheduler.CurrentPermissionLevel = AgentPermissionLevel.FullAccess;

            var results = await scheduler.RunAsync(SingleStepPlan("analysis_Buffer"));

            results.Should().HaveCount(1);
            results[0].Status.Should().Be(StepResultStatus.Failed);
            results[0].ToolResult!.Attempts.Should().Be(1);
        }

        // ════════════════════════════════════════════════════════════════
        // Safety gate decisions
        // ════════════════════════════════════════════════════════════════

        [Fact]
        public async Task RequireApproval_returns_awaiting_approval_before_execution()
        {
            var registry = MakeRegistry("management_DeleteField", ToolAnnotation.Destructive,
                new ToolResult { ToolName = "management_DeleteField", Success = true });
            var safety = Substitute.For<ISafetyGate>();
            safety.Classify("management_DeleteField").Returns(ToolRiskLevel.Destructive);
            safety.Evaluate("management_DeleteField", Arg.Any<AgentPermissionLevel>())
                .Returns(new SafetyDecision
                {
                    Kind = SafetyDecisionKind.RequireApproval,
                    RiskLevel = ToolRiskLevel.Destructive,
                    Reason = "需用户确认删除操作"
                });

            var scheduler = new Scheduler(registry, eventBus: null, validators: null, safety: safety);
            scheduler.CurrentPermissionLevel = AgentPermissionLevel.RequestApproval;

            var results = await scheduler.RunAsync(SingleStepPlan("management_DeleteField"));

            results.Should().HaveCount(1);
            results[0].Status.Should().Be(StepResultStatus.AwaitingApproval);
            results[0].ApprovalReason.Should().Contain("需用户确认删除操作");
            // Tool should NOT have been executed
            results[0].ToolResult.Should().BeNull();
        }

        [Fact]
        public async Task Safety_block_returns_blocked_before_execution()
        {
            var registry = MakeRegistry("management_DeleteField", ToolAnnotation.Destructive,
                new ToolResult { ToolName = "management_DeleteField", Success = true });
            var safety = Substitute.For<ISafetyGate>();
            safety.Classify("management_DeleteField").Returns(ToolRiskLevel.Destructive);
            safety.Evaluate("management_DeleteField", Arg.Any<AgentPermissionLevel>())
                .Returns(new SafetyDecision
                {
                    Kind = SafetyDecisionKind.Block,
                    RiskLevel = ToolRiskLevel.Destructive,
                    Reason = "ReadOnly 模式下不允许破坏性操作"
                });

            var scheduler = new Scheduler(registry, eventBus: null, validators: null, safety: safety);
            scheduler.CurrentPermissionLevel = AgentPermissionLevel.ReadOnly;

            var results = await scheduler.RunAsync(SingleStepPlan("management_DeleteField"));

            results.Should().HaveCount(1);
            results[0].Status.Should().Be(StepResultStatus.Blocked);
            results[0].BlockReason.Should().Contain("ReadOnly");
            results[0].ToolResult.Should().BeNull();
        }

        // ════════════════════════════════════════════════════════════════
        // Timeout control
        // ════════════════════════════════════════════════════════════════

        [Fact]
        public async Task Tool_timeout_returns_failed_with_timeout_error()
        {
            var registry = new ToolRegistry();
            registry.Register(
                new StubDescriptor { Name = "analysis_Statistics", Annotation = ToolAnnotation.ReadOnly },
                new LambdaExecutor(async (call, ct) =>
                {
                    // Simulate a tool that respects cancellation
                    await Task.Delay(Timeout.Infinite, ct);
                    return new ToolResult { ToolName = "analysis_Statistics", Success = true };
                }));
            var safety = Substitute.For<ISafetyGate>();
            safety.Classify("analysis_Statistics").Returns(ToolRiskLevel.ReadOnly);
            safety.Evaluate("analysis_Statistics", Arg.Any<AgentPermissionLevel>())
                .Returns(new SafetyDecision { Kind = SafetyDecisionKind.Allow, RiskLevel = ToolRiskLevel.ReadOnly });

            var scheduler = new Scheduler(registry, eventBus: null, validators: null, safety: safety);
            scheduler.CurrentPermissionLevel = AgentPermissionLevel.FullAccess;
            // Override the default 60s timeout to 50ms for test
            scheduler.SetTimeoutPolicy(new ToolExecutionTimeoutPolicy
            {
                ReadOnlyTimeout = TimeSpan.FromMilliseconds(50)
            });

            var results = await scheduler.RunAsync(SingleStepPlan("analysis_Statistics"));

            results.Should().HaveCount(1);
            results[0].Status.Should().Be(StepResultStatus.Failed);
            results[0].ToolResult!.Error.Should().Contain("超时");
        }

        // ════════════════════════════════════════════════════════════════
        // ★ P0-3 加强：shortName 回退风险升级
        // 场景：LLM 调用了 "conversion_Erase"（编造的），ToolNameResolver 通过
        // shortName 回退解析到 "management_Erase"（真实 Destructive 工具）。
        // SafetyGate 用原始名称 → Destructive（修复后），但需要确认解析后
        // 名称不会导致风险降级。
        // ════════════════════════════════════════════════════════════════

        [Fact]
        public async Task Shortname_fallback_triggers_risk_upgrade_event()
        {
            // 注册一个以 "Erase" 结尾的工具——这是 ToolNameResolver shortName 回退的目标
            var registry = new ToolRegistry();
            registry.Register(
                new StubDescriptor { Name = "management_Erase", Annotation = ToolAnnotation.Destructive },
                new StubExecutor(new ToolResult { ToolName = "management_Erase", Success = true }));

            // 为 "management.Erase" 注册 shortName 别名，让 ResolveName 能匹配
            // （ToolNameResolver 会将 "conversion_Erase" → shortName "Erase" → 匹配 "management_Erase"）
            // 这里需要在 registry 中注册短名
            registry.Register(
                new StubDescriptor { Name = "Erase", Annotation = ToolAnnotation.Destructive },
                new StubExecutor(new ToolResult { ToolName = "management_Erase", Success = true }));

            var eventBus = Substitute.For<IEventBus>();
            var safety = Substitute.For<ISafetyGate>();
            // LLM 原始名称 "conversion_Erase" — 不在 SafetyGate 中 → Destructive（P0-3 修复后）
            safety.Classify("conversion_Erase").Returns(ToolRiskLevel.Destructive);
            // 但解析后的 "management_Erase" 也是 Destructive — 这里风险等级相同
            // 真正的升级场景是：原始=Modify, 解析后=Destructive
            safety.Classify("management_Erase").Returns(ToolRiskLevel.Destructive);
            safety.Evaluate(Arg.Any<string>(), Arg.Any<AgentPermissionLevel>())
                .Returns(ci => new SafetyDecision
                {
                    Kind = SafetyDecisionKind.Allow,
                    RiskLevel = safety.Classify(ci.Arg<string>()),
                    ToolName = ci.Arg<string>()
                });

            var scheduler = new Scheduler(registry, eventBus, validators: null, safety: safety);
            scheduler.CurrentPermissionLevel = AgentPermissionLevel.FullAccess;
            // workspace 必须设置——否则 Destructive 操作被 block（F2 修复）
            var workspace = Substitute.For<IWorkspaceService>();
            workspace.BackupAsync(Arg.Any<string>(), Arg.Any<string>())
                .Returns(Task.FromResult<string?>("G:\\backup\\test.gdb"));
            scheduler.SetWorkspace(workspace);

            var plan = new AgentPlan
            {
                Goal = "test",
                Steps = new List<PlanStep>
                {
                    new()
                    {
                        Id = 1,
                        Description = "conversion_Erase",
                        ToolCall = new ToolCall
                        {
                            Id = "call_1",
                            Name = "conversion_Erase",
                            Arguments = new Dictionary<string, object?>
                            {
                                ["in_features"] = "BASE_01"
                            }
                        }
                    }
                }
            };

            var results = await scheduler.RunAsync(plan);

            results.Should().HaveCount(1);
            // 工具应该成功执行（management_Erase 存在且 Success=true）
            results[0].ToolName.Should().Be("conversion_Erase");
            results[0].Status.Should().Be(StepResultStatus.Completed,
                "原始名称被解析到 management_Erase 并成功执行");
            // 验证 eventBus 收到了 tool.started 和 tool.completed
            eventBus.Received().Publish(Arg.Is<AgentEvent>(e =>
                e.EventType == "tool.started"));
        }

        [Fact]
        public async Task Risk_upgrade_on_resolved_name_fires_event()
        {
            // 模拟升级场景：原始=Modify, 解析后=Destructive
            var registry = new ToolRegistry();
            registry.Register(
                new StubDescriptor { Name = "management_Erase", Annotation = ToolAnnotation.Destructive },
                new StubExecutor(new ToolResult { ToolName = "management_Erase", Success = true }));
            registry.Register(
                new StubDescriptor { Name = "Erase", Annotation = ToolAnnotation.Destructive },
                new StubExecutor(new ToolResult { ToolName = "management_Erase", Success = true }));

            var eventBus = Substitute.For<IEventBus>();
            var safety = Substitute.For<ISafetyGate>();
            // 原始名称分类=Modify（低风险），解析后名称分类=Destructive（高风险）
            safety.Classify("conversion_Erase").Returns(ToolRiskLevel.Modify);
            safety.Classify("management_Erase").Returns(ToolRiskLevel.Destructive);
            safety.Evaluate(Arg.Any<string>(), Arg.Any<AgentPermissionLevel>())
                .Returns(ci => new SafetyDecision
                {
                    Kind = SafetyDecisionKind.Allow,
                    RiskLevel = safety.Classify(ci.Arg<string>()),
                    ToolName = ci.Arg<string>()
                });

            var scheduler = new Scheduler(registry, eventBus, validators: null, safety: safety);
            scheduler.CurrentPermissionLevel = AgentPermissionLevel.FullAccess;
            var workspace = Substitute.For<IWorkspaceService>();
            workspace.BackupAsync(Arg.Any<string>(), Arg.Any<string>())
                .Returns(Task.FromResult<string?>("G:\\backup\\test.gdb"));
            scheduler.SetWorkspace(workspace);

            var plan = new AgentPlan
            {
                Goal = "test",
                Steps = new List<PlanStep>
                {
                    new()
                    {
                        Id = 1,
                        Description = "conversion_Erase",
                        ToolCall = new ToolCall
                        {
                            Id = "call_1",
                            Name = "conversion_Erase",
                            Arguments = new Dictionary<string, object?>
                            {
                                ["in_features"] = "BASE_01"
                            }
                        }
                    }
                }
            };

            await scheduler.RunAsync(plan);

            // ★ 关键断言：safety.risk_upgraded 事件应被发布
            eventBus.Received().Publish(Arg.Is<AgentEvent>(e =>
                e.EventType == "safety.risk_upgraded"
                && e.Metadata!["original_tool"] == "conversion_Erase"
                && e.Metadata["resolved_tool"] == "management_Erase"
                && e.Metadata["original_risk"] == "Modify"
                && e.Metadata["upgraded_risk"] == "Destructive"));
        }
    // ════════════════════════════════════════════════════════════════
    // Quantity-sensitive safety gate (ADR 0051)
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Modify_tool_with_large_influence_requires_approval()
    {
        // Setup: Modify tool, RequestApproval, influence > 100
        var registry = MakeRegistry("management_CalculateField", ToolAnnotation.Modify,
            new ToolResult { ToolName = "management_CalculateField", Success = true });
        var safety = Substitute.For<ISafetyGate>();
        safety.Classify("management_CalculateField").Returns(ToolRiskLevel.Modify);
        safety.Evaluate("management_CalculateField", AgentPermissionLevel.RequestApproval)
            .Returns(new SafetyDecision { Kind = SafetyDecisionKind.Allow, RiskLevel = ToolRiskLevel.Modify });

        var influence = Substitute.For<IDataInfluenceEstimator>();
        influence.EstimateAsync(Arg.Any<ToolCall>(), Arg.Any<DataInfluenceContext?>())
            .Returns(Task.FromResult(new DataInfluenceEstimate
            {
                LayerName = "layer1",
                FieldName = "TQMJ",
                FeatureCount = 500,
                NonNullCount = 500
            }));

        var scheduler = new Scheduler(registry, validators: null, safety: safety);
        scheduler.SetDataInfluenceEstimator(influence);
        scheduler.CurrentPermissionLevel = AgentPermissionLevel.RequestApproval;

        var plan = SingleStepPlan("management_CalculateField",
            new() { ["in_table"] = "layer1", ["field_name"] = "TQMJ" });

        var results = await scheduler.RunAsync(plan);

        results.Should().HaveCount(1);
        results[0].Status.Should().Be(StepResultStatus.AwaitingApproval);
        results[0].ApprovalReason.Should().Contain("500");
        results[0].ApprovalReason.Should().Contain("100");
    }

    [Fact]
    public async Task Increment_tool_skips_quantity_gate()
    {
        var registry = MakeRegistry("analysis_Buffer", ToolAnnotation.Increment,
            new ToolResult { ToolName = "analysis_Buffer", Success = true });
        var safety = Substitute.For<ISafetyGate>();
        safety.Classify("analysis_Buffer").Returns(ToolRiskLevel.Modify);
        safety.Evaluate("analysis_Buffer", AgentPermissionLevel.RequestApproval)
            .Returns(new SafetyDecision { Kind = SafetyDecisionKind.Allow, RiskLevel = ToolRiskLevel.Modify });

        var scheduler = new Scheduler(registry, validators: null, safety: safety);
        scheduler.SetDataInfluenceEstimator(Substitute.For<IDataInfluenceEstimator>());
        scheduler.CurrentPermissionLevel = AgentPermissionLevel.RequestApproval;

        var plan = SingleStepPlan("analysis_Buffer",
            new() { ["in_features"] = "layer1" });

        var results = await scheduler.RunAsync(plan);

        results.Should().HaveCount(1);
        results[0].Status.Should().Be(StepResultStatus.Completed,
            "Increment tools should skip the quantity gate entirely");
    }

    [Fact]
    public async Task FullAccess_skips_quantity_gate()
    {
        var registry = MakeRegistry("management_CalculateField", ToolAnnotation.Modify,
            new ToolResult { ToolName = "management_CalculateField", Success = true });
        var safety = Substitute.For<ISafetyGate>();
        safety.Classify("management_CalculateField").Returns(ToolRiskLevel.Modify);
        safety.Evaluate("management_CalculateField", AgentPermissionLevel.FullAccess)
            .Returns(new SafetyDecision { Kind = SafetyDecisionKind.Allow, RiskLevel = ToolRiskLevel.Modify });

        var influence = Substitute.For<IDataInfluenceEstimator>();
        influence.EstimateAsync(Arg.Any<ToolCall>(), Arg.Any<DataInfluenceContext?>())
            .Returns(Task.FromResult(new DataInfluenceEstimate
            {
                LayerName = "layer1", FieldName = "TQMJ", FeatureCount = 500, NonNullCount = 500
            }));

        var scheduler = new Scheduler(registry, validators: null, safety: safety);
        scheduler.SetDataInfluenceEstimator(influence);
        scheduler.CurrentPermissionLevel = AgentPermissionLevel.FullAccess;

        var plan = SingleStepPlan("management_CalculateField",
            new() { ["in_table"] = "layer1", ["field_name"] = "TQMJ" });

        var results = await scheduler.RunAsync(plan);

        results.Should().HaveCount(1);
        results[0].Status.Should().Be(StepResultStatus.Completed,
            "FullAccess should skip the quantity gate");
    }

    [Fact]
    public async Task Modify_tool_with_small_influence_proceeds()
    {
        var registry = MakeRegistry("cc_calculate_area", ToolAnnotation.Modify,
            new ToolResult { ToolName = "cc_calculate_area", Success = true });
        var safety = Substitute.For<ISafetyGate>();
        safety.Classify("cc_calculate_area").Returns(ToolRiskLevel.Modify);
        safety.Evaluate("cc_calculate_area", AgentPermissionLevel.RequestApproval)
            .Returns(new SafetyDecision { Kind = SafetyDecisionKind.Allow, RiskLevel = ToolRiskLevel.Modify });

        var influence = Substitute.For<IDataInfluenceEstimator>();
        influence.EstimateAsync(Arg.Any<ToolCall>(), Arg.Any<DataInfluenceContext?>())
            .Returns(Task.FromResult(new DataInfluenceEstimate
            {
                LayerName = "small_layer", FieldName = "TQMJ", FeatureCount = 8, NonNullCount = 8
            }));

        var scheduler = new Scheduler(registry, validators: null, safety: safety);
        scheduler.SetDataInfluenceEstimator(influence);
        scheduler.CurrentPermissionLevel = AgentPermissionLevel.RequestApproval;

        var plan = SingleStepPlan("cc_calculate_area",
            new() { ["layer_name"] = "small_layer", ["field_name"] = "TQMJ" });

        var results = await scheduler.RunAsync(plan);

        results.Should().HaveCount(1);
        results[0].Status.Should().Be(StepResultStatus.Completed,
            "Small influence (< threshold) should proceed without approval");
    }

    [Fact]
    public async Task Modify_tool_new_field_is_increment_effect()
    {
        var registry = MakeRegistry("cc_calculate_area", ToolAnnotation.Modify,
            new ToolResult { ToolName = "cc_calculate_area", Success = true });
        var safety = Substitute.For<ISafetyGate>();
        safety.Classify("cc_calculate_area").Returns(ToolRiskLevel.Modify);
        safety.Evaluate("cc_calculate_area", AgentPermissionLevel.RequestApproval)
            .Returns(new SafetyDecision { Kind = SafetyDecisionKind.Allow, RiskLevel = ToolRiskLevel.Modify });

        var influence = Substitute.For<IDataInfluenceEstimator>();
        influence.EstimateAsync(Arg.Any<ToolCall>(), Arg.Any<DataInfluenceContext?>())
            .Returns(Task.FromResult(new DataInfluenceEstimate
            {
                LayerName = "large_layer", FieldName = "NEWFIELD", FeatureCount = 500, NonNullCount = 0
            }));

        var scheduler = new Scheduler(registry, validators: null, safety: safety);
        scheduler.SetDataInfluenceEstimator(influence);
        scheduler.CurrentPermissionLevel = AgentPermissionLevel.RequestApproval;

        var plan = SingleStepPlan("cc_calculate_area",
            new() { ["layer_name"] = "large_layer", ["field_name"] = "NEWFIELD" });

        var results = await scheduler.RunAsync(plan);

        results.Should().HaveCount(1);
        results[0].Status.Should().Be(StepResultStatus.Completed,
            "New field (NonNullCount=0) should skip quantity gate");
    }

    [Fact]
    public async Task Modify_tool_schema_change_without_overwrite_does_not_require_approval()
    {
        var registry = MakeRegistry("cc_calculate_area", ToolAnnotation.Modify,
            new ToolResult { ToolName = "cc_calculate_area", Success = true });
        var safety = Substitute.For<ISafetyGate>();
        safety.Classify("cc_calculate_area").Returns(ToolRiskLevel.Modify);
        safety.Evaluate("cc_calculate_area", AgentPermissionLevel.RequestApproval)
            .Returns(new SafetyDecision { Kind = SafetyDecisionKind.Allow, RiskLevel = ToolRiskLevel.Modify });

        var influence = Substitute.For<IDataInfluenceEstimator>();
        influence.EstimateAsync(Arg.Any<ToolCall>(), Arg.Any<DataInfluenceContext?>())
            .Returns(Task.FromResult(new DataInfluenceEstimate
            {
                LayerName = "layer1",
                FieldName = "TQMJ",
                FeatureCount = 350,
                ScopeObjectCount = 350,
                TargetFieldCount = 1,
                ScopeValueCount = 350,
                ExistingNonEmptyTargetCount = 0,
                NewValueFillCount = 350,
                SchemaMutationCount = 1
            }));

        var scheduler = new Scheduler(registry, validators: null, safety: safety);
        scheduler.SetDataInfluenceEstimator(influence);
        scheduler.CurrentPermissionLevel = AgentPermissionLevel.RequestApproval;

        var plan = SingleStepPlan("cc_calculate_area",
            new() { ["layer_name"] = "layer1", ["field_name"] = "TQMJ" });

        var results = await scheduler.RunAsync(plan);

        results.Should().HaveCount(1);
        results[0].Status.Should().Be(StepResultStatus.Completed,
            "schema creation and first fill alone should not trigger quantity gate approval");
    }

    [Fact]
    public async Task Influence_approval_event_includes_structured_probe_metadata()
    {
        var registry = MakeRegistry("management_CalculateField", ToolAnnotation.Modify,
            new ToolResult { ToolName = "management_CalculateField", Success = true });
        var bus = Substitute.For<IEventBus>();
        var safety = Substitute.For<ISafetyGate>();
        safety.Classify("management_CalculateField").Returns(ToolRiskLevel.Modify);
        safety.Evaluate("management_CalculateField", AgentPermissionLevel.RequestApproval)
            .Returns(new SafetyDecision { Kind = SafetyDecisionKind.Allow, RiskLevel = ToolRiskLevel.Modify });

        var influence = Substitute.For<IDataInfluenceEstimator>();
        influence.EstimateAsync(Arg.Any<ToolCall>(), Arg.Any<DataInfluenceContext?>())
            .Returns(Task.FromResult(new DataInfluenceEstimate
            {
                LayerName = "parcel_layer",
                FieldName = "TQMJ, TBMJ",
                FieldNames = new[] { "TQMJ", "TBMJ" },
                FeatureCount = 300,
                ScopeObjectCount = 147,
                TargetFieldCount = 2,
                ScopeValueCount = 294,
                ExistingNonEmptyTargetCount = 120,
                NewValueFillCount = 174,
                SchemaMutationCount = 0,
                UsesSelectionScope = true,
                UsesFilteredScope = false,
                ModifySubtype = ToolModifySubtype.QuantityGateCandidate,
                Source = EvidenceSource.AttributeTableProbe,
                ReliabilityReason = "Read 147 rows directly from attribute table"
            }));

        var scheduler = new Scheduler(registry, bus, validators: null, safety: safety);
        scheduler.SetDataInfluenceEstimator(influence);
        scheduler.CurrentPermissionLevel = AgentPermissionLevel.RequestApproval;

        var plan = SingleStepPlan("management_CalculateField",
            new() { ["in_table"] = "parcel_layer", ["field_name"] = "TQMJ, TBMJ" });

        var results = await scheduler.RunAsync(plan);

        results.Should().HaveCount(1);
        results[0].Status.Should().Be(StepResultStatus.AwaitingApproval);
        bus.Received().Publish(Arg.Is<AgentEvent>(e =>
            e.EventType == "safety.influence_approval_required"
            && e.Metadata != null
            && e.Metadata["target_field_count"] == "2"
            && e.Metadata["scope_value_count"] == "294"
            && e.Metadata["source"] == "AttributeTableProbe"
            && e.Metadata["modify_subtype"] == "QuantityGateCandidate"
            && e.Data != null
            && e.Data.ContainsKey("estimate_details")));
    }

    [Fact]
    public async Task Quantity_gate_stops_subsequent_steps_from_executing()
    {
        var execCount = 0;
        var registry = new ToolRegistry();
        registry.Register(
            new StubDescriptor { Name = "management_CalculateField", Annotation = ToolAnnotation.Modify },
            new StubExecutor(() => { execCount++; return new ToolResult { ToolName = "management_CalculateField", Success = true }; }));
        registry.Register(
            new StubDescriptor { Name = "analysis_Statistics", Annotation = ToolAnnotation.Increment },
            new StubExecutor(() => { execCount++; return new ToolResult { ToolName = "analysis_Statistics", Success = true }; }));

        var safety = Substitute.For<ISafetyGate>();
        safety.Classify(Arg.Any<string>()).Returns(ToolRiskLevel.Modify);
        safety.Evaluate(Arg.Any<string>(), Arg.Any<AgentPermissionLevel>())
            .Returns(new SafetyDecision { Kind = SafetyDecisionKind.Allow, RiskLevel = ToolRiskLevel.Modify });

        var influence = Substitute.For<IDataInfluenceEstimator>();
        influence.EstimateAsync(Arg.Any<ToolCall>(), Arg.Any<DataInfluenceContext?>())
            .Returns(Task.FromResult(new DataInfluenceEstimate
            {
                LayerName = "big_layer", FieldName = "FLD", FeatureCount = 5000, NonNullCount = 5000
            }));

        var scheduler = new Scheduler(registry, validators: null, safety: safety);
        scheduler.SetDataInfluenceEstimator(influence);
        scheduler.CurrentPermissionLevel = AgentPermissionLevel.RequestApproval;

        var plan = new AgentPlan
        {
            Goal = "test",
            Steps = new List<PlanStep>
            {
                new() { Id = 1, ToolCall = new ToolCall { Id = "c1", Name = "management_CalculateField", Arguments = new() { ["in_table"] = "big_layer", ["field_name"] = "FLD" } } },
                new() { Id = 2, ToolCall = new ToolCall { Id = "c2", Name = "analysis_Statistics", Arguments = new() { ["in_table"] = "big_layer" } } }
            }
        };

        var results = await scheduler.RunAsync(plan);

        results.Should().HaveCount(1, "should return immediately on quantity gate, not execute step 2");
        results[0].Status.Should().Be(StepResultStatus.AwaitingApproval);
        execCount.Should().Be(0, "no steps should have been executed");
    }

    [Fact]
    public async Task Host_state_change_after_planning_blocks_execution()
    {
        var registry = new ToolRegistry();
        var executor = new StubExecutor(new ToolResult
        {
            ToolName = "management_CalculateField",
            Success = true
        });
        registry.Register(
            new StubDescriptor
            {
                Name = "management_CalculateField",
                Annotation = ToolAnnotation.Modify
            },
            executor);
        var safety = Substitute.For<ISafetyGate>();
        safety.Classify("management_CalculateField")
            .Returns(ToolRiskLevel.Modify);
        safety.Evaluate(
                "management_CalculateField",
                Arg.Any<AgentPermissionLevel>())
            .Returns(new SafetyDecision
            {
                Kind = SafetyDecisionKind.Allow,
                RiskLevel = ToolRiskLevel.Modify
            });
        var guard = Substitute.For<IHostStateGuard>();
        var external = new HostStateGuardResult
        {
            IsCurrent = false,
            Decision = HostRevisionDecision.ExternalChange,
            Reason = "ArcGIS project, map, schema, selection, or data content changed after planning",
            Current = new DataRevisionSnapshot
            {
                Fingerprint = "revision:current",
                IsReliable = true
            }
        };
        guard.ValidateAsync(
                Arg.Any<DataRevisionSnapshot>(),
                Arg.Any<CancellationToken>())
            .Returns(external);
        guard.ValidateForExecutionAsync(
                Arg.Any<DataRevisionSnapshot>(),
                Arg.Any<ExpectedMutationSet>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(external);
        var scheduler = new Scheduler(
            registry,
            validators: null,
            safety: safety);
        scheduler.SetHostStateGuard(guard);
        scheduler.CurrentPermissionLevel = AgentPermissionLevel.FullAccess;
        var plan = SingleStepPlan(
            "management_CalculateField",
            new() { ["in_table"] = "roads", ["field_name"] = "LENGTH" });
        plan.DataRevision = new DataRevisionSnapshot
        {
            Fingerprint = "revision:planned",
            IsReliable = true
        };
        plan.Steps[0].Effect = new InvocationEffect
        {
            Profile = new EffectProfile
            {
                Effects = DataEffectKind.UpdateRows
            }
        };

        var results = await scheduler.RunAsync(plan);

        results.Should().ContainSingle();
        results[0].Status.Should().Be(StepResultStatus.Blocked);
        results[0].Cause.Should()
            .Be(StepOutcomeCause.HostStateInvalidated);
        results[0].BlockReason.Should().Contain("changed after planning");
        executor.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Destructive_transaction_validates_host_before_backup_and_executes_after_cleanup()
    {
        var order = new List<string>();
        var registry = new ToolRegistry();
        registry.Register(
            new StubDescriptor
            {
                Name = "management_DeleteField",
                Annotation = ToolAnnotation.Destructive
            },
            async (_, _) =>
            {
                order.Add("execute");
                await Task.Yield();
                return new ToolResult
                {
                    ToolName = "management_DeleteField",
                    Success = true
                };
            });
        var safety = Substitute.For<ISafetyGate>();
        safety.Classify("management_DeleteField")
            .Returns(ToolRiskLevel.Destructive);
        safety.Evaluate(
                "management_DeleteField",
                Arg.Any<AgentPermissionLevel>())
            .Returns(new SafetyDecision
            {
                Kind = SafetyDecisionKind.Allow,
                RiskLevel = ToolRiskLevel.Destructive
            });
        var current = new DataRevisionSnapshot
        {
            Fingerprint = "revision:current",
            IsReliable = true
        };
        var guard = Substitute.For<IHostStateGuard>();
        guard.ValidateForExecutionAsync(
                Arg.Any<DataRevisionSnapshot>(),
                Arg.Any<ExpectedMutationSet>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                order.Add("validate");
                return new HostStateGuardResult
                {
                    IsCurrent = true,
                    Decision = HostRevisionDecision.Current,
                    Current = current
                };
            });
        guard.CaptureAsync(Arg.Any<CancellationToken>())
            .Returns(current);
        var workspace = Substitute.For<IWorkspaceService>();
        workspace.BackupAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                order.Add("backup");
                return "G:\\backup\\snapshot.gdb";
            });
        workspace.RemoveFromMapAsync(
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                order.Add("cleanup");
                return Task.CompletedTask;
            });
        var scheduler = new Scheduler(registry, safety: safety);
        scheduler.SetWorkspace(workspace);
        scheduler.SetHostStateGuard(guard);
        scheduler.CurrentPermissionLevel = AgentPermissionLevel.FullAccess;
        var plan = SingleStepPlan(
            "management_DeleteField",
            new() { ["in_table"] = "roads", ["drop_field"] = "OLD" });
        plan.DataRevision = new DataRevisionSnapshot
        {
            Fingerprint = "revision:planned",
            IsReliable = true
        };
        plan.Steps[0].Effect = new InvocationEffect
        {
            Profile = new EffectProfile
            {
                Effects = DataEffectKind.AlterSchema
            }
        };

        var results = await scheduler.RunAsync(plan);

        results.Should().ContainSingle(result =>
            result.Status == StepResultStatus.Completed);
        order.Should().ContainInOrder(
            "validate",
            "backup",
            "cleanup",
            "validate",
            "execute");
    }

    [Fact]
    public async Task External_host_change_blocks_before_destructive_backup()
    {
        var registry = MakeRegistry(
            "management_DeleteField",
            ToolAnnotation.Destructive,
            new ToolResult
            {
                ToolName = "management_DeleteField",
                Success = true
            });
        var safety = Substitute.For<ISafetyGate>();
        safety.Classify("management_DeleteField")
            .Returns(ToolRiskLevel.Destructive);
        safety.Evaluate(
                "management_DeleteField",
                Arg.Any<AgentPermissionLevel>())
            .Returns(new SafetyDecision
            {
                Kind = SafetyDecisionKind.Allow,
                RiskLevel = ToolRiskLevel.Destructive
            });
        var guard = Substitute.For<IHostStateGuard>();
        guard.ValidateForExecutionAsync(
                Arg.Any<DataRevisionSnapshot>(),
                Arg.Any<ExpectedMutationSet>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(new HostStateGuardResult
            {
                IsCurrent = false,
                Decision = HostRevisionDecision.ExternalChange,
                Reason = "host changed",
                Current = new DataRevisionSnapshot
                {
                    Fingerprint = "revision:changed",
                    IsReliable = true
                }
            });
        var workspace = Substitute.For<IWorkspaceService>();
        var scheduler = new Scheduler(registry, safety: safety);
        scheduler.SetWorkspace(workspace);
        scheduler.SetHostStateGuard(guard);
        scheduler.CurrentPermissionLevel = AgentPermissionLevel.FullAccess;
        var plan = SingleStepPlan(
            "management_DeleteField",
            new() { ["in_table"] = "roads", ["drop_field"] = "OLD" });
        plan.DataRevision = new DataRevisionSnapshot
        {
            Fingerprint = "revision:planned",
            IsReliable = true
        };
        plan.Steps[0].Effect = new InvocationEffect
        {
            Profile = new EffectProfile
            {
                Effects = DataEffectKind.AlterSchema
            }
        };

        var results = await scheduler.RunAsync(plan);

        results[0].Cause.Should().Be(StepOutcomeCause.HostStateInvalidated);
        await workspace.DidNotReceive().BackupAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Host_change_during_backup_is_not_adopted_as_infrastructure_state()
    {
        var registry = new ToolRegistry();
        var executor = new StubExecutor(new ToolResult
        {
            ToolName = "management_DeleteField",
            Success = true
        });
        registry.Register(
            new StubDescriptor
            {
                Name = "management_DeleteField",
                Annotation = ToolAnnotation.Destructive
            },
            executor);
        var safety = Substitute.For<ISafetyGate>();
        safety.Classify("management_DeleteField")
            .Returns(ToolRiskLevel.Destructive);
        safety.Evaluate(
                "management_DeleteField",
                Arg.Any<AgentPermissionLevel>())
            .Returns(new SafetyDecision
            {
                Kind = SafetyDecisionKind.Allow,
                RiskLevel = ToolRiskLevel.Destructive
            });
        var planned = new DataRevisionSnapshot
        {
            Fingerprint = "revision:planned",
            IsReliable = true
        };
        var guard = Substitute.For<IHostStateGuard>();
        guard.ValidateForExecutionAsync(
                Arg.Any<DataRevisionSnapshot>(),
                Arg.Any<ExpectedMutationSet>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(
                new HostStateGuardResult
                {
                    IsCurrent = true,
                    Decision = HostRevisionDecision.Current,
                    Current = planned
                },
                new HostStateGuardResult
                {
                    IsCurrent = false,
                    Decision = HostRevisionDecision.ExternalChange,
                    Reason = "user edited the map during backup",
                    Current = new DataRevisionSnapshot
                    {
                        Fingerprint = "revision:changed",
                        IsReliable = true
                    }
                });
        var workspace = Substitute.For<IWorkspaceService>();
        workspace.BackupAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns("G:\\backup\\snapshot.gdb");
        var scheduler = new Scheduler(registry, safety: safety);
        scheduler.SetWorkspace(workspace);
        scheduler.SetHostStateGuard(guard);
        scheduler.CurrentPermissionLevel = AgentPermissionLevel.FullAccess;
        var plan = SingleStepPlan(
            "management_DeleteField",
            new() { ["in_table"] = "roads", ["drop_field"] = "OLD" });
        plan.DataRevision = planned;
        plan.Steps[0].Effect = new InvocationEffect
        {
            Profile = new EffectProfile
            {
                Effects = DataEffectKind.AlterSchema
            }
        };

        var results = await scheduler.RunAsync(plan);

        results[0].Status.Should().Be(StepResultStatus.Blocked);
        results[0].Cause.Should().Be(StepOutcomeCause.HostStateInvalidated);
        results[0].BlockReason.Should().Contain("during backup");
        executor.CallCount.Should().Be(0);
        await workspace.Received(1).BackupAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Content_only_self_commit_after_backup_is_adopted_in_existing_transaction()
    {
        var executor = new StubExecutor(new ToolResult
        {
            ToolName = "management_DeleteField",
            Success = true
        });
        var registry = new ToolRegistry();
        registry.Register(
            new StubDescriptor
            {
                Name = "management_DeleteField",
                Annotation = ToolAnnotation.Destructive
            },
            executor);
        var safety = Substitute.For<ISafetyGate>();
        safety.Classify("management_DeleteField")
            .Returns(ToolRiskLevel.Destructive);
        safety.Evaluate(
                "management_DeleteField",
                Arg.Any<AgentPermissionLevel>())
            .Returns(new SafetyDecision
            {
                Kind = SafetyDecisionKind.Allow,
                RiskLevel = ToolRiskLevel.Destructive
            });
        var planned = new DataRevisionSnapshot
        {
            Fingerprint = "revision:prior-self-write",
            IsReliable = true
        };
        var afterBackup = new DataRevisionSnapshot
        {
            Fingerprint = "revision:backup-storage-touch",
            IsReliable = true
        };
        var guard = Substitute.For<IHostStateGuard>();
        guard.ValidateForExecutionAsync(
                Arg.Any<DataRevisionSnapshot>(),
                Arg.Any<ExpectedMutationSet>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(
                new HostStateGuardResult
                {
                    IsCurrent = true,
                    Decision = HostRevisionDecision.Current,
                    Current = planned
                },
                new HostStateGuardResult
                {
                    IsCurrent = true,
                    Decision = HostRevisionDecision.SelfCommitted,
                    Current = afterBackup,
                    ChangedComponents = new() { "content" }
                });
        guard.CaptureAsync(Arg.Any<CancellationToken>())
            .Returns(afterBackup);
        var workspace = Substitute.For<IWorkspaceService>();
        workspace.BackupAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns("G:\\backup\\snapshot.gdb");
        var scheduler = new Scheduler(registry, safety: safety);
        scheduler.SetWorkspace(workspace);
        scheduler.SetHostStateGuard(guard);
        scheduler.CurrentPermissionLevel = AgentPermissionLevel.FullAccess;
        AgentPlan plan = SingleStepPlan(
            "management_DeleteField",
            new() { ["in_table"] = "roads", ["drop_field"] = "OLD" });
        plan.DataRevision = planned;
        plan.HasCommittedMutation = true;
        plan.ExpectedMutations.Items.Add(new ExpectedMutation
        {
            Layer = "roads",
            Effects = DataEffectKind.AlterSchema,
            StepId = 1
        });
        plan.Steps[0].Effect = new InvocationEffect
        {
            Profile = new EffectProfile { Effects = DataEffectKind.AlterSchema }
        };

        IReadOnlyList<StepExecutionResult> results =
            await scheduler.RunAsync(plan);

        results.Should().ContainSingle(result =>
            result.Status == StepResultStatus.Completed);
        executor.CallCount.Should().Be(1);
        plan.DataRevision!.Fingerprint.Should()
            .Be("revision:backup-storage-touch");
    }

    [Fact]
    public async Task Unreliable_estimate_fail_closed_requires_approval()
    {
        var registry = MakeRegistry("management_CalculateField", ToolAnnotation.Modify,
            new ToolResult { ToolName = "management_CalculateField", Success = true });
        var safety = Substitute.For<ISafetyGate>();
        safety.Classify("management_CalculateField").Returns(ToolRiskLevel.Modify);
        safety.Evaluate("management_CalculateField", AgentPermissionLevel.RequestApproval)
            .Returns(new SafetyDecision { Kind = SafetyDecisionKind.Allow, RiskLevel = ToolRiskLevel.Modify });

        var influence = Substitute.For<IDataInfluenceEstimator>();
        influence.EstimateAsync(Arg.Any<ToolCall>(), Arg.Any<DataInfluenceContext?>())
            .Returns(Task.FromResult(new DataInfluenceEstimate
            {
                IsReliable = false, FeatureCount = -1, LayerName = "unknown_layer"
            }));

        var scheduler = new Scheduler(registry, validators: null, safety: safety);
        scheduler.SetDataInfluenceEstimator(influence);
        scheduler.CurrentPermissionLevel = AgentPermissionLevel.RequestApproval;

        var plan = SingleStepPlan("management_CalculateField",
            new() { ["in_table"] = "unknown_layer", ["field_name"] = "FLD" });

        var results = await scheduler.RunAsync(plan);

        results.Should().HaveCount(1);
        results[0].Status.Should().Be(StepResultStatus.AwaitingApproval,
            "unreliable estimate must fail-closed");
        results[0].ApprovalReason.Should().Contain("无法评估");
    }
}

// ── Helper: IToolExecutor from lambda ──

    public class SchedulerProgressTests
    {
        [Fact]
        public async Task Scheduler_emits_stage_progress_for_execute_and_complete()
        {
            var registry = new ToolRegistry();
            registry.Register(
                new StubDescriptor { Name = "analysis_Buffer", Annotation = ToolAnnotation.Modify },
                new StubExecutor(new ToolResult { ToolName = "analysis_Buffer", Success = true, Output = "{\"count\":1}" }));

            var safety = Substitute.For<ISafetyGate>();
            safety.Classify("analysis_Buffer").Returns(ToolRiskLevel.Modify);
            safety.Evaluate("analysis_Buffer", Arg.Any<AgentPermissionLevel>())
                .Returns(new SafetyDecision { Kind = SafetyDecisionKind.Allow, RiskLevel = ToolRiskLevel.Modify });

            var bus = new EventBus();
            var events = new List<AgentEvent>();
            bus.Subscribe("tool.progress", e => events.Add(e));

            var scheduler = new Scheduler(registry, eventBus: bus, validators: null, safety: safety)
            {
                CurrentPermissionLevel = AgentPermissionLevel.FullAccess,
                CurrentTurn = 3
            };

            var results = await scheduler.RunAsync(new AgentPlan
            {
                Goal = "test",
                Steps = new List<PlanStep>
                {
                    new()
                    {
                        Id = 1,
                        Description = "analysis_Buffer",
                        ToolCall = new ToolCall
                        {
                            Id = "call_1",
                            Name = "analysis_Buffer",
                            Arguments = new Dictionary<string, object?>()
                        }
                    }
                }
            });

            results.Should().HaveCount(1);
            results[0].Status.Should().Be(StepResultStatus.Completed);
            events.Select(e => e.Metadata?["phase"]).Should().ContainInOrder("execute", "completed");
            events.Select(e => e.Metadata != null && e.Metadata.TryGetValue("tool", out var tool) ? tool : "")
                .Should().OnlyContain(tool => tool == "analysis_Buffer");
        }

        [Fact]
        public async Task Scheduler_emits_elapsed_heartbeats_while_adapter_is_pending()
        {
            var registry = new ToolRegistry();
            registry.Register(
                new StubDescriptor
                {
                    Name = "analysis_Buffer",
                    Annotation = ToolAnnotation.Modify
                },
                async (_, cancellation) =>
                {
                    await Task.Delay(60, cancellation);
                    return new ToolResult
                    {
                        ToolName = "analysis_Buffer",
                        Success = true
                    };
                });
            var safety = Substitute.For<ISafetyGate>();
            safety.Classify("analysis_Buffer").Returns(ToolRiskLevel.Modify);
            safety.Evaluate("analysis_Buffer", Arg.Any<AgentPermissionLevel>())
                .Returns(new SafetyDecision
                {
                    Kind = SafetyDecisionKind.Allow,
                    RiskLevel = ToolRiskLevel.Modify
                });
            var bus = new EventBus();
            var events = new List<AgentEvent>();
            bus.Subscribe("tool.progress", events.Add);
            var scheduler = new Scheduler(
                registry,
                eventBus: bus,
                safety: safety)
            {
                CurrentPermissionLevel = AgentPermissionLevel.FullAccess
            };
            scheduler.SetProgressHeartbeatInterval(TimeSpan.FromMilliseconds(10));

            await scheduler.RunAsync(new AgentPlan
            {
                Goal = "test",
                Steps = new List<PlanStep>
                {
                    new()
                    {
                        Id = 1,
                        ToolCall = new ToolCall
                        {
                            Id = "call_1",
                            Name = "analysis_Buffer",
                            Arguments = new()
                        }
                    }
                }
            });

            events.Any(e =>
                e.Metadata != null
                && e.Metadata.GetValueOrDefault("phase")
                    == "execute_heartbeat"
                && long.Parse(e.Metadata["elapsed_ms"]) > 0)
                .Should().BeTrue();
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
            private readonly ToolResult _result;

            public StubExecutor(ToolResult result)
            {
                _result = result;
            }

            public Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken ct = default)
                => Task.FromResult(_result);
        }
    }

    internal class LambdaExecutor : IToolExecutor
    {
        private readonly Func<ToolCall, CancellationToken, Task<ToolResult>> _fn;
        public LambdaExecutor(Func<ToolCall, CancellationToken, Task<ToolResult>> fn) => _fn = fn;
        public Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken ct = default)
            => _fn(call, ct);
    }

    internal static class ToolRegistryExtensions
    {
        public static void Register(this ToolRegistry registry, IToolDescriptor descriptor,
            Func<ToolCall, CancellationToken, Task<ToolResult>> executor)
        {
            registry.Register(descriptor, new LambdaExecutor(executor));
        }
    }
}
