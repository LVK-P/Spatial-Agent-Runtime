using FluentAssertions;
using SpatialAgent.Services;
using SpatialAgent.Tools.Core;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    /// <summary>
    /// cc_map_field_values（2026-07-19 用户裁定）：字段映射赋值遵循 Pro
    /// "计算字段+Python code_block"标准形态——合并同类项扫唯一值、
    /// 查官方对照、缺失保原值、未映射/歧义如实清单。模型自由度收敛为
    /// 填参数，忘查表/手写表达式错/空值兜底三类事故被结构性消灭。
    /// </summary>
    /// <remarks>English：cc_map_field_values (User ruling on 2026-07-19): Field mapping assignment follows the Pro standard "computed field + Python code_block" format—merge similar items to scan unique values, check official comparisons, preserve original missing values, and provide a truthful list of unmapped/ambiguous items. The model's degrees of freedom converge to fill in parameters, forgetting to look up tables, handwriting errors, and empty value backup are structurally eliminated.</remarks>
    public class MapFieldValuesToolTests
    {
        // ── BuildMapping：官方对照 ──
        // English：BuildMapping: Official comparison

        [Fact]
        public void Name_to_code_maps_via_official_table()
        {
            var r = MapFieldValuesTool.BuildMapping(
                new[] { "公路用地", "农村宅基地" }, "yh_name_to_code", null);

            r.Error.Should().BeNull();
            r.Matched.Should().Contain("公路用地", "1202");
            r.Matched.Should().Contain("农村宅基地", "0703");
            r.Unmatched.Should().BeEmpty();
        }

        [Fact]
        public void Unknown_names_go_to_unmatched_not_fabricated()
        {
            var r = MapFieldValuesTool.BuildMapping(
                new[] { "公路用地", "城镇道路用地" }, "yh_name_to_code", null);

            // "城镇道路用地"不是 2023 版规范名（规范名"城镇村道路用地"）——如实未命中
            // English："Urban road land" is not the 2023 standard name (the standard name "urban village road land")—it is not accurate
            r.Matched.Should().ContainKey("公路用地");
            r.Unmatched.Should().ContainSingle().Which.Should().Be("城镇道路用地");
        }

        [Fact]
        public void Ambiguous_same_name_multi_code_is_listed_not_guessed()
        {
            // 三调"建制镇"对应 202/202A——一名多码不猜
            // English：Third adjustment "Established Town" corresponds to 202/202A—one name with multiple codes, no guessing
            var r = MapFieldValuesTool.BuildMapping(
                new[] { "建制镇" }, "sd_name_to_code", null);

            r.Matched.Should().BeEmpty();
            r.Ambiguous.Should().ContainSingle();
            r.Ambiguous[0].Key.Should().Be("建制镇");
            r.Ambiguous[0].Value.Should().Contain(new[] { "202", "202A" });
        }

        [Fact]
        public void Code_to_name_direction_works()
        {
            var r = MapFieldValuesTool.BuildMapping(
                new[] { "1207", "0703" }, "yh_code_to_name", null);

            r.Matched.Should().Contain("1207", "城镇村道路用地");
            r.Matched.Should().Contain("0703", "农村宅基地");
        }

        // ── BuildMapping：显式 mapping ──
        // English：BuildMapping: Explicit mapping

        [Fact]
        public void Explicit_mapping_overrides_official_table()
        {
            var r = MapFieldValuesTool.BuildMapping(
                new[] { "甲类", "乙类" }, "yh_name_to_code", "{\"甲类\":\"A1\"}");

            r.Matched.Should().Contain("甲类", "A1");
            r.Unmatched.Should().ContainSingle().Which.Should().Be("乙类");
        }

        [Fact]
        public void Invalid_explicit_mapping_json_fails_explicitly()
        {
            var r = MapFieldValuesTool.BuildMapping(
                new[] { "甲类" }, "yh_name_to_code", "not-json{{");

            r.Error.Should().NotBeNull();
        }

        // ── BuildCodeBlock：Pro 计算字段标准形态 ──
        // English：BuildCodeBlock: The standard form of the Pro calculation field

        [Fact]
        public void Code_block_preserves_current_value_on_missing_mapping()
        {
            string code = MapFieldValuesTool.BuildCodeBlock(
                new Dictionary<string, string> { ["公路用地"] = "1202" });

            code.Should().Contain("def _sa_map(v, cur):");
            code.Should().Contain("return m.get(str(v).strip(), cur)");   // 保留原值的回退。 / Preserve-value fallback.
            code.Should().NotContain(", None)");                          // 禁止空值回退。 / Forbid a null-value fallback.
            code.Should().NotContain(", \"\")");                          // 禁止空字符串回退。 / Forbid an empty-string fallback.
        }

        [Fact]
        public void Code_block_escapes_quotes_in_values()
        {
            string code = MapFieldValuesTool.BuildCodeBlock(
                new Dictionary<string, string> { ["含\"引号\"名"] = "X1" });

            code.Should().Contain("\\\"引号\\\"");   // JSON 转义，与 Python 兼容。 / JSON escaping compatible with Python.
        }
    }

    /// <summary>
    /// cc_read_rows 保守调用门槛（2026-07-19 用户裁定：小样本核对专用，
    /// 超限拒绝而非截断——部分可见比不可见更误导）。
    /// </summary>
    /// <remarks>English：cc_read_rows Conservative call threshold (2026-07-19 user rule: few-sample verification only, overlimit denial rather than truncation—partially visible is more misleading than invisible).</remarks>
    public class ReadRowsGateTests
    {
        [Fact]
        public void Fields_over_limit_are_rejected_with_guidance()
        {
            string? err = ReadRowsTool.ValidateRequest("道路面", "A,B,C,D", out _);

            err.Should().NotBeNull();
            err.Should().Contain("合并同类项");
        }

        [Fact]
        public void Two_fields_for_pairwise_check_pass()
        {
            string? err = ReadRowsTool.ValidateRequest("道路面", "YDYHMC、YDYHBM", out var fields);

            err.Should().BeNull();
            fields.Should().BeEquivalentTo(new[] { "YDYHMC", "YDYHBM" });
        }

        [Fact]
        public void Duplicate_field_names_are_collapsed()
        {
            ReadRowsTool.ValidateRequest("L", "A,a,A", out var fields);
            fields.Should().ContainSingle();
        }

        [Fact]
        public void Empty_inputs_are_rejected()
        {
            ReadRowsTool.ValidateRequest("", "A", out _).Should().NotBeNull();
            ReadRowsTool.ValidateRequest("L", " ", out _).Should().NotBeNull();
        }

        [Fact]
        public void Row_cap_matches_user_mandate()
        {
            ReadRowsTool.MaxRows.Should().Be(10);
            ReadRowsTool.MaxFields.Should().Be(3);
        }
    }
}
