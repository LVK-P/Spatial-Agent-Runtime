using FluentAssertions;
using SpatialAgent.Runtime.Contracts;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    /// <summary>
    /// 技能→工具暴露联动（2026-07-19）：技能文档教流程，但所教工具若不在
    /// 暴露集，模型根本调不了（拓扑链工具从不进 top50，实测九轮 拓扑=[]）。
    /// 技能通过「## 所需工具」节声明依赖，命中时并入暴露集。
    /// </summary>
    /// <remarks>English：Skill → tool exposure linkage (2026-07-19): The skill document teaches the process, but if the tool is not in the exposure set, the model cannot be adjusted at all (topology chain tools never enter the top 50; tested nine rounds of topology = []). Skills declare dependencies through the "## Required Tools" section, and when they hit, they are merged into the exposure set.</remarks>
    public class SkillRequiredToolsTests
    {
        private static string WriteSkillDir(string content)
        {
            string dir = Directory.CreateDirectory(Path.Combine(
                Path.GetTempPath(), "spatial-agent-skills-" + Guid.NewGuid().ToString("N"))).FullName;
            File.WriteAllText(Path.Combine(dir, "test-skill.md"), content);
            return dir;
        }

        private const string SkillWithTools = @"# 拓扑测试技能

## 触发条件
- ""拓扑""、""压盖检查""

## 所需工具
- management_CreateTopology
- `management_ValidateTopology`
- management_ExportTopologyErrors
- 这一行不是合法工具名，应被过滤
- management_CreateTopology

## 执行流程
略。
";

        [Fact]
        public void Parse_extracts_required_tools_and_filters_noise()
        {
            var mgr = new SkillManager(WriteSkillDir(SkillWithTools));
            mgr.LoadAll();

            var skill = mgr.Skills.Single();
            skill.RequiredTools.Should().BeEquivalentTo(new[]
            {
                "management_CreateTopology",     // 去重后一次。 / Appears once after deduplication.
                "management_ValidateTopology",   // 反引号已移除。 / Backticks are stripped.
                "management_ExportTopologyErrors"
            });
        }

        [Fact]
        public void Matched_skill_carries_required_tools_to_context_layer()
        {
            ISkillProvider provider = new SkillManager(WriteSkillDir(SkillWithTools));
            ((SkillManager)provider).LoadAll();

            var matched = provider.MatchSkills("帮我做拓扑压盖检查");

            matched.Should().NotBeEmpty();
            matched[0].RequiredTools.Should().Contain("management_CreateTopology");
        }

        [Fact]
        public void Skill_without_tools_section_has_empty_required_tools()
        {
            var mgr = new SkillManager(WriteSkillDir("# 无工具技能\n\n## 触发条件\n- \"测试\"\n\n## 执行流程\n略。\n"));
            mgr.LoadAll();

            mgr.Skills.Single().RequiredTools.Should().BeEmpty();
        }

        [Fact]
        public void Generic_resource_noun_does_not_activate_procedural_skill()
        {
            const string content = """
# 村庄规划入库

## 触发条件
- "村庄"、"村庄规划数据入库"

## 所需工具
- management_Append
""";
            ISkillProvider provider =
                new SkillManager(WriteSkillDir(content));
            ((SkillManager)provider).LoadAll();

            var matched = provider.MatchSkills(
                "分析每个乡镇的村庄密度");

            matched.Should().BeEmpty();
        }

        [Fact]
        public void Specific_workflow_phrase_still_activates_skill()
        {
            const string content = """
# 村庄规划入库

## 触发条件
- "村庄"、"村庄规划数据入库"

## 所需工具
- management_Append
""";
            ISkillProvider provider =
                new SkillManager(WriteSkillDir(content));
            ((SkillManager)provider).LoadAll();

            var matched = provider.MatchSkills(
                "请执行村庄规划数据入库");

            matched.Should().ContainSingle();
            matched[0].RequiredTools.Should().Contain(
                "management_Append");
        }
    }
}
