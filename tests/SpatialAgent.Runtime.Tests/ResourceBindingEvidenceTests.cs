using FluentAssertions;
using SpatialAgent.Core;
using SpatialAgent.Runtime;
using SpatialAgent.Runtime.Contracts;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    public sealed class ResourceBindingEvidenceTests
    {
        private readonly DefaultResourceBindingEvidencePromoter _promoter = new();

        [Fact]
        public void Observation_factory_preserves_resource_identity_echo_for_binding_evidence()
        {
            IObservation observation = ObservationFactory.Create(
                "describe-resource",
                new Dictionary<string, object?> { ["resource"] = "roads_projected" },
                new ToolResult
                {
                    ToolName = "describe-resource",
                    Success = true,
                    Output = "{\"layer\":\"roads_projected\",\"geometryType\":\"Polyline\","
                        + "\"fields\":[{\"name\":\"OBJECTID\"},{\"name\":\"NAME\"}]}"
                },
                TimeSpan.Zero);

            observation.Output.ResourceName.Should().Be("roads_projected");
            observation.Output.GeometryType.Should().Be("Polyline");
            observation.Output.Fields.Should().Equal("OBJECTID", "NAME");
        }

        [Fact]
        public void Probe_of_one_ambiguous_candidate_preserves_confirmation_boundary()
        {
            var (scope, plan, result, domain) = Scenario(
                sourceGoal: "update roads",
                observedFields: new[] { "OBJECTID", "NAME" });

            ResourceBindingPromotionResult promotion = _promoter.Evaluate(
                scope,
                plan,
                new[] { result },
                domain);
            ResourceBindingPatchApplier.Apply(scope, promotion, domain);

            promotion.Evidence.Should().ContainSingle(item => item.IsAdmissible);
            promotion.Patches.Should().ContainSingle()
                .Which.NextStatus.Should().Be(
                    GoalResourceResolutionStatus.CandidateFoundAwaitingConfirmation);
            scope.ResourceBindings.Single().Status.Should().Be(
                GoalResourceResolutionStatus.CandidateFoundAwaitingConfirmation);
            scope.UnresolvedTargets.Should().Equal("roads");
            scope.ExplicitTargets.Should().BeEmpty();
        }

        [Fact]
        public void Lineage_preference_and_probe_evidence_promote_unique_derived_candidate()
        {
            var (scope, plan, result, domain) = Scenario(
                sourceGoal: "update projected only roads",
                observedFields: new[] { "OBJECTID", "NAME" });

            ResourceBindingPromotionResult promotion = _promoter.Evaluate(
                scope,
                plan,
                new[] { result },
                domain);
            ResourceBindingPatchApplier.Apply(scope, promotion, domain);

            promotion.Patches.Should().ContainSingle()
                .Which.NextStatus.Should().Be(GoalResourceResolutionStatus.Resolved);
            scope.UnresolvedTargets.Should().BeEmpty();
            scope.ExplicitTargets.Should().Equal("roads_projected");
            scope.ResourceHandles.Should().ContainSingle(handle =>
                handle.MapMemberId == "map-derived"
                && handle.DatasetId == "dataset-derived"
                && handle.LineageRoot == "roads-family"
                && handle.LineageDepth == 1);
        }

        [Fact]
        public void Schema_mismatch_never_authorizes_a_binding_patch()
        {
            var (scope, plan, result, domain) = Scenario(
                sourceGoal: "update projected only roads",
                observedFields: new[] { "OBJECTID", "WRONG_FIELD" });

            ResourceBindingPromotionResult promotion = _promoter.Evaluate(
                scope,
                plan,
                new[] { result },
                domain);
            ResourceBindingPatchApplier.Apply(scope, promotion, domain);

            promotion.Evidence.Should().ContainSingle()
                .Which.SchemaMatched.Should().BeFalse();
            promotion.Patches.Should().BeEmpty();
            scope.ResourceBindings.Single().Status.Should().Be(
                GoalResourceResolutionStatus.Ambiguous);
            scope.UnresolvedTargets.Should().Equal("roads");
        }

        [Fact]
        public void Candidate_response_is_deterministic_and_model_text_is_only_clarification()
        {
            var (scope, plan, result, domain) = Scenario(
                sourceGoal: "update roads",
                observedFields: new[] { "OBJECTID", "NAME" });
            ResourceBindingPromotionResult promotion = _promoter.Evaluate(
                scope,
                plan,
                new[] { result },
                domain);
            ResourceBindingPatchApplier.Apply(scope, promotion, domain);

            GoalCompletionReport report = CompletionReportBuilder.Build(
                scope.SourceGoal,
                new[] { result },
                scope,
                new TargetCoverageResult
                {
                    Applicability = CoverageApplicability.ScopeInsufficient,
                    UnresolvedTargets = new() { "roads" }
                });
            string response = CompletionResponseArbitrator.Compose(
                report,
                "Please choose the intended roads layer.");

            report.Outcome.Should().Be(GoalOutcomeStatus.ClarificationRequired);
            report.Items.Should().BeEmpty();
            report.SupportItems.Should().ContainSingle();
            response.Should().Contain("roads_projected");
            response.Should().Contain("Please choose the intended roads layer.");
            response.Should().NotContain("未找到");
        }

        private static (
            ResolvedGoalScope Scope,
            AgentPlan Plan,
            StepExecutionResult Result,
            DomainContext Domain) Scenario(
                string sourceGoal,
                IReadOnlyCollection<string> observedFields)
        {
            GoalResourceCandidate raw = Candidate(
                "map-raw",
                "roads_raw",
                "dataset-raw",
                depth: 0);
            GoalResourceCandidate derived = Candidate(
                "map-derived",
                "roads_projected",
                "dataset-derived",
                depth: 1);
            var binding = new GoalResourceBinding
            {
                Mention = "roads",
                Role = GoalResourceRole.Target,
                Status = GoalResourceResolutionStatus.Ambiguous,
                Candidates = new() { raw, derived }
            };
            var scope = new ResolvedGoalScope
            {
                GoalId = "goal-binding",
                Version = 3,
                SourceGoal = sourceGoal,
                UnresolvedTargets = new() { "roads" },
                ResourceBindings = new() { binding },
                ScopeSufficiency = GoalScopeSufficiency.ClarificationRequired
            };
            var step = new PlanStep
            {
                Id = 7,
                Purpose = PlanStepPurpose.SupportProbe,
                ReadSet = new() { "dataset-derived" },
                ToolCall = new ToolCall
                {
                    Id = "probe-call",
                    Name = "describe-resource",
                    Arguments = new() { ["resource"] = "roads_projected" }
                },
                Effect = ReadEffect("roads_projected")
            };
            var plan = new AgentPlan { Steps = new() { step } };
            var observation = new Observation
            {
                StepId = step.Id,
                ToolName = "describe-resource",
                Output = new ObservationOutput
                {
                    ResourceName = "dataset-derived",
                    GeometryType = "Polyline",
                    Fields = observedFields.ToList()
                }
            };
            var result = new StepExecutionResult
            {
                StepId = step.Id,
                ToolCallId = step.ToolCall.Id,
                ToolName = step.ToolCall.Name,
                Status = StepResultStatus.Completed,
                Purpose = PlanStepPurpose.SupportProbe,
                Effect = step.Effect,
                Observation = observation,
                InputParameters = new() { ["resource"] = "roads_projected" }
            };
            var domain = new DomainContext
            {
                Layers = new()
                {
                    Layer(raw),
                    Layer(derived)
                }
            };
            return (scope, plan, result, domain);
        }

        private static GoalResourceCandidate Candidate(
            string mapMemberId,
            string name,
            string datasetId,
            int depth) => new()
            {
                CandidateId = "map:" + mapMemberId,
                DisplayName = name,
                MapMemberId = mapMemberId,
                DatasetId = datasetId,
                GeometryType = "Polyline",
                Fields = new() { "OBJECTID", "NAME" },
                SchemaFingerprint = "schema:" + name,
                LineageRoot = "roads-family",
                LineageDepth = depth
            };

        private static LayerInfo Layer(GoalResourceCandidate candidate) => new()
        {
            Name = candidate.DisplayName,
            MapMemberId = candidate.MapMemberId,
            DatasetId = candidate.DatasetId,
            GeometryType = candidate.GeometryType,
            Fields = candidate.Fields.ToList(),
            SchemaFingerprint = candidate.SchemaFingerprint,
            LineageRoot = candidate.LineageRoot,
            LineageDepth = candidate.LineageDepth
        };

        private static InvocationEffect ReadEffect(string layer) => new()
        {
            Profile = new EffectProfile { Effects = DataEffectKind.ReadData },
            Target = new ApprovalTarget { Layer = layer }
        };
    }
}
