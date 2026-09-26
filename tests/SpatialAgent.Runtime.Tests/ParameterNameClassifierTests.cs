using FluentAssertions;
using SpatialAgent.Runtime;
using SpatialAgent.Core;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    public class ParameterNameClassifierTests
    {
        [Theory]
        [InlineData("out_feature_class", true)]
        [InlineData("out_table", true)]
        [InlineData("output", true)]
        [InlineData("Output_Features", true)]
        [InlineData("derived_output", true)]
        [InlineData("out_dataset", true)]
        [InlineData("out_coor_system", false)]
        [InlineData("output_coordinate_system", false)]
        [InlineData("output_type", false)]
        [InlineData("out_cell_size", false)]
        [InlineData("in_features", false)]
        [InlineData("layer_name", false)]
        [InlineData("field_name", false)]
        [InlineData(null, false)]
        [InlineData("", false)]
        [InlineData("  ", false)]
        public void IsOutputParameter_classifies_correctly(string? name, bool expected)
        {
            ParameterNameClassifier.IsOutputParameter(name).Should().Be(expected);
        }

        [Theory]
        [InlineData("in_features", true)]
        [InlineData("in_table", true)]
        [InlineData("input_layer", true)]
        [InlineData("source_features", true)]
        [InlineData("clip_features", true)]
        // Fix NC-CX5: case_field should NOT be blocked
        [InlineData("case_field", false)]
        [InlineData("case_fields", false)]
        // Fields/settings should NOT be classified as data sources
        [InlineData("field_name", false)]
        [InlineData("expression", false)]
        [InlineData("where_clause", false)]
        [InlineData("sql_expression", false)]
        [InlineData("method_type", false)]
        [InlineData("distance_value", false)]
        [InlineData("out_features", false)]
        [InlineData("output_table", false)]
        [InlineData(null, false)]
        [InlineData("", false)]
        public void LooksLikeInputDataSource_classifies_correctly(string? key, bool expected)
        {
            ParameterNameClassifier.LooksLikeInputDataSource(key).Should().Be(expected);
        }

        [Theory]
        [InlineData("in_features", true)]
        [InlineData("input_table", true)]
        [InlineData("source_dataset", true)]
        [InlineData("layer_name", true)]
        [InlineData("raster_name", true)]
        [InlineData("workspace_name", true)]
        [InlineData("features", true)]
        [InlineData("field_name", false)]
        [InlineData("expression", false)]
        [InlineData("out_table", false)]
        public void IsLikelyInputParameter_classifies_correctly(string? name, bool expected)
        {
            ParameterNameClassifier.IsLikelyInputParameter(name).Should().Be(expected);
        }
    }
}
