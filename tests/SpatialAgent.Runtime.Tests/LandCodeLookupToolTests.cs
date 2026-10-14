using FluentAssertions;
using Newtonsoft.Json.Linq;
using SpatialAgent.Knowledge;
using SpatialAgent.Runtime.Contracts;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    /// <summary>
    /// cc_lookup_land_code：把官方分类对照表暴露为工具。此前模型做
    /// ydyhmc/ydyhbm 转换时拿不到权威映射，只能编造或从其他图层猜——
    /// 2026-07-18 字段清空事故的行为根源。
    /// </summary>
    /// <remarks>English：cc_lookup_land_code: Exposing the official classification comparison table as a tool. Previously, when the model performed ydyhmc/ydyhbm conversion, it couldn't obtain authoritative mappings and could only fabricate or guess from other layers—2026-07-18 Field Clearing Incident Root Cause.</remarks>
    public class LandCodeLookupToolTests
    {
        private static async Task<JObject> RunAsync(string query, string? system = null, string? direction = null)
        {
            var tool = new LandCodeLookupTool();
            var args = new Dictionary<string, object?> { ["query"] = query };
            if (system != null) args["system"] = system;
            if (direction != null) args["direction"] = direction;

            var result = await tool.ExecuteAsync(new ToolCall { Name = tool.Name, Arguments = args });
            result.Success.Should().BeTrue(result.Error);
            return JObject.Parse(result.Output!);
        }

        [Fact]
        public async Task Code_to_name_lookup_in_yh_system()
        {
            var o = await RunAsync("1202");   // 用地用海：公路用地。 / Land-and-sea use: highway land.

            var match = (JArray)o["matches"]!;
            match.Should().HaveCount(1);
            match[0]!["code"]!.ToString().Should().Be("1202");
            match[0]!["name"]!.ToString().Should().Be("公路用地");
        }

        [Fact]
        public async Task Name_to_code_exact_match()
        {
            var o = await RunAsync("农村宅基地", system: "yh");

            var match = (JArray)o["matches"]!;
            match.Should().HaveCount(1);
            match[0]!["code"]!.ToString().Should().Be("0703");
        }

        [Fact]
        public async Task Same_name_differs_between_yh_and_sd_systems()
        {
            // 同名"农村宅基地"：用地用海 0703，三调 0702——凭记忆写码必错的典型
            // English：Named "rural homesteads": land and sea use 0703, third adjustment 0702—typical cases where code writing is inevitably mistaken by memory
            var sd = await RunAsync("农村宅基地", system: "sd");

            var match = (JArray)sd["matches"]!;
            match.Should().HaveCount(1);
            match[0]!["code"]!.ToString().Should().Be("0702");
        }

        [Fact]
        public async Task Multiple_queries_split_on_chinese_separators()
        {
            var o = await RunAsync("公路用地、1201");

            var matches = (JArray)o["matches"]!;
            matches.Should().HaveCount(2);
            matches.Select(m => m["code"]!.ToString()).Should().BeEquivalentTo("1202", "1201");
        }

        [Fact]
        public async Task Unknown_items_are_reported_not_fabricated()
        {
            var o = await RunAsync("不存在的地类xyz");

            ((JArray)o["matches"]!).Should().BeEmpty();
            ((JArray)o["unmatched"]!).Select(t => t.ToString())
                .Should().ContainSingle().Which.Should().Be("不存在的地类xyz");
            o["note"]!.ToString().Should().Contain("不得编造");
        }

        [Fact]
        public async Task Fuzzy_name_returns_candidates_not_a_guess()
        {
            var o = await RunAsync("宅基地", system: "yh");   // 非精确名称返回候选列表。 / A non-exact name returns candidates.

            ((JArray)o["matches"]!).Should().BeEmpty();
            var candidates = (JArray)o["candidates"]!;
            candidates.Should().HaveCount(1);
            var options = (JArray)candidates[0]!["options"]!;
            options.Select(op => op["code"]!.ToString()).Should().Contain("0703");
        }

        [Fact]
        public async Task Empty_query_fails_explicitly()
        {
            var tool = new LandCodeLookupTool();
            var result = await tool.ExecuteAsync(new ToolCall
            {
                Name = tool.Name,
                Arguments = new() { ["query"] = "  " }
            });

            result.Success.Should().BeFalse();
        }

        [Fact]
        public void Domain_tables_are_never_empty_regardless_of_external_json()
        {
            // 数据完整性守卫：空壳 land_classification.json 曾把空字典当有效覆盖，
            // 静默清空全部领域数据。无论外部 JSON 存在与否，表必须非空。
            // English：Data integrity guardian: Empty Shell land_classification.json used to treat empty words as effective override, silently clearing all domain data. Regardless of the presence or absence of an external JSON, the table must be non-empty.
            SpatialAgent.Knowledge.Data.LandClassificationData.YhCodeToName.Count.Should().BeGreaterThan(100);
            SpatialAgent.Knowledge.Data.LandClassificationData.SdCodeToName.Count.Should().BeGreaterThan(50);
        }
    }
}
