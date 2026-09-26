using FluentAssertions;
using SpatialAgent.Core;
using SpatialAgent.Runtime.Contracts;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    public sealed class SettlementPolicyTests
    {
        [Fact]
        public void Host_invalidation_does_not_blacklist_tool_name()
        {
            var result = new StepExecutionResult
            {
                Status = StepResultStatus.Blocked,
                Cause = StepOutcomeCause.HostStateInvalidated,
                ToolName = "management_Project",
                UserApproved = null
            };

            SettlementPolicy.ShouldBlockToolName(result).Should().BeFalse();
        }

        [Fact]
        public void User_rejection_blacklists_tool_and_signature()
        {
            var result = new StepExecutionResult
            {
                Status = StepResultStatus.Blocked,
                Cause = StepOutcomeCause.UserRejected,
                ToolName = "management_Project",
                UserApproved = false
            };

            SettlementPolicy.ShouldBlockToolName(result).Should().BeTrue();
        }

        [Fact]
        public void Expected_mutation_builder_includes_write_set()
        {
            var plan = new AgentPlan
            {
                Steps =
                {
                    new PlanStep
                    {
                        Id = 1,
                        WriteSet = { "roads" },
                        ToolCall = new ToolCall
                        {
                            Name = "generic_transform",
                            Arguments =
                            {
                                ["in_features"] = "source_roads",
                                ["out_feature_class"] =
                                    @"C:\proj\agentworkspace.gdb\roads"
                            }
                        },
                        Effect = new InvocationEffect
                        {
                            Profile = new EffectProfile
                            {
                                Effects = DataEffectKind.ModifySpatialReference
                            },
                            Target = new ApprovalTarget
                            {
                                Dataset = "roads",
                                OutputTarget = @"C:\proj\agentworkspace.gdb\roads"
                            }
                        }
                    }
                }
            };

            var set = ExpectedMutationBuilder.Build(plan);
            set.IsEmpty.Should().BeFalse();
            set.CoversResource("roads").Should().BeTrue();
            set.CoversResource(@"C:\proj\agentworkspace.gdb\roads").Should().BeTrue();
            set.CoversResource("source_roads").Should().BeFalse();
            set.HasMutatingEffects.Should().BeTrue();
        }
    }
}
