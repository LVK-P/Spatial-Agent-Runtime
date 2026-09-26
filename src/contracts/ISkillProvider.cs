using System.Threading.Tasks;

namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Skill matching provider. Concrete implementation in Runtime (SkillManager).
    /// </summary>
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
        public List<string> RequiredTools { get; set; } = new();
    }
}
