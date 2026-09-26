using FluentAssertions;
using SpatialAgent.Core;
using SpatialAgent.Runtime;
using SpatialAgent.Runtime.Contracts;
using SpatialAgent.Services;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    public sealed class ParameterScopedProofTests
    {
        [Fact]
        public void Goal_contract_and_plan_step_preserve_method_unit_precision_and_rounding()
        {
            var domain = new DomainContext
            {
                Layers = new()
                {
                    new LayerInfo
                    {
                        Name = "roads",
                        MapMemberId = "map-roads",
                        DatasetId = "dataset-roads",
                        GeometryType = "Polygon",
                        Fields = new() { "OBJECTID", "AREA_HA" },
                        SchemaFingerprint = "schema-roads"
                    }
                }
            };
            CompiledGoalContract contract = new DefaultGoalContractCompiler().Compile(
                "calculate projected area for roads in hectares, round to 2 decimal places using half up",
                domain);
            GoalProofObligation proof = contract.ProofObligations.Single(item =>
                item.Kind == GoalProofKind.ParameterSemantics
                && item.CapabilityId == GisCapabilityIds.MeasureArea);

            proof.Expected.Should().Contain("measurement_method", "PLANAR");
            proof.Expected.Should().Contain("unit", "HECTARES");
            proof.Expected.Should().Contain("decimal_places", "2");
            proof.Expected.Should().Contain("rounding_mode", "HALF_UP");

            var step = new PlanStep
            {
                Id = 1,
                ToolCall = new ToolCall
                {
                    Id = "area-call",
                    Name = "logical-area",
                    Arguments = new()
                    {
                        ["in_features"] = "roads",
                        ["field_name"] = "AREA_HA"
                    }
                },
                Effect = new InvocationEffect
                {
                    Target = new ApprovalTarget
                    {
                        Layer = "roads",
                        Field = "AREA_HA"
                    }
                },
                ProvidedCapabilities = new() { GisCapabilityIds.MeasureArea }
            };

            List<ParameterScopedProofRequirement> requirements =
                ParameterScopedProofBuilder.Build(
                    step,
                    descriptor: null,
                    contract.ParameterConstraints,
                    contract.ProofObligations,
                    domain);

            requirements.Should().ContainSingle().Which.Should().BeEquivalentTo(
                new ParameterScopedProofRequirement
                {
                    ProofObligationId = proof.Id,
                    CapabilityId = GisCapabilityIds.MeasureArea,
                    MeasurementKind = MeasurementProofKind.Area,
                    Layer = "roads",
                    DatasetId = "dataset-roads",
                    Field = "AREA_HA",
                    Unit = "HECTARES",
                    MeasurementMethod = "PLANAR",
                    DecimalPlaces = 2,
                    RoundingMode = "HALF_UP",
                    SampleSize = 64
                });
        }

        [Fact]
        public async Task Exact_parameter_proof_closes_only_its_exact_obligation()
        {
            var probe = new ContractCheckingProbe();
            var rule = new PostConditionValidationRule(probe);
            ParameterScopedProofRequirement requirement = Requirement();
            var observation = new Observation
            {
                StepId = 3,
                ToolName = "logical-area",
                Output = new ObservationOutput { Raw = "{}" },
                ProofRequirements = new[] { requirement }
            };

            ValidationResult validation = await rule.ValidateAsync(observation);
            var result = new StepExecutionResult
            {
                StepId = 3,
                Status = StepResultStatus.Completed,
                ProvidedCapabilities = new() { GisCapabilityIds.MeasureArea },
                Validations = new() { validation }
            };
            var goal = new ResolvedGoalScope
            {
                ProofObligations = new()
                {
                    new GoalProofObligation
                    {
                        Id = requirement.ProofObligationId,
                        Kind = GoalProofKind.ParameterSemantics,
                        CapabilityId = GisCapabilityIds.MeasureArea,
                        Required = true,
                        RequiresValidation = true
                    }
                }
            };

            validation.Verdict.Should().Be(ValidationVerdict.PASS);
            validation.SatisfiedProofObligationIds.Should().Equal(
                requirement.ProofObligationId);
            new DefaultGoalClosureEvaluator()
                .Evaluate(goal, coverage: null, new[] { result })
                .MissingProofs.Should().BeEmpty();
        }

        [Theory]
        [InlineData("unit")]
        [InlineData("method")]
        [InlineData("rounding")]
        public async Task Wrong_parameter_semantics_never_return_pass(string fault)
        {
            ParameterScopedProofRequirement requirement = Requirement();
            switch (fault)
            {
                case "unit":
                    requirement.Unit = "SQUARE_METERS";
                    break;
                case "method":
                    requirement.MeasurementMethod = "GEODESIC";
                    break;
                case "rounding":
                    requirement.RoundingMode = "TO_EVEN";
                    break;
            }
            var rule = new PostConditionValidationRule(
                new ContractCheckingProbe());
            var observation = new Observation
            {
                ToolName = "logical-area",
                Output = new ObservationOutput { Raw = "{}" },
                ProofRequirements = new[] { requirement }
            };

            ValidationResult validation = await rule.ValidateAsync(observation);

            validation.Verdict.Should().Be(ValidationVerdict.FAIL);
            validation.SatisfiedProofObligationIds.Should().BeEmpty();
        }

        [Fact]
        public void Geodesic_area_tolerance_accepts_cross_api_drift_from_pro_run()
        {
            var samples = new[]
            {
                (Expected: 23_684_207_143.29, ObservedDrift: 132.37),
                (Expected: 549_991.34, ObservedDrift: 0.62),
                (Expected: 12_146.77, ObservedDrift: 0.05)
            };

            foreach (var sample in samples)
            {
                double allowed = ParameterScopedProofTolerance
                    .CalculateAllowedAbsoluteError(
                        MeasurementProofKind.Area,
                        "GEODESIC",
                        2,
                        sample.Expected);
                sample.ObservedDrift.Should().BeLessThan(allowed);
            }
        }

        [Theory]
        [InlineData(MeasurementProofKind.Length, "GEODESIC")]
        [InlineData(MeasurementProofKind.Area, "PLANAR")]
        public void Non_geodesic_area_tolerance_remains_rounding_strict(
            MeasurementProofKind kind,
            string method)
        {
            ParameterScopedProofTolerance.CalculateAllowedAbsoluteError(
                    kind,
                    method,
                    2,
                    549_991.34)
                .Should().BeApproximately(0.005, 1e-12);
        }

        private static ParameterScopedProofRequirement Requirement() => new()
        {
            ProofObligationId = "proof:parameter:measure.area",
            CapabilityId = GisCapabilityIds.MeasureArea,
            MeasurementKind = MeasurementProofKind.Area,
            Layer = "roads",
            DatasetId = "dataset-roads",
            Field = "AREA_HA",
            Unit = "HECTARES",
            MeasurementMethod = "PLANAR",
            DecimalPlaces = 2,
            RoundingMode = "HALF_UP",
            SampleSize = 64
        };

        private sealed class ContractCheckingProbe : IPostConditionProbe
        {
            public Task<ParameterScopedProofResult> VerifyParameterScopedProofAsync(
                ParameterScopedProofRequirement requirement)
            {
                bool passed = requirement.Unit == "HECTARES"
                    && requirement.MeasurementMethod == "PLANAR"
                    && requirement.DecimalPlaces == 2
                    && requirement.RoundingMode == "HALF_UP";
                return Task.FromResult(new ParameterScopedProofResult
                {
                    Supported = true,
                    Passed = passed,
                    SampledCount = 8,
                    MismatchCount = passed ? 0 : 8,
                    MaximumAbsoluteError = passed ? 0 : 1,
                    Detail = passed
                        ? "sample values match the parameter contract"
                        : "sample values do not match the parameter contract"
                });
            }

            public Task<(bool Exists, string Detail)> VerifyExistsAsync(string target) =>
                Task.FromResult((true, "exists"));

            public Task<(bool Found, long? Count, string Detail)> GetCountAsync(string target) =>
                Task.FromResult((true, (long?)1, "count"));

            public Task<(bool Exists, string? FieldType, string Detail)> VerifyFieldAsync(
                string layerName,
                string fieldName) =>
                Task.FromResult((true, (string?)"Double", "field"));

            public Task<(bool Found, int? Wkid, string Detail)> GetSpatialReferenceAsync(
                string target) =>
                Task.FromResult((true, (int?)4490, "crs"));

            public Task<(bool Passed, string Detail)> VerifyNoOverlapAsync(
                string target,
                string expected) =>
                Task.FromResult((true, "no overlap"));

            public Task<(bool Found, long? Total, long? Populated, string Detail)>
                GetFieldPopulationAsync(string layerName, string fieldName) =>
                Task.FromResult((true, (long?)1, (long?)1, "populated"));
        }
    }
}
