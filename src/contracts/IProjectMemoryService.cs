namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Project-level persistent memory across sessions.
    /// Encapsulates both reading (summary injection) and writing (session recording).
    /// </summary>
    /// <remarks>中文：跨会话的项目级持久记忆，同时封装读取（摘要注入）和写入（会话记录）。</remarks>
    public interface IProjectMemoryService
    {
        /// <summary>Human-readable summary for injection into the system prompt.</summary>
        /// <remarks>中文：用于注入系统提示词的易读摘要。</remarks>
        string GetSummary();

        /// <summary>Record a completed session summary into persistent storage.</summary>
        /// <remarks>中文：将完成的会话摘要记录到持久存储中。</remarks>
        void RecordSessionSummary(SessionSummary summary);

        /// <summary>Whether any previous sessions have been recorded.</summary>
        /// <remarks>中文：是否记录过任何历史会话。</remarks>
        bool HasHistory { get; }

        /// <summary>Update the 5W2H project cognition result.</summary>
        /// <remarks>中文：更新5W2H项目的认知结果。</remarks>
        void SetW5H2Context(string w5h2);

        /// <summary>Get the 5W2H analysis text, or empty if not performed.</summary>
        /// <remarks>中文：获取 5W2H 分析文本；尚未执行分析时返回空字符串。</remarks>
        string GetW5H2Context();
    }
}
