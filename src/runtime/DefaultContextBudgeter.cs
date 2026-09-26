using System.Security.Cryptography;
using System.Text;
using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Runtime
{
    public sealed class DefaultContextBudgeter : IContextBudgeter
    {
        public ContextBundle Build(string userMessage, AgentContext context, IReadOnlyList<Message>? history = null)
        {
            var packets = new List<ContextPacket>();
            var cuts = new Dictionary<string, BudgetCutInfo>();

            AddIfNotEmpty(packets, cuts, ContextPacketSource.Project, ContextPacketPriority.Critical, "项目真实数据", context.ProjectInfo, "project", RuntimePolicy.ContextMaxProjectChars);
            AddIfNotEmpty(packets, cuts, ContextPacketSource.Map, ContextPacketPriority.Critical, "地图与图层", context.MapInfo, "map", RuntimePolicy.ContextMaxMapChars);
            string lineage = BuildLineageContext(context.Layers);
            AddIfNotEmpty(
                packets,
                cuts,
                ContextPacketSource.Map,
                ContextPacketPriority.High,
                "数据谱系",
                lineage,
                "lineage",
                1200);

            if (context.MatchedSkills.Count > 0)
            {
                var skillsRaw = context.MatchedSkills;
                var skills = skillsRaw.Take(RuntimePolicy.ContextMaxMatchedSkills)
                    .Select(s =>
                    {
                        string workflow = string.IsNullOrWhiteSpace(s.Content)
                            ? s.Summary
                            : s.Content;
                        return $"{s.Name}：{TrimTo(workflow, 900)}";
                    })
                    .ToList();
                string skillsBody = string.Join("\n", skills);
                int originalSkillsLen = skillsRaw.Sum(s =>
                    s.Name.Length + (string.IsNullOrWhiteSpace(s.Content)
                        ? s.Summary.Length
                        : s.Content.Length));
                AddIfNotEmpty(packets, cuts, ContextPacketSource.Skill, ContextPacketPriority.Normal, "相关技能", skillsBody, "skills", RuntimePolicy.ContextMaxSkillChars, originalSkillsLen);
            }

            if (context.KnowledgeChunks.Count > 0)
            {
                var chunksRaw = context.KnowledgeChunks;
                var knowledge = chunksRaw.Take(RuntimePolicy.ContextMaxKnowledgeChunks)
                    .Select((chunk, idx) => $"证据{idx + 1}：{TrimTo(chunk, 700)}")
                    .ToList();
                string knowledgeBody = string.Join("\n---\n", knowledge);
                int originalKnowledgeLen = chunksRaw.Sum(c => c.Length);
                AddIfNotEmpty(packets, cuts, ContextPacketSource.Knowledge, ContextPacketPriority.Normal, "知识证据", knowledgeBody, "knowledge", RuntimePolicy.ContextMaxKnowledgeChars, originalKnowledgeLen);
            }

            AddIfNotEmpty(packets, cuts, ContextPacketSource.Memory, ContextPacketPriority.Normal, "项目记忆", context.MemorySummary, "memory", RuntimePolicy.ContextMaxMemoryChars);

            if (history != null && history.Count > 0)
            {
                var tail = history
                    .TakeLast(4)
                    .Select(m => $"{m.Role}: {TrimTo(m.Content, 240)}")
                    .ToList();
                AddIfNotEmpty(packets, cuts, ContextPacketSource.Session, ContextPacketPriority.Low, "最近对话", string.Join("\n", tail), "session", 900);
            }

            var ordered = packets
                .OrderBy(p => p.Priority)
                .ThenBy(p => p.Source)
                .ToList();

            var sb = new StringBuilder();
            foreach (var packet in ordered)
            {
                if (sb.Length > 0)
                    sb.AppendLine();

                sb.Append('[').Append(packet.Title).AppendLine("]");
                sb.AppendLine(packet.Body);
            }

            string userSection = $"[用户消息]{Environment.NewLine}{userMessage}";
            int contextBudget = Math.Max(
                0,
                RuntimePolicy.ContextMaxAugmentedChars - userSection.Length - 2);
            string contextSection = TrimTo(sb.ToString().TrimEnd(), contextBudget);
            string augmented = string.IsNullOrWhiteSpace(contextSection)
                ? userSection
                : contextSection + Environment.NewLine + Environment.NewLine + userSection;
            string contextHash = ComputeHash(string.Join("\n", ordered.Select(p => $"{p.Source}|{p.Title}|{p.Body}|{p.EvidenceId}")) + "\n" + userMessage);
            int estimatedTokens = ordered.Sum(p => p.EstimatedTokens) + EstimateTokens(userMessage);

            return new ContextBundle
            {
                Packets = ordered,
                AugmentedUserMessage = augmented,
                ContextHash = contextHash,
                EstimatedTokens = estimatedTokens,
                BudgetCuts = cuts
            };
        }

        private static string BuildLineageContext(IReadOnlyList<LayerInfo> layers)
        {
            var derived = layers
                .Where(layer => layer.LineageDepth > 0
                    && !string.IsNullOrWhiteSpace(layer.LineageRoot))
                .OrderBy(layer => layer.LineageRoot, StringComparer.OrdinalIgnoreCase)
                .ThenBy(layer => layer.LineageDepth)
                .ThenBy(layer => layer.Name, StringComparer.OrdinalIgnoreCase)
                .Select(layer =>
                    $"来源={layer.LineageRoot}; 派生={layer.Name}; 深度={layer.LineageDepth}")
                .ToList();
            if (derived.Count == 0)
                return "";

            return "以下是运行时记录的同源派生关系。除非用户明确要求同时处理多个版本，"
                + "规划时优先使用同一谱系中深度最大的现存版本；参考来源本身不自动成为写入目标。"
                + Environment.NewLine
                + string.Join(Environment.NewLine, derived);
        }

        // ★ Phase C4：记录裁剪信息的重载
        private static void AddIfNotEmpty(
            List<ContextPacket> packets,
            Dictionary<string, BudgetCutInfo> cuts,
            ContextPacketSource source,
            ContextPacketPriority priority,
            string title,
            string? body,
            string evidenceId,
            int maxChars,
            int? originalLen = null)
        {
            if (string.IsNullOrWhiteSpace(body))
                return;

            int origLen = originalLen ?? body.Trim().Length;
            string text = TrimTo(body.Trim(), maxChars);
            packets.Add(new ContextPacket
            {
                Source = source,
                Priority = priority,
                Title = title,
                Body = text,
                EvidenceId = evidenceId,
                EstimatedTokens = EstimateTokens(text),
                IsCompressed = text.Length < body.Trim().Length
            });

            // ★ Phase C4：记录裁剪信息
            cuts[evidenceId] = new BudgetCutInfo
            {
                OriginalLen = origLen,
                Budget = maxChars,
                CutLen = Math.Max(0, origLen - maxChars)
            };
        }

        private static void AddIfNotEmpty(
            List<ContextPacket> packets,
            ContextPacketSource source,
            ContextPacketPriority priority,
            string title,
            string? body,
            string evidenceId,
            int maxChars)
        {
            if (string.IsNullOrWhiteSpace(body))
                return;

            string text = TrimTo(body.Trim(), maxChars);
            packets.Add(new ContextPacket
            {
                Source = source,
                Priority = priority,
                Title = title,
                Body = text,
                EvidenceId = evidenceId,
                EstimatedTokens = EstimateTokens(text),
                IsCompressed = text.Length < body.Trim().Length
            });
        }

        private static string TrimTo(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value))
                return value;
            if (maxChars <= 0)
                return "";
            if (value.Length <= maxChars)
                return value;

            if (maxChars == 1)
                return value[..1];

            return value[..(maxChars - 1)] + "…";
        }

        private static int EstimateTokens(string value) =>
            Math.Max(1, (int)Math.Ceiling(value.Length / 4.0));

        private static string ComputeHash(string text)
        {
            byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
            return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
        }
    }
}
