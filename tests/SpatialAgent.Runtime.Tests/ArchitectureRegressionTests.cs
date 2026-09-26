using FluentAssertions;
using SpatialAgent.Core;
using SpatialAgent.Runtime;
using SpatialAgent.Runtime.Contracts;
using SpatialAgent.Services;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    public sealed class ArchitectureRegressionTests
    {
        [Fact]
        public void Absent_state_scope_targets_every_matching_prestate_across_lineage()
        {
            var domain = new DomainContext
            {
                Layers = new()
                {
                    Layer("raw", "Polyline", "legacy"),
                    Layer("derived", "Polyline", "legacy"),
                    Layer("already_done", "Polyline"),
                    Layer("unrelated", "Point", "legacy")
                }
            };
            domain.Layers[1].LineageRoot = "raw";
            domain.Layers[1].LineageDepth = 1;
            var plan = Plan(
                ("raw", "legacy"),
                ("derived", "legacy"));
            var validator = new DefaultPlanCoverageValidator();
            var goals = new DefaultGoalScopeResolver();

            var scope = goals.Resolve(
                "remove the legacy property from all line layers",
                null,
                domain);
            var coverage = validator.Validate(scope, plan, domain);

            scope.Quantifier.Should().Be(GoalTargetQuantifier.AllMatchingState);
            coverage.ExpectedTargets.Should().BeEquivalentTo("raw", "derived");
            coverage.ExpectedTargets.Should().NotContain("already_done");
            coverage.ExpectedTargets.Should().NotContain("unrelated");
            coverage.IsComplete.Should().BeTrue();
        }

        [Fact]
        public void Follow_up_universal_scope_inherits_prior_domain_constraints()
        {
            var domain = new DomainContext
            {
                Layers = new()
                {
                    Layer("line_a", "Polyline", "legacy"),
                    Layer("line_b", "Polyline"),
                    Layer("point_a", "Point", "legacy")
                }
            };
            var validator = new DefaultPlanCoverageValidator();
            var goals = new DefaultGoalScopeResolver();
            var firstPlan = Plan(("line_a", "legacy"));
            var previous = goals.Resolve(
                "remove the legacy property from all line layers",
                null,
                domain);

            var followUp = goals.Resolve(
                "I mean all matching ones",
                previous,
                domain);
            var coverage = validator.Validate(followUp, firstPlan, domain);

            followUp.InheritedConstraints.Should().BeTrue();
            followUp.DomainCategories.Should().Equal("line");
            coverage.ExpectedTargets.Should().Equal("line_a");
        }

        [Fact]
        public void Project_lineage_and_terminal_session_summary_survive_store_reopen()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "spatial-agent-architecture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var first = new ProjectMemoryStore(root);
                first.UpsertLineage(new DataLineageRecord
                {
                    Output = "derived",
                    Parent = "raw",
                    Inputs = new()
                    {
                        new DataLineageInput
                        {
                            Resource = "raw",
                            Role = DataLineageInputRole.Primary
                        },
                        new DataLineageInput
                        {
                            Resource = "boundary",
                            Role = DataLineageInputRole.Reference
                        }
                    }
                });
                first.RecordSessionSummary(new SessionSummary
                {
                    ThreadId = "thread-1",
                    Revision = 1,
                    WhatWasDone = "progress"
                });
                first.RecordSessionSummary(new SessionSummary
                {
                    ThreadId = "thread-1",
                    Revision = 2,
                    IsTerminal = true,
                    WhatWasDone = "complete"
                });
                first.RecordSessionSummary(new SessionSummary
                {
                    ThreadId = "thread-1",
                    Revision = 3,
                    WhatWasDone = "stale progress"
                });

                var reopened = new ProjectMemoryStore(root);

                reopened.LoadLineage().Should().ContainSingle(item =>
                    item.Output == "derived" && item.Parent == "raw");
                reopened.LoadLineage().Single().Inputs.Should().Contain(item =>
                    item.Resource == "boundary"
                    && item.Role == DataLineageInputRole.Reference);
                reopened.GetSummary().Should().Contain("complete");
                reopened.GetSummary().Should().NotContain("stale progress");
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void Completion_report_separates_support_reads_from_business_steps()
        {
            var read = Result(1, DataEffectKind.ReadData);
            var write = Result(2, DataEffectKind.ReadData | DataEffectKind.UpdateRows);

            var report = CompletionReportBuilder.Build("update targets", new[] { read, write });
            string rendered = CompletionReportBuilder.Render(report);

            report.Items.Should().ContainSingle(item => item.StepId == 2);
            report.SupportItems.Should().ContainSingle(item => item.StepId == 1);
            rendered.Should().Contain("支持性检查：1/1 项完成");
        }

        private static LayerInfo Layer(
            string name,
            string geometry,
            params string[] fields) => new()
            {
                Name = name,
                GeometryType = geometry,
                Fields = fields.ToList()
            };

        private static AgentPlan Plan(params (string Layer, string Field)[] targets) =>
            new()
            {
                Steps = targets.Select((target, index) => new PlanStep
                {
                    Id = index + 1,
                    WriteSet = new() { target.Layer },
                    Effect = new InvocationEffect
                    {
                        Profile = new EffectProfile
                        {
                            Effects = DataEffectKind.AlterSchema
                        },
                        DesiredTargetState = TargetStateExpectation.Absent,
                        Target = new ApprovalTarget
                        {
                            Layer = target.Layer,
                            Field = target.Field
                        }
                    }
                }).ToList()
            };

        private static StepExecutionResult Result(
            int id,
            DataEffectKind effects) => new()
            {
                StepId = id,
                ToolName = $"tool-{id}",
                Status = StepResultStatus.Completed,
                Effect = new InvocationEffect
                {
                    Profile = new EffectProfile { Effects = effects },
                    Target = new ApprovalTarget { Layer = $"layer-{id}" }
                }
            };
    }
}
