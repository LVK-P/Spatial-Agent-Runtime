using FluentAssertions;
using SpatialAgent.Core;
using SpatialAgent.Runtime.Contracts;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    public class PlanCoverageValidatorTests
    {
        private readonly DefaultPlanCoverageValidator _validator = new();
        private readonly DefaultGoalScopeResolver _goals = new();

        [Fact]
        public void Category_scope_reports_every_unplanned_domain_target()
        {
            var domain = Domain(
                ("roads", "Polyline"),
                ("highway_4490", "Polyline"),
                ("boundary", "Polygon"),
                ("parcel", "Polygon"),
                ("point_a", "Point"));
            var plan = PlanFor("highway_4490", "boundary");

            var result = Validate(
                "线图层计算长度，面图层计算面积",
                plan,
                domain);

            result.IsApplicable.Should().BeTrue();
            result.IsComplete.Should().BeFalse();
            result.MissingTargets.Should().BeEquivalentTo("roads", "parcel");
        }

        [Fact]
        public void Category_scope_accepts_complete_plan_without_tool_name_rules()
        {
            var domain = Domain(
                ("a", "Polyline"),
                ("b", "Polyline"),
                ("p", "Point"));

            var result = Validate(
                "process all line layers",
                PlanFor("a", "b"),
                domain);

            result.IsComplete.Should().BeTrue();
            result.ExpectedTargets.Should().BeEquivalentTo("a", "b");
        }

        [Fact]
        public void Singular_goal_without_set_scope_is_not_overexpanded()
        {
            var domain = Domain(("a", "Polyline"), ("b", "Polyline"));

            var result = Validate("更新 a", PlanFor("a"), domain);

            result.IsApplicable.Should().BeTrue();
            result.IsComplete.Should().BeTrue();
            result.ExpectedTargets.Should().Equal("a");
            result.GoalTargets.Should().NotContain("b");
        }

        [Fact]
        public void Explicit_target_qualified_by_category_does_not_expand_to_peers()
        {
            var domain = Domain(
                ("roads", "Polyline"),
                ("highway", "Polyline"));

            var result = Validate(
                "在线图层 roads 上执行处理",
                PlanFor("roads"),
                domain);

            result.IsComplete.Should().BeTrue();
            result.ExpectedTargets.Should().Equal("roads");
        }

        [Fact]
        public void Named_reference_is_not_promoted_to_a_write_target()
        {
            var domain = Domain(
                ("source_a", "Polygon"),
                ("reference_surface", "Polygon"));

            var result = Validate(
                "对 source_a 统一处理，采用 reference_surface 面图层作为参考",
                PlanFor("source_a"),
                domain);

            result.ReferenceTargets.Should().ContainSingle(
                "reference_surface");
            result.MissingTargets.Should().NotContain("reference_surface");
        }

        [Fact]
        public void Plan_target_outside_goal_contract_is_rejected()
        {
            var domain = Domain(
                ("town_boundary", "Polygon"),
                ("old_project_boundary", "Polygon"));

            var result = Validate(
                "更新 town_boundary",
                PlanFor("town_boundary", "old_project_boundary"),
                domain);

            result.IsComplete.Should().BeFalse();
            result.UnexpectedTargets.Should().Equal(
                "old_project_boundary");
        }

        [Fact]
        public void Universal_scope_keeps_every_physical_lineage_member()
        {
            var domain = Domain(
                ("roads_raw", "Polyline"),
                ("roads_normalized", "Polyline"));
            domain.Layers.Single(layer =>
                layer.Name == "roads_normalized").LineageRoot = "roads_raw";
            domain.Layers.Single(layer =>
                layer.Name == "roads_normalized").LineageDepth = 1;

            var result = Validate(
                "处理所有线图层",
                PlanFor("roads_raw", "roads_normalized"),
                domain);

            result.ExpectedTargets.Should().BeEquivalentTo(
                "roads_raw",
                "roads_normalized");
            result.RedundantTargets.Should().BeEmpty();
            result.IsComplete.Should().BeTrue();
        }

        private TargetCoverageResult Validate(
            string goal,
            AgentPlan plan,
            DomainContext domain)
        {
            var scope = _goals.Resolve(goal, null, domain);
            return _validator.Validate(scope, plan, domain);
        }

        private static DomainContext Domain(
            params (string Name, string Geometry)[] layers) =>
            new()
            {
                Layers = layers.Select(item => new LayerInfo
                {
                    Name = item.Name,
                    GeometryType = item.Geometry
                }).ToList()
            };

        private static AgentPlan PlanFor(params string[] targets) =>
            new()
            {
                Steps = targets.Select((target, index) => new PlanStep
                {
                    Id = index + 1,
                    WriteSet = new() { target },
                    Effect = new InvocationEffect
                    {
                        Profile = new EffectProfile
                        {
                            Effects = DataEffectKind.UpdateRows
                        },
                        Target = new ApprovalTarget { Layer = target }
                    }
                }).ToList()
            };
    }
}
