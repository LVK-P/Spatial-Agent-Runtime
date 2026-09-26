using FluentAssertions;
using NSubstitute;
using SpatialAgent.Runtime.Contracts;
using SpatialAgent.Services;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    public class DataInfluenceEstimatorTests
    {
        [Fact]
        public async Task EstimateAsync_uses_selection_scope_when_present()
        {
            var mapReader = Substitute.For<IMapReader>();
            mapReader.DescribeLayerAsync("layer1").Returns(
                "{\"name\":\"layer1\",\"featureCount\":200,\"fields\":[{\"name\":\"TQMJ\",\"null_count\":20,\"sample\":{\"values\":[{\"value\":\"1\",\"count\":80}]}}]}");
            mapReader.GetSelectionAsync().Returns(
                "{\"total\":50,\"selections\":[{\"layer\":\"layer1\",\"count\":50}]}");
            mapReader.ReadRowsAsync("layer1", Arg.Any<IReadOnlyList<string>>(), null, 50, true)
                .Returns("{\"layer\":\"layer1\",\"selected_only\":true,\"row_count\":50,\"rows\":["
                    + string.Join(",", Enumerable.Range(1, 40).Select(_ => "{\"TQMJ\":\"1\"}"))
                    + ","
                    + string.Join(",", Enumerable.Range(1, 10).Select(_ => "{\"TQMJ\":null}"))
                    + "]}");

            var estimator = new DataInfluenceEstimator(mapReader);

            var estimate = await estimator.EstimateAsync(new ToolCall
            {
                Name = "management_CalculateField",
                Arguments = new()
                {
                    ["in_table"] = "layer1",
                    ["field_name"] = "TQMJ"
                }
            });

            estimate.IsReliable.Should().BeTrue();
            estimate.ScopeObjectCount.Should().Be(50);
            estimate.TargetFieldCount.Should().Be(1);
            estimate.ScopeValueCount.Should().Be(50);
            estimate.UsesSelectionScope.Should().BeTrue();
            estimate.ExistingNonEmptyTargetCount.Should().Be(40);
            estimate.NewValueFillCount.Should().Be(10);
            estimate.EffectiveCount.Should().Be(40);
            estimate.Source.Should().Be(EvidenceSource.AttributeTableProbe);
        }

        [Fact]
        public async Task EstimateAsync_missing_field_is_treated_as_first_fill_and_schema_change()
        {
            var mapReader = Substitute.For<IMapReader>();
            mapReader.DescribeLayerAsync("layer1").Returns(
                "{\"name\":\"layer1\",\"featureCount\":120,\"fields\":[{\"name\":\"NAME\",\"null_count\":0}]}");
            mapReader.GetSelectionAsync().Returns("{\"total\":0,\"selections\":[]}");

            var estimator = new DataInfluenceEstimator(mapReader);

            var estimate = await estimator.EstimateAsync(new ToolCall
            {
                Name = "cc_calculate_area",
                Arguments = new()
                {
                    ["layer_name"] = "layer1",
                    ["field_name"] = "TQMJ"
                }
            });

            estimate.IsReliable.Should().BeTrue();
            estimate.ScopeObjectCount.Should().Be(120);
            estimate.ExistingNonEmptyTargetCount.Should().Be(0);
            estimate.NewValueFillCount.Should().Be(120);
            estimate.SchemaMutationCount.Should().Be(1);
            estimate.IsIncrementEffect.Should().BeTrue();
        }

        [Fact]
        public async Task EstimateAsync_aggregates_non_empty_overwrite_count_across_multiple_fields()
        {
            var mapReader = Substitute.For<IMapReader>();
            mapReader.DescribeLayerAsync("layer1").Returns(
                "{\"name\":\"layer1\",\"featureCount\":4,\"fields\":[{\"name\":\"F1\",\"null_count\":1,\"sample\":{\"values\":[{\"value\":\"1\",\"count\":3}]}},{\"name\":\"F2\",\"null_count\":2,\"sample\":{\"values\":[{\"value\":\"1\",\"count\":2}]}}]}");
            mapReader.GetSelectionAsync().Returns("{\"total\":0,\"selections\":[]}");
            mapReader.ReadRowsAsync("layer1", Arg.Any<IReadOnlyList<string>>(), null, 4, false)
                .Returns("{\"layer\":\"layer1\",\"selected_only\":false,\"row_count\":4,\"rows\":[" +
                    "{\"F1\":\"1\",\"F2\":null}," +
                    "{\"F1\":\"1\",\"F2\":\"1\"}," +
                    "{\"F1\":null,\"F2\":\"1\"}," +
                    "{\"F1\":\"1\",\"F2\":null}" +
                    "]}");

            var estimator = new DataInfluenceEstimator(mapReader);

            var estimate = await estimator.EstimateAsync(new ToolCall
            {
                Name = "management_CalculateField",
                Arguments = new()
                {
                    ["in_table"] = "layer1",
                    ["fields"] = "F1,F2"
                }
            });

            estimate.IsReliable.Should().BeTrue();
            estimate.FieldNames.Should().Equal("F1", "F2");
            estimate.TargetFieldCount.Should().Be(2);
            estimate.ScopeObjectCount.Should().Be(4);
            estimate.ScopeValueCount.Should().Be(8);
            estimate.ExistingNonEmptyTargetCount.Should().Be(5);
            estimate.NewValueFillCount.Should().Be(3);
            estimate.IsIncrementEffect.Should().BeFalse();
        }

        [Fact]
        public async Task EstimateAsync_fieldless_call_fails_closed_with_scope_context()
        {
            var mapReader = Substitute.For<IMapReader>();
            mapReader.DescribeLayerAsync("layer1").Returns(
                "{\"name\":\"layer1\",\"featureCount\":147,\"fields\":[{\"name\":\"NAME\",\"null_count\":0}]}");
            mapReader.GetSelectionAsync().Returns("{\"total\":0,\"selections\":[]}");

            var estimator = new DataInfluenceEstimator(mapReader);

            var estimate = await estimator.EstimateAsync(new ToolCall
            {
                Name = "cc_calculate_area",
                Arguments = new()
                {
                    ["layer_name"] = "layer1"
                }
            });

            estimate.IsReliable.Should().BeFalse();
            estimate.ScopeObjectCount.Should().Be(147);
            estimate.FieldName.Should().BeNull();
            estimate.EffectiveCount.Should().Be(147);
        }
    }
}
