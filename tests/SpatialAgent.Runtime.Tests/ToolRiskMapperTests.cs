using FluentAssertions;
using SpatialAgent.Runtime.Contracts;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    public class ToolRiskMapperTests
    {
        [Theory]
        [InlineData(ToolAnnotation.ReadOnly, ToolRiskLevel.ReadOnly)]
        [InlineData(ToolAnnotation.Idempotent, ToolRiskLevel.ReadOnly)]
        [InlineData(ToolAnnotation.Modify, ToolRiskLevel.Modify)]
        [InlineData(ToolAnnotation.Destructive, ToolRiskLevel.Destructive)]
        public void FromAnnotation_maps_correctly(ToolAnnotation annotation, ToolRiskLevel expected)
        {
            ToolRiskMapper.FromAnnotation(annotation).Should().Be(expected);
        }

        [Theory]
        [InlineData(ToolRiskLevel.ReadOnly, ToolAnnotation.ReadOnly)]
        [InlineData(ToolRiskLevel.Modify, ToolAnnotation.Modify)]
        [InlineData(ToolRiskLevel.Destructive, ToolAnnotation.Destructive)]
        public void ToAnnotation_maps_correctly(ToolRiskLevel risk, ToolAnnotation expected)
        {
            ToolRiskMapper.ToAnnotation(risk).Should().Be(expected);
        }

        [Theory]
        [InlineData("readonly", ToolRiskLevel.ReadOnly)]
        [InlineData("read_only", ToolRiskLevel.ReadOnly)]
        [InlineData("read-only", ToolRiskLevel.ReadOnly)]
        [InlineData("read", ToolRiskLevel.ReadOnly)]
        [InlineData("modify", ToolRiskLevel.Modify)]
        [InlineData("write", ToolRiskLevel.Modify)]
        [InlineData("writes", ToolRiskLevel.Modify)]
        [InlineData("increment", ToolRiskLevel.Increment)]
        [InlineData("create", ToolRiskLevel.Increment)]
        [InlineData("creates", ToolRiskLevel.Increment)]
        [InlineData("destructive", ToolRiskLevel.Destructive)]
        [InlineData("delete", ToolRiskLevel.Destructive)]
        [InlineData("danger", ToolRiskLevel.Destructive)]
        public void TryParse_recognizes_all_variants(string input, ToolRiskLevel expected)
        {
            ToolRiskMapper.TryParse(input, out var riskLevel).Should().BeTrue();
            riskLevel.Should().Be(expected);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("  ")]
        [InlineData("unknown")]
        [InlineData("ambiguous")]
        public void TryParse_returns_false_for_invalid(string? input)
        {
            ToolRiskMapper.TryParse(input, out _).Should().BeFalse();
        }

        [Fact]
        public void FromSerialized_with_valid_string_returns_parsed_value()
        {
            ToolRiskMapper.FromSerialized("readonly", false)
                .Should().Be(ToolRiskLevel.ReadOnly);
        }

        [Fact]
        public void FromSerialized_with_invalid_and_destructive_fallback_returns_destructive()
        {
            ToolRiskMapper.FromSerialized(null, destructiveFallback: true)
                .Should().Be(ToolRiskLevel.Destructive);
        }

        [Fact]
        public void FromSerialized_with_invalid_and_no_fallback_returns_default()
        {
            ToolRiskMapper.FromSerialized("garbage", destructiveFallback: false, defaultRisk: ToolRiskLevel.ReadOnly)
                .Should().Be(ToolRiskLevel.ReadOnly);
        }

        [Fact]
        public void ToSerializedName_returns_pascal_case()
        {
            ToolRiskMapper.ToSerializedName(ToolRiskLevel.ReadOnly).Should().Be("ReadOnly");
            ToolRiskMapper.ToSerializedName(ToolRiskLevel.Destructive).Should().Be("Destructive");
        }
    }
}
