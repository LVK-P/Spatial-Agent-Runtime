namespace SpatialAgent.Runtime.Contracts
{
    public enum ContextPacketSource
    {
        Project,
        Map,
        Knowledge,
        Skill,
        Memory,
        FileChange,
        Session,
        Tool
    }

    public enum ContextPacketPriority
    {
        Critical = 0,
        High = 1,
        Normal = 2,
        Low = 3
    }

    public class ContextPacket
    {
        public ContextPacketSource Source { get; set; }
        public ContextPacketPriority Priority { get; set; } = ContextPacketPriority.Normal;
        public string Title { get; set; } = "";
        public string Body { get; set; } = "";
        public string? EvidenceId { get; set; }
        public int EstimatedTokens { get; set; }
        public bool IsCompressed { get; set; }
    }

    public class ContextBundle
    {
        public List<ContextPacket> Packets { get; set; } = new();
        public string AugmentedUserMessage { get; set; } = "";
        public string ContextHash { get; set; } = "";
        public int EstimatedTokens { get; set; }

        /// <summary>
        /// ★ Phase C4（2026-07-20）：ContextBudgeter 裁剪明细。
        /// Key = 上下文源名称（project/map/skills/knowledge/memory/session），
        /// Value = { original_len, budget, cut_len }。
        /// </summary>
        /// <remarks>中文：C4阶段（2026-07-20）：ContextBudgeter 裁剪明细。Key = 上下文源名称（project/map/skills/knowledge/memory/session），Value = { original_len， budget， cut_len }。</remarks>
        public Dictionary<string, BudgetCutInfo> BudgetCuts { get; set; } = new();
    }

    /// <summary>
    /// ★ Phase C4：单个上下文源的裁剪信息。
    /// </summary>
    /// <remarks>English：Phase C4: Clipping information from a single context source.</remarks>
    public class BudgetCutInfo
    {
        public int OriginalLen { get; set; }
        public int Budget { get; set; }
        public int CutLen { get; set; }
        public bool WasTruncated => OriginalLen > Budget;
    }

    public interface IContextBudgeter
    {
        ContextBundle Build(string userMessage, AgentContext context, IReadOnlyList<Message>? history = null);
    }
}
