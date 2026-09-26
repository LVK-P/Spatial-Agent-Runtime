using ArcGISProAIAgent.ViewModels;
using FluentAssertions;
using SpatialAgent.Runtime.Contracts;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    public class ApprovalUiStateTests
    {
        [Fact]
        public void Empty_state_is_not_actionable()
        {
            var state = ApprovalUiStateMachine.Empty();

            state.HasPendingApproval.Should().BeFalse();
            state.CanApprove.Should().BeFalse();
            state.PendingTools.Should().BeEmpty();
        }

        [Fact]
        public void Response_state_becomes_actionable_only_with_request_and_ticket()
        {
            var resp = new AgentResponse
            {
                RequiresApproval = true,
                ApprovalKind = ApprovalKind.Destructive,
                PendingApproval = new ApprovalTicket
                {
                    Id = "ticket-1",
                    ToolName = "management_DeleteField",
                    CanonicalToolName = "management_DeleteField",
                    Evidence = new ApprovalEvidence
                    {
                        Summary = "需要用户确认"
                    }
                },
                PendingTools = new List<string> { "management_DeleteField" }
            };

            var state = ApprovalUiStateMachine.FromResponse(resp, "删除字段");

            state.HasPendingApproval.Should().BeTrue();
            state.CanApprove.Should().BeTrue();
            state.PendingApprovalText.Should().Be("删除字段前，请确认本步操作");
            state.PendingApprovalReason.Should().Contain("可能删除、覆盖或重建现有数据");
            state.PendingRequestText.Should().Be("删除字段");
        }

        [Fact]
        public void Checkpoint_restore_preserves_pending_approval_context()
        {
            var restored = new AgentRunState
            {
                Status = SessionStatus.WaitingForApproval,
                UserMessage = "继续执行",
                PendingApproval = new ApprovalTicket
                {
                    Id = "ticket-2",
                    Kind = ApprovalKind.DataInfluence,
                    ToolName = "management_CalculateField",
                    CanonicalToolName = "management_CalculateField",
                    Evidence = new ApprovalEvidence
                    {
                        Summary = "影响记录过多",
                        IsReliable = true
                    }
                },
                PendingTools = new List<string> { "management_CalculateField", "cc_calculate_area" }
            };

            var state = ApprovalUiStateMachine.FromCheckpoint(restored);

            state.HasPendingApproval.Should().BeTrue();
            state.CanApprove.Should().BeTrue();
            state.PendingApprovalKind.Should().Be(ApprovalKind.DataInfluence);
            state.PendingApprovalText.Should().Be("更新字段值前，需要确认影响范围");
            state.PendingApprovalReason.Should().Contain("批量改写");
            state.PendingRequestText.Should().Be("继续执行");
        }

        [Fact]
        public void Response_state_formats_structured_influence_evidence_for_ui()
        {
            var resp = new AgentResponse
            {
                RequiresApproval = true,
                ApprovalKind = ApprovalKind.DataInfluence,
                PendingApproval = new ApprovalTicket
                {
                    Id = "ticket-3",
                    Kind = ApprovalKind.DataInfluence,
                    ToolName = "management_CalculateField",
                    CanonicalToolName = "management_CalculateField",
                    Target = new ApprovalTarget
                    {
                        Layer = "乡镇边界",
                        Field = "TQMJ, TBMJ"
                    },
                    Evidence = new ApprovalEvidence
                    {
                        Summary = "乡镇边界图层影响过大",
                        IsReliable = true,
                        ScopeObjectCount = 147,
                        TargetFieldCount = 2,
                        ScopeValueCount = 294,
                        ExistingNonEmptyTargetCount = 120,
                        UsesSelectionScope = true,
                        UsesFilteredScope = false
                    }
                },
                PendingTools = new List<string> { "cc_calculate_area" }
            };

            var state = ApprovalUiStateMachine.FromResponse(resp, "继续执行");

            state.PendingApprovalText.Should().Contain("乡镇边界");
            state.PendingApprovalText.Should().Contain("2 个字段");
            state.PendingApprovalReason.Should().Contain("批量改写");
            state.PendingApprovalReason.Should().Contain("字段成果批量覆写");
            state.PendingApprovalReason.Should().Contain("本次涉及对象约 147");
            state.PendingApprovalReason.Should().Contain("会同时写入 2 个目标字段");
            state.PendingApprovalReason.Should().Contain("目标写入单元约 294");
            state.PendingApprovalReason.Should().Contain("已有成果会被改写约 120");
            state.PendingApprovalReason.Should().Contain("已按选择集收窄范围");
        }

        [Fact]
        public void Area_write_uses_area_specific_copy()
        {
            var resp = new AgentResponse
            {
                RequiresApproval = true,
                ApprovalKind = ApprovalKind.DataInfluence,
                PendingApproval = new ApprovalTicket
                {
                    Id = "ticket-4",
                    Kind = ApprovalKind.DataInfluence,
                    ToolName = "cc_calculate_area",
                    CanonicalToolName = "cc_calculate_area",
                    Target = new ApprovalTarget
                    {
                        Layer = "项目红线",
                        Field = "TQMJ"
                    },
                    Evidence = new ApprovalEvidence
                    {
                        IsReliable = true,
                        ScopeObjectCount = 180,
                        ExistingNonEmptyTargetCount = 135
                    }
                }
            };

            var state = ApprovalUiStateMachine.FromResponse(resp, "继续执行");

            state.PendingApprovalText.Should().Be("写入面积结果前，请确认 “项目红线”图层的“TQMJ”字段");
            state.PendingApprovalReason.Should().Contain("重算");
            state.PendingApprovalReason.Should().Contain("面积成果覆写");
        }

        [Fact]
        public void Exact_batch_renders_every_machine_covered_target()
        {
            var ticket = new ApprovalTicket
            {
                Id = "batch-1",
                Scope = ApprovalScope.ExactBatch,
                Kind = ApprovalKind.Destructive,
                ToolName = "schema_change",
                CanonicalToolName = "schema_change",
                Target = new ApprovalTarget { Dataset = "first" },
                CoveredCalls = new List<ApprovalCoveredCall>
                {
                    new()
                    {
                        StepId = 1,
                        ToolName = "schema_change",
                        CanonicalToolName = "schema_change",
                        Target = new ApprovalTarget { Dataset = "first", Field = "old_a" }
                    },
                    new()
                    {
                        StepId = 2,
                        ToolName = "schema_change",
                        CanonicalToolName = "schema_change",
                        Target = new ApprovalTarget { Dataset = "second", Field = "old_b" }
                    }
                }
            };

            var state = ApprovalUiStateMachine.FromResponse(new AgentResponse
            {
                RequiresApproval = true,
                PendingApproval = ticket
            }, "批量调整结构");

            state.PendingApprovalText.Should().Contain("2 项操作");
            state.PendingApprovalReason.Should().Contain("first");
            state.PendingApprovalReason.Should().Contain("old_a");
            state.PendingApprovalReason.Should().Contain("second");
            state.PendingApprovalReason.Should().Contain("old_b");
            state.PendingTools.Should().Contain("change · first.old_a");
            state.PendingTools.Should().Contain("change · second.old_b");
        }
    }
}
