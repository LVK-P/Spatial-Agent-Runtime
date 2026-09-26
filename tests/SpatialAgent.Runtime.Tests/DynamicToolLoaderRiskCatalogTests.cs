using FluentAssertions;
using SpatialAgent.Runtime;
using SpatialAgent.Runtime.Contracts;
using SpatialAgent.Services;
using SpatialAgent.Tools.Core;
using SpatialAgent.Adapters.ArcGIS;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    [Collection("GP dispatcher")]
    public class DynamicToolLoaderRiskCatalogTests
    {
        private sealed class InlineDispatcher : IGisDispatcher
        {
            public Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken ct = default) =>
                operation();
            public Task RunAsync(Func<Task> operation, CancellationToken ct = default) =>
                operation();
        }

        [Fact]
        public void LoadAndRegister_honors_explicit_manifest_risk_without_bundled_catalog()
        {
            string manifestPath = Path.Combine(Path.GetTempPath(), $"tool_manifest_{Guid.NewGuid():N}.json");

            try
            {
                GPToolRunner.DefaultDispatcher = new InlineDispatcher();
                File.WriteAllText(manifestPath, """
                {
                  "Local Tools.tbx": [
                    {
                      "name": "Synthetic Writer",
                      "function_name": "local_SyntheticWriter",
                      "display_name": "Synthetic Writer",
                      "execution_name": "local.SyntheticWriter",
                      "summary": "test",
                      "description": "test",
                      "destructive": true,
                      "risk_level": "Destructive",
                      "params": [
                        {
                          "name": "input_value",
                          "type": "string",
                          "title": "Input Value",
                          "description": "Input Value",
                          "required": true,
                          "direction": "Input",
                          "parameter_type": "Required",
                          "order": 0
                        }
                      ]
                    }
                  ]
                }
                """);

                var registry = new ToolRegistry();
                int count = DynamicToolLoader.LoadAndRegister(manifestPath, registry);

                count.Should().Be(1);

                var descriptor = registry.GetDescriptor("local_SyntheticWriter");
                descriptor.Should().NotBeNull();
                descriptor!.Annotation.Should().Be(ToolAnnotation.Destructive);

                var safety = descriptor.Should().BeAssignableTo<IToolSafetyDescriptor>().Subject;
                safety.SafetyProfile.DeclaredRisk.Should().Be(ToolRiskLevel.Destructive);
                var effects = descriptor.Should().BeAssignableTo<IToolEffectDescriptor>().Subject;
                effects.EffectProfile.Id.Should().NotBe("unknown");
                effects.EffectProfile.Source.Should().Be("manifest");
                effects.EffectProfile.RequiresBackup.Should().BeTrue();
            }
            finally
            {
                GPToolRunner.DefaultDispatcher = null;
                if (File.Exists(manifestPath))
                    File.Delete(manifestPath);
            }
        }

        [Fact]
        public void LoadAndRegister_maps_snake_case_capability_claims()
        {
            string manifestPath = Path.Combine(
                Path.GetTempPath(),
                $"tool_manifest_{Guid.NewGuid():N}.json");

            try
            {
                GPToolRunner.DefaultDispatcher = new InlineDispatcher();
                File.WriteAllText(manifestPath, """
                {
                  "Test Tools.tbx": [
                    {
                      "name": "Measure Surface",
                      "function_name": "test_MeasureSurface",
                      "execution_name": "test.MeasureSurface",
                      "summary": "test",
                      "risk_level": "ReadOnly",
                      "capability_claims": [
                        {
                          "capability_id": "gis.measure.area",
                          "applicable_geometry_types": ["polygon", "POLYGON"],
                          "requires_postcondition_evidence": true,
                          "evidence": "audited-manifest",
                          "confidence": 1.5
                        }
                      ],
                      "params": [
                        {
                          "name": "in_surface",
                          "type": "string",
                          "required": true,
                          "direction": "Input",
                          "parameter_type": "Required",
                          "order": 0
                        }
                      ]
                    }
                  ]
                }
                """);

                var registry = new ToolRegistry();
                DynamicToolLoader.LoadAndRegister(manifestPath, registry)
                    .Should().Be(1);

                IToolDescriptor descriptor =
                    registry.GetDescriptor("test_MeasureSurface")!;
                var capabilities = descriptor.Should()
                    .BeAssignableTo<IToolCapabilityDescriptor>()
                    .Subject
                    .CapabilityClaims;
                capabilities.Should().ContainSingle();
                ToolCapabilityClaim claim = capabilities.Single();
                claim.CapabilityId.Should().Be("gis.measure.area");
                claim.ApplicableGeometryTypes.Should().Equal("polygon");
                claim.RequiresPostconditionEvidence.Should().BeTrue();
                claim.Evidence.Should().Be("audited-manifest");
                claim.Confidence.Should().Be(1);
            }
            finally
            {
                GPToolRunner.DefaultDispatcher = null;
                if (File.Exists(manifestPath))
                    File.Delete(manifestPath);
            }
        }
    }
}
