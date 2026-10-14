using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SpatialAgent.Runtime.Contracts;
using SpatialAgent.Core;

// ================================================================
// SkillManager.cs — Skill loading, matching, and summarization.
//
// Loads Markdown skill files from Skills/ directory. Matching uses a
// three-layer cascade: L1 trigger-word exact match (+10), L2 keyword
// overlap (+5 name / +1 content), L3 embedding cosine similarity
// (when IEmbeddingService is injected). Top 3 are returned via RRF.
// ================================================================
// 中文：================================================================ SkillManager.cs — 技能加载、匹配与总结。从Skills/目录加载Markdown技能文件。匹配采用三层级联处理：L1触发字精确匹配（+10）、L2关键词重叠（+5个名称/+1内容）、L3嵌入余弦相似度（注入IEmbeddingService时）。前三名通过RRF返回。================================================================
namespace SpatialAgent.Runtime
{
    public class Skill
    {
        public string Name { get; set; } = "";
        public string FilePath { get; set; } = "";
        public List<string> Triggers { get; set; } = new();
        public string Content { get; set; } = "";
        public string Category { get; set; } = "general";
        public string Summary { get; set; } = "";
        public bool Enabled { get; set; } = true;

        /// <summary>Tools declared in the skill's "## 所需工具" section (schema names).</summary>
        /// <remarks>中文：工具声明在技能的“## 所需工具”部分（结构名称）。</remarks>
        public List<string> RequiredTools { get; set; } = new();
    }

    public class SkillManager : ISkillProvider
    {
        private readonly string _skillsDir;
        private readonly Func<string, Task<float[]>>? _embedAsync;
        private List<Skill> _skills = new();
        private readonly Dictionary<string, float[]> _skillVectors = new(StringComparer.OrdinalIgnoreCase);
        private bool _vectorsBuilt;

        public int Count => _skills.Count(s => s.Enabled);
        public IReadOnlyList<Skill> Skills => _skills.AsReadOnly();

        /// <summary>
        /// Create a SkillManager.
        /// </summary>
        /// <param name="skillsDir">Directory containing .md skill files.</param>
        /// <param name="embedAsync">Optional async embedding function for L3 semantic matching.
        /// Signature: text -> dense float vector. If null, only L1/L2 keyword matching is used.</param>
        /// <remarks>中文：创建一个技能管理器。包含.md技能文件的目录。可选的异步嵌入功能用于L3语义匹配。签名：文本 -&gt;密集浮点向量。如果为空，则仅使用 L1/L2 关键词匹配。</remarks>
        public SkillManager(string skillsDir, Func<string, Task<float[]>>? embedAsync = null)
        {
            _skillsDir = skillsDir;
            _embedAsync = embedAsync;
        }

        public void LoadAll()
        {
            if (!Directory.Exists(_skillsDir))
            {
                Directory.CreateDirectory(_skillsDir);
                return;
            }

            var mdFiles = Directory.GetFiles(_skillsDir, "*.md", SearchOption.AllDirectories);
            _skills.Clear();
            _skillVectors.Clear();
            _vectorsBuilt = false;

            foreach (var file in mdFiles)
            {
                try
                {
                    string content = File.ReadAllText(file);
                    var skill = ParseSkill(file, content);
                    _skills.Add(skill);
                }
                catch (Exception ex)
                {
                    AgentDiagnostics.Warn("SkillManager.LoadAll", $"Failed to load skill file {Path.GetFileName(file)}", ex);
                }
            }

            if (_embedAsync != null)
                _ = BuildVectorsAsync();
        }

        private async Task BuildVectorsAsync()
        {
            if (_embedAsync == null || _vectorsBuilt) return;

            try
            {
                var enabled = _skills.Where(s => s.Enabled).ToList();
                if (enabled.Count == 0) return;

                // Build text representations: name + triggers + summary
                // 中文：构建文本表示：名称 + 触发器 + 摘要
                var texts = enabled.Select(s => $"{s.Name}. {string.Join(", ", s.Triggers)}. {s.Summary}").ToList();
                var tasks = texts.Select(t => _embedAsync(t));
                var vectors = await Task.WhenAll(tasks);

                for (int i = 0; i < enabled.Count && i < vectors.Length; i++)
                {
                    var vec = vectors[i];
                    if (vec != null && vec.Length > 0)
                        _skillVectors[enabled[i].Name] = vec;
                }

                _vectorsBuilt = true;
            }
            catch (Exception ex)
            {
                AgentDiagnostics.Warn("SkillManager.BuildVectors", "Skill embedding build failed, falling back to keyword-only", ex);
                _vectorsBuilt = false;
            }
        }

        List<MatchedSkill> ISkillProvider.MatchSkills(string userInput)
        {
            return MatchSkillsInternal(userInput).Select(ToMatched).ToList();
        }

        async Task<List<MatchedSkill>> ISkillProvider.MatchSkillsAsync(string userInput)
        {
            var skills = await MatchSkillsInternalAsync(userInput);
            return skills.Select(ToMatched).ToList();
        }

        private static MatchedSkill ToMatched(Skill s) => new()
        {
            Name = s.Name,
            Summary = s.Summary,
            Content = s.Content,
            RequiredTools = s.RequiredTools
        };

        /// <summary>Sync fallback for L1/L2-only callers. Prefer MatchSkillsAsync when embedding is available.</summary>
        /// <remarks>中文：同步回退仅供 L1/L2 调用方使用；具备嵌入能力时优先使用 MatchSkillsAsync。</remarks>
        private List<Skill> MatchSkillsInternal(string userInput)
        {
            // L1+L2 only — no embedding. Use MatchSkillsInternalAsync for full L3 support.
            // 中文：仅L1+L2——不嵌入。使用MatchSkillsInternalAsync来实现完整的L3支持。
            var (l1l2Scores, triggerHits) = ComputeL1L2Scores(userInput);
            return RankByRrf(l1l2Scores, new Dictionary<string, double>(), triggerHits);
        }

        private async Task<List<Skill>> MatchSkillsInternalAsync(string userInput)
        {
            if (string.IsNullOrWhiteSpace(userInput))
                return new List<Skill>();

            var (l1l2Scores, triggerHits) = ComputeL1L2Scores(userInput);

            // L3 — embedding cosine similarity (async, no .Result blocking).
            // 中文：L3 — 嵌入余弦相似性（异步，无 。结果阻塞）。
            Dictionary<string, double> l3Scores = new();
            if (_vectorsBuilt && _skillVectors.Count > 0 && _embedAsync != null)
            {
                try
                {
                    var queryVec = await _embedAsync(userInput);
                    if (queryVec.Length > 0)
                    {
                        foreach (var kv in _skillVectors)
                        {
                            double cos = CosineSimilarity(queryVec, kv.Value);
                            if (cos > 0.3)
                                l3Scores[kv.Key] = cos * 10;
                        }
                    }
                }
                catch
                {
                    // Embedding failure → skip L3, use L1+L2 only.
                    // 中文：嵌入失败→跳过L3，只使用L1+L2。
                }
            }

            return RankByRrf(l1l2Scores, l3Scores, triggerHits);
        }

        private (Dictionary<string, double> scores, HashSet<string> triggerHits)
            ComputeL1L2Scores(string userInput)
        {
            string lower = userInput.ToLowerInvariant();
            var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var triggerHits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var skill in _skills.Where(s => s.Enabled))
            {
                double score = 0;

                // L1 — exact trigger word match.
                // 中文：L1 — 完全匹配的触发词。
                foreach (var trigger in skill.Triggers)
                {
                    if (IsStrongTrigger(lower, trigger))
                    {
                        score += 10;
                        triggerHits.Add(skill.Name);
                    }
                }

                // L2 — name and content keyword overlap.
                // 中文：L2 — 名称和内容关键词重叠。
                if (lower.Contains(skill.Name.ToLowerInvariant()))
                    score += 5;

                var words = lower.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Length > 0)
                {
                    var contentLower = skill.Content.ToLowerInvariant();
                    foreach (var word in words)
                    {
                        if (!string.IsNullOrWhiteSpace(word) && contentLower.Contains(word))
                            score += 1;
                    }
                }

                if (score > 0)
                {
                    scores[skill.Name] = score;
                }
            }

            return (scores, triggerHits);
        }

        private static bool IsStrongTrigger(
            string normalizedInput,
            string trigger)
        {
            string normalizedTrigger = trigger.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(normalizedTrigger))
                return false;
            if (normalizedInput.Trim().Equals(
                    normalizedTrigger,
                    StringComparison.OrdinalIgnoreCase))
                return true;

            int cjkCount = normalizedTrigger.Count(ch =>
                ch >= '\u3400' && ch <= '\u9fff');
            // Short ontology nouns such as "村庄" describe resources, not
            // workflow intent. They may be exact commands but cannot activate
            // a procedural skill merely by appearing inside a larger request.
            // 中文：短本体名词如“村庄”描述资源，而非工作流意图。它们可能是精确命令，但不能仅凭出现在较大请求中激活程序技能。
            if (cjkCount > 0 && cjkCount < 4)
                return false;
            if (cjkCount == 0 && normalizedTrigger.Length < 4)
                return Regex.IsMatch(
                    normalizedInput,
                    $@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(normalizedTrigger)}(?![\p{{L}}\p{{N}}_])",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return normalizedInput.Contains(
                normalizedTrigger,
                StringComparison.OrdinalIgnoreCase);
        }

        private List<Skill> RankByRrf(
            Dictionary<string, double> l1l2Scores,
            Dictionary<string, double> l3Scores,
            HashSet<string> triggerHits)
        {
            const double rrfK = 60;

            var l1l2Ranked = l1l2Scores
                .OrderByDescending(kv => kv.Value)
                .Select((kv, i) => (kv.Key, kv.Value, Rank: i + 1))
                .ToList();

            var l3Ranked = l3Scores
                .OrderByDescending(kv => kv.Value)
                .Select((kv, i) => (kv.Key, kv.Value, Rank: i + 1))
                .ToList();

            var rrfScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            foreach (var (name, _, rank) in l1l2Ranked)
                rrfScores[name] = 1.0 / (rrfK + rank);

            double l3Weight = triggerHits.Count > 0 ? 0.6 : 1.2;
            foreach (var (name, _, rank) in l3Ranked)
            {
                double contribution = l3Weight / (rrfK + rank);
                rrfScores[name] = rrfScores.GetValueOrDefault(name, 0) + contribution;
            }

            foreach (var name in triggerHits)
            {
                if (rrfScores.TryGetValue(name, out double val))
                    rrfScores[name] = val + 0.5;
            }

            return rrfScores
                .OrderByDescending(kv => kv.Value)
                .Take(3)
                .Select(kv => _skills.FirstOrDefault(s => s.Name.Equals(kv.Key, StringComparison.OrdinalIgnoreCase)))
                .Where(s => s != null)
                .Cast<Skill>()
                .ToList();
        }

        public string GetSkillsSummary()
        {
            var enabled = _skills.Where(s => s.Enabled).ToList();
            if (enabled.Count == 0) return "";

            var lines = new List<string> { "可用技能模块\n" };
            foreach (var skill in enabled)
            {
                lines.Add($"- {skill.Name}：{skill.Summary}");
            }
            return string.Join("\n", lines);
        }

        public string GetSkillContent(string skillName)
        {
            var skill = _skills.FirstOrDefault(s =>
                s.Name.Equals(skillName, StringComparison.OrdinalIgnoreCase));
            return skill?.Content ?? "";
        }

        public void AddSkill(string name, string content, string category = "general")
        {
            string dir = Path.Combine(_skillsDir, category);
            Directory.CreateDirectory(dir);

            string filePath = Path.Combine(dir, $"{name}.md");
            File.WriteAllText(filePath, content);

            var skill = ParseSkill(filePath, content);
            _skills.RemoveAll(s => s.Name == skill.Name);
            _skills.Add(skill);
        }

        private static Skill ParseSkill(string filePath, string content)
        {
            var skill = new Skill
            {
                FilePath = filePath,
                Content = content,
                Category = Path.GetFileName(Path.GetDirectoryName(filePath)!) ?? "general"
            };

            var titleMatch = Regex.Match(content, @"^#\s+(.+)", RegexOptions.Multiline);
            if (titleMatch.Success)
                skill.Name = titleMatch.Groups[1].Value.Trim();
            else
                skill.Name = Path.GetFileNameWithoutExtension(filePath);

            var triggerMatch = Regex.Match(content,
                @"##\s*触发条件\s*\n([\s\S]*?)(?=\n##\s|\Z)", RegexOptions.IgnoreCase);
            if (triggerMatch.Success)
            {
                skill.Triggers = triggerMatch.Groups[1].Value
                    .Split(new[] { '\n', ',', '、', '|' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(t => t.Trim().TrimStart('-', '*', ' ').Trim().Trim('"', '「', '」'))
                    .Where(t => !string.IsNullOrEmpty(t))
                    .ToList();
            }

            var paraMatch = Regex.Match(content,
                @"^##.*?\n+(.+?)(?=\n##|\n#|\Z)", RegexOptions.Singleline);
            if (paraMatch.Success)
            {
                skill.Summary = paraMatch.Groups[1].Value.Trim();
                if (skill.Summary.Length > 200)
                    skill.Summary = skill.Summary[..200] + "...";
            }
            else
            {
                skill.Summary = skill.Name;
            }

            // "## 所需工具" — schema tool names this skill's workflow depends on.
            // Merged into the exposed-tool set on match (ContextManager), so the
            // model can actually CALL what the skill teaches.
            // 中文：“## 所需工具”——该技能工作流程依赖的结构工具名称。在匹配时合并到暴露工具集（ContextManager），这样模型才能调用技能所教的内容。
            var toolsMatch = Regex.Match(content,
                @"##\s*所需工具\s*\n([\s\S]*?)(?=\n##\s|\Z)", RegexOptions.IgnoreCase);
            if (toolsMatch.Success)
            {
                const int MaxRequiredToolsPerSkill = 15;

                var rawTools = toolsMatch.Groups[1].Value
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(t => t.Trim().TrimStart('-', '*', ' ').Trim().Trim('`'))
                    .Where(t => Regex.IsMatch(t, @"^[a-zA-Z0-9_\-]+$"))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // ★ P1-D（2026-07-20）：上限硬性约束——防止技能声明过多工具导致 prompt 膨胀。
                // 每个 Tier1 工具约 1.8K token。3 个匹配技能 × 15 上限 = 45 个工具 = ~81K token。
                // 当前最大技能（拓扑检查）声明 10 个——15 是合理上限。
                // English：P1-D (2026-07-20): Upper limit rigid constraint—prevents excessive skill statements from causing prompt bloat. Each Tier 1 tool costs about 1.8K tokens. 3 matching skills × 15 cap = 45 tools = ~81K tokens. The current maximum skill (topology check) declaration is 10—15 is a reasonable upper limit.
                if (rawTools.Count > MaxRequiredToolsPerSkill)
                {
                    var removed = rawTools.Skip(MaxRequiredToolsPerSkill).ToList();
                    rawTools = rawTools.Take(MaxRequiredToolsPerSkill).ToList();
                    AgentDiagnostics.Warn("SkillManager.ParseSkill",
                        $"技能「{skill.Name}」声明了 {rawTools.Count + removed.Count} 个 RequiredTools，" +
                        $"超过上限 {MaxRequiredToolsPerSkill}。以下工具被移除（不会暴露给 LLM）：" +
                        $"{string.Join("、", removed)}。" +
                        "如果这些工具确实必需，请分拆为多个技能文件。");
                }

                skill.RequiredTools = rawTools;
            }

            return skill;
        }

        private static double CosineSimilarity(float[] left, float[] right)
        {
            int len = Math.Min(left.Length, right.Length);
            if (len == 0) return 0;

            double dot = 0, leftNorm = 0, rightNorm = 0;
            for (int i = 0; i < len; i++)
            {
                double l = left[i], r = right[i];
                dot += l * r;
                leftNorm += l * l;
                rightNorm += r * r;
            }
            if (leftNorm <= 0 || rightNorm <= 0) return 0;
            return dot / (Math.Sqrt(leftNorm) * Math.Sqrt(rightNorm));
        }
    }
}
