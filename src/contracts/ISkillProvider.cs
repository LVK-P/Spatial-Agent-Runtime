using System.Threading.Tasks;

namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Skill matching provider. Concrete implementation in Runtime (SkillManager).
    /// </summary>
    /// <remarks>中文：技能匹配提供者。运行时（SkillManager）中的具体实现。</remarks>
    public interface ISkillProvider
    {
        int Count { get; }
        string GetSkillsSummary();
        List<MatchedSkill> MatchSkills(string userInput);
        Task<List<MatchedSkill>> MatchSkillsAsync(string userInput);
    }

    public class MatchedSkill
    {
        public string Name { get; set; } = "";
        public string Summary { get; set; } = "";
        public string Content { get; set; } = "";

        /// <summary>
        /// Tools this skill's workflow depends on (schema names). Merged into the
        /// exposed-tool set when the skill matches — a skill that references tools
        /// the model cannot call is dead text (e.g. topology chain tools are never
        /// in the context-filtered top-50 unless a skill pulls them in).
        /// </summary>
        /// <remarks>中文：这项技能的工作流程依赖的工具（模式名称）。当技能匹配时合并到暴露工具集中——如果技能引用模型无法调用的工具，则是死文本（例如拓扑链工具除非技能拉入上下文过滤的前50名）。</remarks>
        public List<string> RequiredTools { get; set; } = new();
    }
}
