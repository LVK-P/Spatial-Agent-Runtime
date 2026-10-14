using FluentAssertions;
using SpatialAgent.Runtime;
using SpatialAgent.Runtime.Contracts;
using SpatialAgent.Services;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    /// <summary>
    /// Regression tests for the 2026-07-18 field-wipe incident:
    /// CalculateField wrote None to every row of ydyhmc/ydyhbm, the GP call
    /// "succeeded", verify_field (existence-only) PASSed, and the agent
    /// reported完成. verify_field_populated closes the value blind spot, and
    /// target resolution no longer fabricates "first-two-args" field names.
    /// </summary>
    /// <remarks>中文：2026-07-18字段清除事件的回归测试：CalculateField对ydyhmc/ydyhbm的每一行写入无，GP呼叫“成功”，仅存在verify_field PASS，代理报告完成。verify_field_populated会关闭数值盲点，目标解析不再伪造“first-two-args”字段名。</remarks>
    public class FieldPopulatedVerificationTests
    {
        private sealed class FakeProbe : IPostConditionProbe
        {
            public bool FieldExists = true;
            public long Total = 10;
            public long Populated = 10;
            public List<(string Layer, string Field)> PopulationCalls { get; } = new();

            public Task<(bool Exists, string Detail)> VerifyExistsAsync(string target)
                => Task.FromResult((true, "存在"));

            public Task<(bool Found, long? Count, string Detail)> GetCountAsync(string target)
                => Task.FromResult((true, (long?)Total, "计数"));

            public Task<(bool Exists, string? FieldType, string Detail)> VerifyFieldAsync(string layerName, string fieldName)
                => Task.FromResult(FieldExists
                    ? (true, (string?)"String", $"字段「{fieldName}」存在，类型 String")
                    : (false, (string?)null, $"图层「{layerName}」中无字段「{fieldName}」"));

            public Task<(bool Found, int? Wkid, string Detail)> GetSpatialReferenceAsync(string target)
                => Task.FromResult((true, (int?)4490, "坐标系"));

            public Task<(bool Passed, string Detail)> VerifyNoOverlapAsync(string target, string expected)
                => Task.FromResult((true, "无重叠"));

            public Task<(bool Found, long? Total, long? Populated, string Detail)> GetFieldPopulationAsync(string layerName, string fieldName)
            {
                PopulationCalls.Add((layerName, fieldName));
                return Task.FromResult((true, (long?)Total, (long?)Populated,
                    $"抽样 {Total} 条记录，字段「{fieldName}」非空 {Populated} 条"));
            }
        }

        private static Observation MakeObservation(
            string toolName,
            Dictionary<string, object?> args,
            VerificationRule? toolRule = null) => new()
        {
            ToolName = toolName,
            InputParameters = args,
            ToolVerification = toolRule,
            Output = new ObservationOutput { Raw = "{}" }
        };

        // ── PostConditionVerifier 层 ──

        [Fact]
        public async Task All_empty_field_values_fail_verification()
        {
            var probe = new FakeProbe { Total = 37, Populated = 0 };
            var verifier = new PostConditionVerifier(probe);

            var (passed, detail) = await verifier.VerifyAsync(new VerificationRule
            {
                Type = "verify_field_populated",
                Target = "现状用地.YDYHMC"
            }, "{}");

            passed.Should().BeFalse();
            detail.Should().Contain("全部为空");
        }

        [Fact]
        public async Task Populated_field_passes_verification()
        {
            var probe = new FakeProbe { Total = 37, Populated = 35 };
            var verifier = new PostConditionVerifier(probe);

            var (passed, detail) = await verifier.VerifyAsync(new VerificationRule
            {
                Type = "verify_field_populated",
                Target = "现状用地.YDYHBM"
            }, "{}");

            passed.Should().BeTrue();
            detail.Should().Contain("非空");
        }

        [Fact]
        public async Task Empty_layer_skips_value_check_but_passes()
        {
            var probe = new FakeProbe { Total = 0, Populated = 0 };
            var verifier = new PostConditionVerifier(probe);

            var (passed, _) = await verifier.VerifyAsync(new VerificationRule
            {
                Type = "verify_field_populated",
                Target = "空图层.F1"
            }, "{}");

            passed.Should().BeTrue();
        }

        [Fact]
        public async Task Missing_field_fails_before_value_sampling()
        {
            var probe = new FakeProbe { FieldExists = false };
            var verifier = new PostConditionVerifier(probe);

            var (passed, _) = await verifier.VerifyAsync(new VerificationRule
            {
                Type = "verify_field_populated",
                Target = "现状用地.NOPE"
            }, "{}");

            passed.Should().BeFalse();
            probe.PopulationCalls.Should().BeEmpty();
        }

        // ── PostConditionValidationRule 目标解析层 ──

        [Fact]
        public async Task CalculateField_contract_fails_when_written_column_is_wiped()
        {
            // 事故复现：CalculateField 成功返回，但字段被整列写空 → 必须 FAIL
            // English：Incident recurrence: CalculateField returns successfully, but the field is written to the entire column → must FAIL
            var probe = new FakeProbe { Total = 37, Populated = 0 };
            var rule = new PostConditionValidationRule(probe);

            var result = await rule.ValidateAsync(MakeObservation(
                "management_CalculateField",
                new() { ["in_table"] = "现状用地", ["field_name"] = "YDYHMC", ["expression"] = "x" }));

            result.Verdict.Should().Be(ValidationVerdict.FAIL);
            probe.PopulationCalls.Should().ContainSingle()
                .Which.Should().Be(("现状用地", "YDYHMC"));
        }

        [Fact]
        public async Task Field_default_from_rule_params_resolves_composite_tool_target()
        {
            // cc_calculate_area 省略 field_name 时实际写 TQMJ——通过 Params 声明
            // English：cc_calculate_area Actually write TQMJ when omitting field_name—declared via Params
            var probe = new FakeProbe { Total = 8, Populated = 8 };
            var rule = new PostConditionValidationRule(probe);

            var result = await rule.ValidateAsync(MakeObservation(
                "cc_calculate_area",
                new() { ["layer_name"] = "道路面", ["area_type"] = "椭球面积", ["unit"] = "平方米" },
                new VerificationRule { Type = "verify_field_populated", Params = "field_default=TQMJ" }));

            result.Verdict.Should().Be(ValidationVerdict.PASS);
            probe.PopulationCalls.Should().ContainSingle()
                .Which.Should().Be(("道路面", "TQMJ"));
        }

        [Fact]
        public async Task Unresolvable_field_target_is_skipped_not_fabricated()
        {
            // 旧行为：取前两个参数值拼成 "道路面.椭球面积" 去查字段 → 18 个假失败。
            // 新行为：目标未知 → WARN 跳过，探针不被调用。
            // English：Old behavior: Combine the first two parameter values to form "road surface. Ellipsoid area" to check fields → 18 false failures. New behavior: Target unknown→ WARN skips, probe not called.
            var probe = new FakeProbe();
            var rule = new PostConditionValidationRule(probe);

            var result = await rule.ValidateAsync(MakeObservation(
                "cc_calculate_area",
                new() { ["layer_name"] = "道路面", ["area_type"] = "椭球面积" },
                new VerificationRule { Type = "verify_field" }));

            result.Verdict.Should().Be(ValidationVerdict.WARN);
            probe.PopulationCalls.Should().BeEmpty();
        }

        [Fact]
        public async Task Inverted_field_rule_reports_absence_as_the_human_expectation()
        {
            var probe = new FakeProbe { FieldExists = false };
            var bus = new EventBus();
            AgentEvent? verificationEvent = null;
            using var subscription = bus.Subscribe("verification.passed", evt =>
                verificationEvent = evt);
            var rule = new PostConditionValidationRule(probe, bus);

            var result = await rule.ValidateAsync(MakeObservation(
                "management_DeleteField",
                new() { ["in_table"] = "assets", ["drop_field"] = "legacy_code" }));

            result.Verdict.Should().Be(ValidationVerdict.PASS);
            verificationEvent.Should().NotBeNull();
            verificationEvent!.Data!["expected"].Should().Be("字段不存在于图层中");
        }
    }
}
