namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// LLM provider capability — self-reported by provider at initialization.
    /// </summary>
    /// <remarks>中文：LLM提供者能力——由提供者在初始化时自我报告。</remarks>
    public class LLMCapability
    {
        public bool ToolCalling { get; set; }
        public bool StructuredOutput { get; set; }
        public bool Vision { get; set; }
        public bool Streaming { get; set; }
        public int MaxContextTokens { get; set; }
    }

    /// <summary>
    /// LLM response from a chat completion.
    /// </summary>
    /// <remarks>中文：聊天完成后的LLM回复。</remarks>
    public class LLMResponse
    {
        public bool Success { get; set; } = true;
        public LLMErrorKind ErrorKind { get; set; } = LLMErrorKind.None;
        public string ErrorMessage { get; set; } = "";
        public bool Retryable { get; set; }
        public int Attempts { get; set; } = 1;
        public string Content { get; set; } = "";
        /// <summary>
        /// Provider reasoning payload that must be preserved across tool-call
        /// continuations when the upstream protocol requires it.
        /// It is transport state, not user-facing content.
        /// </summary>
        /// <remarks>中文：当上游协议需要时，必须在工具调用延续间保持提供者推理有效载荷。它是传输状态，而不是面向用户的内容。</remarks>
        public string? ReasoningContent { get; set; }
        public List<ToolCall> ToolCalls { get; set; } = new();
        public bool HasToolCalls => ToolCalls.Count > 0;
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
        /// <summary>Provider termination reason, when exposed by the API.</summary>
        /// <remarks>中文：API暴露的提供者终止原因。</remarks>
        public string FinishReason { get; set; } = "";
        /// <summary>
        /// A successful transport with neither user-visible content nor a
        /// valid tool call is not a completed agent turn.
        /// </summary>
        /// <remarks>中文：如果成功传输但既无用户可见内容，也无有效工具调用，则不算是完成的代理回合。</remarks>
        public bool IsSemanticallyEmpty =>
            Success && !HasToolCalls && string.IsNullOrWhiteSpace(Content);

        /// <summary>Session-level accumulated usage (written by AgentRuntime after each call).</summary>
        /// <remarks>中文：会话级累计使用量（由AgentRuntime在每次调用后编写）。</remarks>
        public TokenUsage? SessionUsage { get; set; }
    }

    /// <summary>
    /// Session-level accumulated token usage — updated by AgentRuntime after each
    /// LLM call and exposed to the UI through the session contract. Pure data, no logic.
    /// </summary>
    /// <remarks>中文：会话层级累计令牌使用量——由AgentRuntime在每次LLM调用后更新，并通过会话合同向界面展示。纯数据，没有逻辑。</remarks>
    public class TokenUsage
    {
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
        public int TotalTokens => PromptTokens + CompletionTokens;
    }

    public enum LLMErrorKind
    {
        None,
        NotConfigured,
        Authentication,
        RateLimit,
        Timeout,
        Network,
        Server,
        Parse,
        InvalidRequest,
        EmptyResponse,
        Unknown
    }

    /// <summary>
    /// LLM provider abstraction. Planner and Scheduler depend on this, not on concrete providers.
    /// </summary>
    /// <remarks>中文：LLM 提供者抽象。规划师和调度员依赖于这一点，而不是具体的服务提供者。</remarks>
    public interface ILLMProvider
    {
        string ProviderName { get; }
        string ModelName { get; }

        /// <summary>
        /// Current reasoning-effort level actually sent to the API (empty when
        /// the provider does not support it). Exposed so the transcript can
        /// record link-level evidence per llm_request, not just UI state.
        /// </summary>
        /// <remarks>中文：当前推理工作量水平实际发送给 API（当提供者不支持时为空）。公开是为了让转录记录每个llm_request记录链接层面的证据，而不仅仅是界面状态。</remarks>
        string ThinkingEffort { get; }

        LLMCapability Capability { get; }
        bool IsConfigured { get; }
        void SetApiKey(string key);

        Task<LLMResponse> ChatAsync(
            IReadOnlyList<Message> messages,
            string systemPrompt,
            IReadOnlyList<IToolDescriptor>? tools = null,
            double temperature = 0.7,
            int maxTokens = 4096,
            CancellationToken cancellation = default);
    }
}
