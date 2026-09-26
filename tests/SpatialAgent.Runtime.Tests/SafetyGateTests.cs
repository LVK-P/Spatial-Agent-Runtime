using FluentAssertions;
using SpatialAgent.Runtime;
using SpatialAgent.Runtime.Contracts;
using SpatialAgent.Services;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    public class SafetyGateTests
    {
        private readonly SafetyGate _gate = new();

        [Theory]
        [InlineData("get_map_info", ToolRiskLevel.ReadOnly)]
        [InlineData("list_layers", ToolRiskLevel.ReadOnly)]
        [InlineData("describe_layer", ToolRiskLevel.ReadOnly)]
        [InlineData("get_selection", ToolRiskLevel.ReadOnly)]
        [InlineData("get_project_info", ToolRiskLevel.ReadOnly)]
        [InlineData("SelectLayerByAttribute", ToolRiskLevel.ReadOnly)]
        [InlineData("MakeFeatureLayer", ToolRiskLevel.ReadOnly)]
        [InlineData("GetCount", ToolRiskLevel.ReadOnly)]
        public void Classify_readonly_tools(string toolName, ToolRiskLevel expected)
        {
            _gate.Classify(toolName).Should().Be(expected);
        }

        [Theory]
        [InlineData("Buffer", ToolRiskLevel.Increment)]
        [InlineData("Clip", ToolRiskLevel.Increment)]
        [InlineData("Intersect", ToolRiskLevel.Increment)]
        [InlineData("Union", ToolRiskLevel.Increment)]
        [InlineData("Merge", ToolRiskLevel.Increment)]
        [InlineData("Dissolve", ToolRiskLevel.Increment)]
        [InlineData("Near", ToolRiskLevel.Increment)]
        [InlineData("SpatialJoin", ToolRiskLevel.Increment)]
        [InlineData("Project", ToolRiskLevel.Increment)]
        [InlineData("CopyFeatures", ToolRiskLevel.Increment)]
        [InlineData("Append", ToolRiskLevel.Increment)]
        [InlineData("FeatureToPolygon", ToolRiskLevel.Increment)]
        [InlineData("Erase", ToolRiskLevel.Increment)]
        [InlineData("Statistics", ToolRiskLevel.Increment)]
        [InlineData("SummaryStatistics", ToolRiskLevel.Increment)]
        [InlineData("AddJoin", ToolRiskLevel.Increment)]
        [InlineData("AddField", ToolRiskLevel.Increment)]
        [InlineData("TableToTable", ToolRiskLevel.Increment)]
        public void Classify_increment_tools(string toolName, ToolRiskLevel expected)
        {
            _gate.Classify(toolName).Should().Be(expected);
        }

        [Theory]
        [InlineData("CalculateField", ToolRiskLevel.Modify)]
        public void Classify_modify_tools(string toolName, ToolRiskLevel expected)
        {
            _gate.Classify(toolName).Should().Be(expected);
        }

        [Theory]
        [InlineData("DeleteField", ToolRiskLevel.Destructive)]
        [InlineData("DeleteFeatures", ToolRiskLevel.Destructive)]
        [InlineData("DeleteRows", ToolRiskLevel.Destructive)]
        [InlineData("TruncateTable", ToolRiskLevel.Destructive)]
        [InlineData("AlterField", ToolRiskLevel.Destructive)]
        public void Classify_destructive_tools(string toolName, ToolRiskLevel expected)
        {
            _gate.Classify(toolName).Should().Be(expected);
        }

        [Fact]
        public void Unknown_dotted_tool_defaults_to_destructive()
        {
            // ★ P0-3 修复（2026-07-20）：未知工具默认归类为 Destructive 而非 Modify。
            // 未知名称可能是 LLM 幻觉，也可能来自未审计的本地 manifest。
            // 2026 年安全实践：unknown → deny/require-approval，不是 unknown → allow。
            _gate.Classify("custom.Tool").Should().Be(ToolRiskLevel.Destructive);
        }

        [Fact]
        public void Unknown_tools_default_to_destructive()
        {
            // ★ P0-3 修复（2026-07-20）：所有未知工具默认归类为 Destructive。
            // 如果确实是新工具（manifest 更新导致），应在 SafetyGate.RegisterClassification()
            // 中显式注册风险级别后才能使用。
            _gate.Classify("UnknownTool").Should().Be(ToolRiskLevel.Destructive);
            _gate.Classify("some.custom.Tool").Should().Be(ToolRiskLevel.Destructive);
        }

        [Theory]
        [InlineData(AgentPermissionLevel.FullAccess, ToolRiskLevel.Destructive, SafetyDecisionKind.Allow)]
        [InlineData(AgentPermissionLevel.RequestApproval, ToolRiskLevel.Destructive, SafetyDecisionKind.RequireApproval)]
        [InlineData(AgentPermissionLevel.RequestApproval, ToolRiskLevel.Modify, SafetyDecisionKind.Allow)]
        [InlineData(AgentPermissionLevel.RequestApproval, ToolRiskLevel.ReadOnly, SafetyDecisionKind.Allow)]
        [InlineData(AgentPermissionLevel.ReadOnly, ToolRiskLevel.ReadOnly, SafetyDecisionKind.Allow)]
        [InlineData(AgentPermissionLevel.ReadOnly, ToolRiskLevel.Modify, SafetyDecisionKind.Block)]
        [InlineData(AgentPermissionLevel.ReadOnly, ToolRiskLevel.Destructive, SafetyDecisionKind.Block)]
        public void Evaluate_respects_permission_level(
            AgentPermissionLevel permission, ToolRiskLevel risk, SafetyDecisionKind expected)
        {
            // Register a custom classification to control risk level
            _gate.RegisterClassification("_test_tool", risk);
            var decision = _gate.Evaluate("_test_tool", permission);
            decision.Kind.Should().Be(expected);
        }

        [Fact]
        public void IsDestructive_returns_true_only_for_destructive()
        {
            _gate.IsDestructive("DeleteField").Should().BeTrue();
            _gate.IsDestructive("Buffer").Should().BeFalse();
            _gate.IsDestructive("get_map_info").Should().BeFalse();
        }

        [Fact]
        public void NeedsBackup_returns_true_only_for_destructive()
        {
            _gate.NeedsBackup("DeleteField").Should().BeTrue();
            _gate.NeedsBackup("Buffer").Should().BeFalse();
            _gate.NeedsBackup("get_map_info").Should().BeFalse();
        }

        [Fact]
        public void Wildcard_registration_matches_short_name_prefix()
        {
            // Register "Delete*" — matches any tool whose short name starts with "Delete"
            _gate.RegisterClassification("Delete*", ToolRiskLevel.Destructive);
            _gate.Classify("management_DeleteRows").Should().Be(ToolRiskLevel.Destructive);
        }

        // ════════════════════════════════════════════════════════════════
        // ★ P0-3：未知工具 → Destructive 行为在 Evaluate 中的表现
        // ════════════════════════════════════════════════════════════════

        [Fact]
        public void Unknown_tool_in_RequestApproval_requires_approval()
        {
            // P0-3 修复后：未知工具 → Destructive → RequestApproval 模式下触发审批
            var decision = _gate.Evaluate("UnknownHallucinatedTool", AgentPermissionLevel.RequestApproval);
            decision.Kind.Should().Be(SafetyDecisionKind.RequireApproval,
                "未知工具被分类为 Destructive，在 RequestApproval 模式必须触发审批");
        }

        [Fact]
        public void Unknown_tool_in_ReadOnly_is_blocked()
        {
            // P0-3 修复后：未知工具 → Destructive → ReadOnly 模式下被阻止
            var decision = _gate.Evaluate("UnknownHallucinatedTool", AgentPermissionLevel.ReadOnly);
            decision.Kind.Should().Be(SafetyDecisionKind.Block,
                "未知工具被分类为 Destructive，在 ReadOnly 模式必须被阻止");
        }

        [Fact]
        public void Unknown_tool_in_FullAccess_is_allowed_but_classified_destructive()
        {
            // P0-3 修复后：未知工具 → Destructive → FullAccess 仍放行（保持效率）
            // 但 RiskLevel 为 Destructive——后续 Scheduler 会触发备份流程
            var decision = _gate.Evaluate("UnknownHallucinatedTool", AgentPermissionLevel.FullAccess);
            decision.Kind.Should().Be(SafetyDecisionKind.Allow,
                "FullAccess 模式放行所有操作");
            decision.RiskLevel.Should().Be(ToolRiskLevel.Destructive,
                "但风险等级标记为 Destructive——确保备份流程被触发");
        }
        [Fact]
        public void Descriptor_safety_profile_overrides_annotation_for_runtime_classification()
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

            _gate.SetToolRegistry(registry);

            _gate.Classify("custom_safe_tool").Should().Be(ToolRiskLevel.Destructive);
        }

        [Fact]
        public void Catalog_classification_is_loaded_for_dynamic_tool_function_name()
        {
            _gate.Classify("_3d_AppendTerrainPoints").Should().Be(ToolRiskLevel.Destructive);
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
