using FluentAssertions;
using SpatialAgent.Runtime;
using SpatialAgent.Runtime.Contracts;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    public class ContextBudgetingTests
    {
        [Fact]
        public void Build_prioritizes_key_sections_and_keeps_user_message()
        {
            var budgeter = new DefaultContextBudgeter();
            var context = new AgentContext
            {
                ProjectInfo = new string('P', 500),
                MapInfo = new string('M', 800),
                MemorySummary = new string('R', 200),
                KnowledgeChunks = new List<string> { new string('K', 1200) },
                MatchedSkills = new List<MatchedSkill>
                {
                    new() { Name = "buffer", Summary = new string('S', 400) }
                }
            };

            var bundle = budgeter.Build("请分析这个图层", context, new List<Message>());

            bundle.AugmentedUserMessage.Should().Contain("[用户消息]");
            bundle.AugmentedUserMessage.Should().Contain("请分析这个图层");
            bundle.Packets.Should().NotBeEmpty();
            bundle.Packets.First().Title.Should().Be("项目真实数据");
            bundle.ContextHash.Should().NotBeNullOrWhiteSpace();
            bundle.EstimatedTokens.Should().BeGreaterThan(0);
        }

        [Fact]
        public void Build_injects_matched_skill_workflow_not_only_summary()
        {
            var context = new AgentContext
            {
                MatchedSkills = new()
                {
                    new MatchedSkill
                    {
                        Name = "topology",
                        Summary = "short summary",
                        Content = "STEP_ONE describe inputs\nSTEP_TWO run overlap checks"
                    }
                }
            };

            var bundle = new DefaultContextBudgeter().Build("check topology", context);

            bundle.AugmentedUserMessage.Should().Contain("STEP_ONE");
            bundle.AugmentedUserMessage.Should().Contain("STEP_TWO");
        }

        [Fact]
        public void Build_injects_typed_lineage_without_operation_specific_rules()
        {
            var context = new AgentContext
            {
                Layers = new()
                {
                    new LayerInfo { Name = "source_asset" },
                    new LayerInfo
                    {
                        Name = "derived_asset",
                        LineageRoot = "source_asset",
                        LineageDepth = 1
                    }
                }
            };

            var bundle = new DefaultContextBudgeter().Build("process the available assets", context);

            bundle.AugmentedUserMessage.Should().Contain("[数据谱系]");
            bundle.AugmentedUserMessage.Should().Contain("来源=source_asset");
            bundle.AugmentedUserMessage.Should().Contain("派生=derived_asset");
            bundle.Packets.Should().ContainSingle(packet => packet.EvidenceId == "lineage");
        }

        [Fact]
        public void Build_never_truncates_the_current_user_message_to_fit_context()
        {
            string userMessage = "request:" + new string('U', 13000);
            var context = new AgentContext
            {
                ProjectInfo = new string('P', 5000),
                MapInfo = new string('M', 5000)
            };

            var bundle = new DefaultContextBudgeter().Build(userMessage, context);

            bundle.AugmentedUserMessage.Should().EndWith(userMessage);
        }
    }
}
