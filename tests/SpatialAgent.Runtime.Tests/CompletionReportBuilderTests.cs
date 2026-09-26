using FluentAssertions;
using SpatialAgent.Runtime.Contracts;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    public class CompletionReportBuilderTests
    {
        [Fact]
        public void Completion_report_preserves_goal_dataset_field_and_verification()
        {
            var steps = new List<StepExecutionResult>
            {
                new()
                {
                    StepId = 1,
                    ToolName = "schema_change",
                    Status = StepResultStatus.Completed,
                    InputParameters = new()
                    {
                        ["source_table"] = "assets",
                        ["target_field"] = "legacy_code"
                    },
                    Effect = new InvocationEffect
                    {
                        Target = new ApprovalTarget
                        {
                            Dataset = "assets",
                            Field = "legacy_code"
                        }
                    },
                    Validations = new()
                    {
                        new ValidationResult
                        {
                            RuleName = "post_condition",
                            Verdict = ValidationVerdict.PASS,
                            Detail = "目标状态符合预期"
                        }
                    }
                }
            };

            string reply = CompletionReportBuilder.Render(
                CompletionReportBuilder.Build("移除废弃字段", steps));

            reply.Should().Contain("已完成：移除废弃字段");
            reply.Should().Contain("业务结果：1/1 项成功");
            reply.Should().Contain("验证结果：1 项通过");
            reply.Should().NotContain("schema_change");
        }

        [Fact]
        public void Closed_goal_is_not_reopened_by_historical_failed_steps()
        {
            var scope = new ResolvedGoalScope
            {
                ExplicitTargets = new() { "roads", "boundary" },
                SatisfiedTargets = new() { "roads", "boundary" },
                ProofObligations = new()
                {
                    new GoalProofObligation
                    {
                        Id = "proof:coverage",
                        Kind = GoalProofKind.TargetCoverage,
                        Required = true
                    }
                },
                SatisfiedProofObligations = new() { "proof:coverage" }
            };
            var coverage = new TargetCoverageResult
            {
                IsApplicable = true,
                Applicability = CoverageApplicability.Applicable,
                ProgressEvaluated = true,
                IsGoalComplete = true,
                GoalTargets = new() { "roads", "boundary" },
                SatisfiedTargets = new() { "roads", "boundary" }
            };
            var steps = new List<StepExecutionResult>
            {
                new()
                {
                    StepId = 1,
                    Status = StepResultStatus.Blocked,
                    Effect = MutationEffect("roads")
                },
                new()
                {
                    StepId = 2,
                    Status = StepResultStatus.Completed,
                    Effect = MutationEffect("roads")
                },
                new()
                {
                    StepId = 3,
                    Status = StepResultStatus.Completed,
                    Effect = MutationEffect("boundary")
                }
            };

            GoalCompletionReport report = CompletionReportBuilder.Build(
                "更新全部目标",
                steps,
                scope,
                coverage);

            report.Outcome.Should().Be(GoalOutcomeStatus.CompletedWithWarnings);
            report.IsComplete.Should().BeTrue();
            CompletionReportBuilder.Render(report)
                .Should().Contain("目标状态和必需证据已经闭合")
                .And.NotContain("部分完成");
        }

        [Fact]
        public void Failed_business_item_surfaces_validation_detail_and_target()
        {
            var steps = new List<StepExecutionResult>
            {
                new()
                {
                    StepId = 1,
                    ToolName = "cc_calculate_area",
                    Status = StepResultStatus.Failed,
                    Effect = new InvocationEffect
                    {
                        Target = new ApprovalTarget
                        {
                            Layer = "项目红线",
                            Field = "TQMJ"
                        },
                        Profile = new EffectProfile
                        {
                            Effects = DataEffectKind.UpdateRows
                        }
                    },
                    Validations = new()
                    {
                        new ValidationResult
                        {
                            Verdict = ValidationVerdict.FAIL,
                            Detail = "测地面积重算超出允许误差"
                        }
                    }
                }
            };

            string reply = CompletionReportBuilder.Render(
                CompletionReportBuilder.Build("计算椭球面积", steps));

            reply.Should().Contain("未成功：项目红线.TQMJ")
                .And.Contain("测地面积重算超出允许误差");
        }

        private static InvocationEffect MutationEffect(string target) => new()
        {
            Target = new ApprovalTarget { Layer = target },
            Profile = new EffectProfile { Effects = DataEffectKind.AlterSchema }
        };
    }
}
