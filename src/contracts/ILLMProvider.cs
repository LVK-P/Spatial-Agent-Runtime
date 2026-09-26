namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// LLM provider capability — self-reported by provider at initialization.
    /// </summary>
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
        public string? ReasoningContent { get; set; }
        public List<ToolCall> ToolCalls { get; set; } = new();
        public bool HasToolCalls => ToolCalls.Count > 0;
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
        /// <summary>Provider termination reason, when exposed by the API.</summary>
        public string FinishReason { get; set; } = "";
        /// <summary>
        /// A successful transport with neither user-visible content nor a
        /// valid tool call is not a completed agent turn.
        /// </summary>
        public bool IsSemanticallyEmpty =>
            Success && !HasToolCalls && string.IsNullOrWhiteSpace(Content);

        /// <summary>Session-level accumulated usage (written by AgentRuntime after each call).</summary>
        public TokenUsage? SessionUsage { get; set; }
    }

    /// <summary>
    /// Session-level accumulated token usage — updated by AgentRuntime after each
    /// LLM call and exposed to the UI through the session contract. Pure data, no logic.
    /// </summary>
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
    public interface ILLMProvider
    {
        string ProviderName { get; }
        string ModelName { get; }

        /// <summary>
        /// Current reasoning-effort level actually sent to the API (empty when
        /// the provider does not support it). Exposed so the transcript can
        /// record link-level evidence per llm_request, not just UI state.
        /// </summary>
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
