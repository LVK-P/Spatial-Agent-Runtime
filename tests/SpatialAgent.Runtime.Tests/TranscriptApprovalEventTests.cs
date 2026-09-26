using FluentAssertions;
using SpatialAgent.Runtime;
using SpatialAgent.Runtime.Contracts;
using SpatialAgent.Services;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    public class TranscriptApprovalEventTests : IDisposable
    {
        private readonly TranscriptWriter _writer;
        private readonly string _filePath;

        public TranscriptApprovalEventTests()
        {
            var dir = Path.Combine(Path.GetTempPath(), "tw_approval_" + Guid.NewGuid().ToString("N")[..8]);
            _writer = new TranscriptWriter();
            _writer.Initialize(dir);
            _filePath = _writer.FilePath!;
        }

        public void Dispose()
        {
            _writer.Dispose();
            if (File.Exists(_filePath))
            {
                try { File.Delete(_filePath); } catch { }
            }
        }

        [Fact]
        public void Approval_required_event_includes_ticket_metadata_in_transcript()
        {
            var bus = new EventBus();
            _writer.SubscribeTo(bus);

            bus.Publish(new AgentEvent
            {
                EventType = "safety.approval_required",
                Turn = 2,
                Metadata = new()
                {
                    ["tool"] = "management_DeleteField",
                    ["step"] = "3",
                    ["risk"] = "Destructive",
                    ["required_permission"] = "FullAccess",
                    ["ticket_id"] = "ticket-123",
                    ["approval_kind"] = "Destructive",
                    ["call_signature"] = "sig:abc",
                    ["target_signature"] = "target:def"
                },
                Data = new()
                {
                    ["args"] = new Dictionary<string, object?> { ["in_table"] = "parcel_layer" },
                    ["reason"] = "需要用户确认删除操作",
                    ["ticket"] = new ApprovalTicket
                    {
                        Id = "ticket-123",
                        Kind = ApprovalKind.Destructive,
                        CallSignature = "sig:abc",
                        Target = new ApprovalTarget { Signature = "target:def" }
                    }
                }
            });

            _writer.Dispose();
            var content = File.ReadAllText(_filePath);
            content.Should().Contain("\"type\":\"safety_approval_required\"");
            content.Should().Contain("\"ticket_id\":\"ticket-123\"");
            content.Should().Contain("\"approval_kind\":\"Destructive\"");
            content.Should().Contain("\"call_signature\":\"sig:abc\"");
            content.Should().Contain("\"target_signature\":\"target:def\"");
        }

        [Fact]
        public void Approval_resumed_and_rejected_events_include_ticket_metadata_in_transcript()
        {
            var bus = new EventBus();
            _writer.SubscribeTo(bus);

            bus.Publish(new AgentEvent
            {
                EventType = "safety.approval_resumed",
                Turn = 5,
                Metadata = new()
                {
                    ["approved"] = "true",
                    ["action"] = "approved",
                    ["ticket_id"] = "ticket-approve",
                    ["approval_kind"] = "DataInfluence",
                    ["required_permission"] = "",
                    ["approved_steps"] = "2"
                },
                Data = new()
                {
                    ["approved"] = new[] { new { step = 2, ticket_id = "ticket-approve" } }
                }
            });

            bus.Publish(new AgentEvent
            {
                EventType = "safety.approval_rejected",
                Turn = 5,
                Metadata = new()
                {
                    ["tools"] = "management_CalculateField",
                    ["reason"] = "UserRejected",
                    ["ticket_id"] = "ticket-reject"
                },
                Data = new()
                {
                    ["rejected_steps"] = new[] { new { step = 4, ticket_id = "ticket-reject", call_signature = "sig:reject" } }
                }
            });

            _writer.Dispose();
            var content = File.ReadAllText(_filePath);
            content.Should().Contain("\"type\":\"approval_resumed\"");
            content.Should().Contain("\"ticket_id\":\"ticket-approve\"");
            content.Should().Contain("\"approval_kind\":\"DataInfluence\"");
            content.Should().Contain("\"type\":\"safety_approval_rejected\"");
            content.Should().Contain("\"ticket_id\":\"ticket-reject\"");
            content.Should().Contain("sig:reject");
        }

        [Fact]
        public void Influence_approval_transcript_includes_structured_probe_fields()
        {
            var bus = new EventBus();
            _writer.SubscribeTo(bus);

            bus.Publish(new AgentEvent
            {
                EventType = "safety.influence_approval_required",
                Turn = 3,
                Metadata = new()
                {
                    ["tool"] = "cc_calculate_area",
                    ["step"] = "10",
                    ["risk"] = "Modify",
                    ["influence_count"] = "147",
                    ["scope_object_count"] = "147",
                    ["target_field_count"] = "2",
                    ["scope_value_count"] = "294",
                    ["existing_nonempty_target_count"] = "120",
                    ["new_value_fill_count"] = "27",
                    ["schema_mutation_count"] = "0",
                    ["uses_selection_scope"] = "true",
                    ["uses_filtered_scope"] = "false",
                    ["source"] = "AttributeTableProbe",
                    ["modify_subtype"] = "QuantityGateCandidate",
                    ["layer"] = "乡镇边界_420300",
                    ["field"] = "TQMJ, TBMJ",
                    ["reliable"] = "true",
                    ["ticket_id"] = "ticket-147",
                    ["approval_kind"] = "DataInfluence",
                    ["call_signature"] = "sig:calc",
                    ["target_signature"] = "target:calc"
                },
                Data = new()
                {
                    ["reason"] = "影响记录过多",
                    ["estimate"] = "“乡镇边界_420300”的 2 个字段约有 147 个已有值会被覆写",
                    ["estimate_details"] = new
                    {
                        layer = "乡镇边界_420300",
                        fields = new[] { "TQMJ", "TBMJ" },
                        scope_object_count = 147,
                        target_field_count = 2,
                        scope_value_count = 294,
                        existing_nonempty_target_count = 120,
                        new_value_fill_count = 27,
                        schema_mutation_count = 0,
                        reliable = true,
                        source = "AttributeTableProbe",
                        modify_subtype = "QuantityGateCandidate",
                        reliability_reason = "Read 147 rows directly from attribute table"
                    },
                    ["ticket"] = new ApprovalTicket
                    {
                        Id = "ticket-147",
                        Kind = ApprovalKind.DataInfluence,
                        CallSignature = "sig:calc",
                        Target = new ApprovalTarget { Signature = "target:calc" },
                        Evidence = new ApprovalEvidence
                        {
                            InfluenceCount = 147,
                            ScopeObjectCount = 147,
                            TargetFieldCount = 2,
                            ScopeValueCount = 294,
                            ExistingNonEmptyTargetCount = 120,
                            NewValueFillCount = 27,
                            SchemaMutationCount = 0,
                            UsesSelectionScope = true,
                            UsesFilteredScope = false
                        }
                    }
                }
            });

            _writer.Dispose();
            var content = File.ReadAllText(_filePath);
            content.Should().Contain("\"type\":\"safety_influence_approval\"");
            content.Should().Contain("\"scope_object_count\":\"147\"");
            content.Should().Contain("\"target_field_count\":\"2\"");
            content.Should().Contain("\"scope_value_count\":\"294\"");
            content.Should().Contain("\"existing_nonempty_target_count\":\"120\"");
            content.Should().Contain("\"new_value_fill_count\":\"27\"");
            content.Should().Contain("\"schema_mutation_count\":\"0\"");
            content.Should().Contain("\"uses_selection_scope\":\"true\"");
            content.Should().Contain("\"uses_filtered_scope\":\"false\"");
            content.Should().Contain("\"source\":\"AttributeTableProbe\"");
            content.Should().Contain("\"modify_subtype\":\"QuantityGateCandidate\"");
            content.Should().Contain("\"estimate_details\"");
        }

        [Fact]
        public void Goal_progress_and_bound_approval_are_durable_transcript_events()
        {
            var bus = new EventBus();
            _writer.SubscribeTo(bus);

            bus.Publish(new AgentEvent
            {
                EventType = "goal.progress",
                Turn = 3,
                Metadata = new()
                {
                    ["goal_id"] = "goal-1",
                    ["newly_satisfied"] = "4",
                    ["residual"] = "1",
                    ["complete"] = "false"
                }
            });
            bus.Publish(new AgentEvent
            {
                EventType = "safety.approval_bound",
                Turn = 3,
                Metadata = new()
                {
                    ["ticket_id"] = "ticket-bound",
                    ["data_revision"] = "revision-123",
                    ["revision_reliable"] = "true"
                }
            });

            _writer.Dispose();
            var content = File.ReadAllText(_filePath);
            content.Should().Contain("\"type\":\"goal_progress\"");
            content.Should().Contain("\"goal_id\":\"goal-1\"");
            content.Should().Contain(
                "\"type\":\"safety_approval_bound\"");
            content.Should().Contain(
                "\"data_revision\":\"revision-123\"");
        }
    }
}
