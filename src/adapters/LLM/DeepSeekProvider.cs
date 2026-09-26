using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SpatialAgent.Runtime.Contracts;
using SpatialAgent.Core;

namespace SpatialAgent.Adapters.LLM
{
    /// <summary>
    /// DeepSeek API provider. Implements ILLMProvider for the Runtime.
    /// Supports Function Calling and Thinking/Reasoning. Retrieval uses a
    /// separately configured embedding provider or the local BM25 fallback.
    /// </summary>
    public class DeepSeekProvider : ILLMProvider, IDisposable
    {
        private const string CHAT_URL = "https://api.deepseek.com/chat/completions";
        private const int MaxChatAttempts = 3;
        private readonly HttpClient _http;
        private string _apiKey = "";
        private string _model;
        private string _thinkingEffort = "high";

        public string ProviderName => "deepseek";
        public string ModelName => _model;
        public string ThinkingEffort => _thinkingEffort;

        public LLMCapability Capability => new()
        {
            ToolCalling = true,
            StructuredOutput = true,
            Vision = false,
            Streaming = true,
            MaxContextTokens = 128000
        };

        public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);
        public bool SupportsEmbeddings => false;

        public DeepSeekProvider(string model = "deepseek-v4-pro", HttpMessageHandler? handler = null)
        {
            _model = model;
            _http = handler == null
                ? new HttpClient()
                : new HttpClient(handler, disposeHandler: true);
            _http.Timeout = TimeSpan.FromSeconds(120);
            _http.DefaultRequestHeaders.Add("Accept", "application/json");
        }

        public void SetApiKey(string key) => _apiKey = key?.Trim() ?? "";
        public void SetModel(string model) => _model = model;
        public void SetThinkingEffort(string level) => _thinkingEffort = level;

        public async Task<LLMResponse> ChatAsync(
            IReadOnlyList<Message> messages,
            string systemPrompt,
            IReadOnlyList<IToolDescriptor>? tools = null,
            double temperature = 0.7,
            int maxTokens = 4096,
            CancellationToken cancellation = default)
        {
            if (!IsConfigured)
                return BuildError("未配置 API Key。", LLMErrorKind.NotConfigured, retryable: false);

            ToolMessageProtocolResult protocol =
                ToolMessageProtocol.Validate(messages);
            if (!protocol.IsValid)
            {
                return BuildError(
                    "Invalid tool-message protocol at message " +
                    $"{protocol.MessageIndex}: {protocol.Error}",
                    LLMErrorKind.InvalidRequest,
                    retryable: false);
            }

            var msgList = new List<object> { new { role = "system", content = systemPrompt } };
            foreach (var m in messages)
            {
                if (m.Role == "tool" && !string.IsNullOrEmpty(m.ToolCallId))
                {
                    msgList.Add(new { role = "tool", tool_call_id = m.ToolCallId, content = m.Content });
                }
                else if (m.Role == "assistant" && m.ToolCalls != null && m.ToolCalls.Count > 0)
                {
                    var assistant = new Dictionary<string, object?>
                    {
                        ["role"] = "assistant",
                        ["content"] = string.IsNullOrEmpty(m.Content) ? null : m.Content,
                        ["tool_calls"] = m.ToolCalls.Select(tc => new
                        {
                            id = tc.Id,
                            type = "function",
                            function = new { name = tc.Name, arguments = JsonConvert.SerializeObject(tc.Arguments) }
                        }).ToArray()
                    };
                    if (!string.IsNullOrWhiteSpace(m.ReasoningContent))
                        assistant["reasoning_content"] = m.ReasoningContent;
                    msgList.Add(assistant);
                }
                else
                {
                    msgList.Add(new { role = m.Role, content = m.Content });
                }
            }

            var body = new Dictionary<string, object>
            {
                ["model"] = _model,
                ["messages"] = msgList,
                ["max_tokens"] = maxTokens,
                ["temperature"] = temperature,
                ["stream"] = false
            };

            if (tools != null && tools.Count > 0)
            {
                var toolDefs = tools.Select(t =>
                {
                    var props = new Dictionary<string, object>();
                    foreach (var kv in t.Parameters)
                    {
                        var prop = new Dictionary<string, object>
                        {
                            ["type"] = kv.Value.Type,
                            ["description"] = kv.Value.Description
                        };
                        if (kv.Value.Enum != null && kv.Value.Enum.Count > 0)
                            prop["enum"] = kv.Value.Enum;
                        props[kv.Key] = prop;
                    }

                    return (object)new
                    {
                        type = "function",
                        function = new
                        {
                            name = t.Name,
                            description = t.Description,
                            parameters = new
                            {
                                type = "object",
                                properties = props,
                                required = (object?)t.RequiredParameters ?? Array.Empty<string>()
                            }
                        }
                    };
                }).ToList<object>();

                body["tools"] = toolDefs;
                body["tool_choice"] = "auto";
            }

            if (!string.IsNullOrEmpty(_thinkingEffort))
            {
                body["thinking"] = new { type = "enabled" };
                body["reasoning_effort"] = NormalizeThinkingEffort(_thinkingEffort);
                body.Remove("temperature");
            }

            string requestJson = JsonConvert.SerializeObject(body);
            for (int attempt = 1; attempt <= MaxChatAttempts; attempt++)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, CHAT_URL);
                    request.Headers.Add("Authorization", $"Bearer {_apiKey}");
                    request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

                    var response = await _http.SendAsync(request, cancellation);
                    string respBody = await response.Content.ReadAsStringAsync(cancellation);

                    if (!response.IsSuccessStatusCode)
                    {
                        int statusCode = (int)response.StatusCode;
                        var kind = ClassifyStatus(statusCode);
                        bool retryable = IsRetryableStatus(statusCode);
                        string err = TryExtractError(respBody);
                        AgentDiagnostics.Warn("LLM.DeepSeek", $"HTTP {statusCode}: {err}", eventBus: null);

                        if (retryable && attempt < MaxChatAttempts)
                        {
                            AgentDiagnostics.Warn("LLM.DeepSeek", $"Retryable HTTP {statusCode}, attempt {attempt}", eventBus: null);
                            await Task.Delay(BackoffDelay(attempt), cancellation);
                            continue;
                        }

                        return BuildError($"API 错误 ({statusCode})：{err}", kind, retryable, attempt);
                    }

                    var parsed = ParseResponse(respBody);
                    parsed.Attempts = attempt;
                    return parsed;
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    throw;
                }
                catch (TaskCanceledException)
                {
                    if (attempt < MaxChatAttempts)
                    {
                        AgentDiagnostics.Warn("LLM.DeepSeek", $"Timeout on attempt {attempt}, retrying", eventBus: null);
                        await Task.Delay(BackoffDelay(attempt), cancellation);
                        continue;
                    }

                    AgentDiagnostics.Warn("LLM.DeepSeek", "Request timed out", eventBus: null);
                    return BuildError("请求超时。", LLMErrorKind.Timeout, retryable: true, attempt);
                }
                catch (HttpRequestException ex)
                {
                    if (attempt < MaxChatAttempts)
                    {
                        AgentDiagnostics.Warn("LLM.DeepSeek", $"Network error on attempt {attempt}, retrying", ex);
                        await Task.Delay(BackoffDelay(attempt), cancellation);
                        continue;
                    }

                    AgentDiagnostics.Warn("LLM.DeepSeek", "Network error", ex);
                    return BuildError($"网络错误：{ex.Message}", LLMErrorKind.Network, retryable: true, attempt);
                }
            }

            return BuildError("LLM 请求失败。", LLMErrorKind.Unknown, retryable: true, MaxChatAttempts);
        }

        public Task<float[]> GetEmbeddingAsync(string text) =>
            Task.FromResult(Array.Empty<float>());

        private static LLMResponse ParseResponse(string body)
        {
            dynamic? parsed = JsonConvert.DeserializeObject<dynamic>(body);
            if (parsed == null) return BuildError("无法解析模型响应。", LLMErrorKind.Parse, retryable: false);

            var resp = new LLMResponse
            {
                PromptTokens = (int?)parsed.usage?.prompt_tokens ?? 0,
                CompletionTokens = (int?)parsed.usage?.completion_tokens ?? 0
            };

            var choice = parsed.choices?[0];
            if (choice == null) return BuildError("响应中没有 choices。", LLMErrorKind.Parse, retryable: false);

            var msg = choice.message;
            resp.FinishReason = choice.finish_reason?.ToString() ?? "";
            if (msg?.tool_calls != null)
            {
                foreach (var tc in msg.tool_calls)
                {
                    string funcName = tc.function?.name?.ToString() ?? "";
                    string funcArgs = tc.function?.arguments?.ToString() ?? "{}";
                    string tcId = tc.id?.ToString() ?? Guid.NewGuid().ToString("N")[..8];

                    Dictionary<string, object?> args;
                    try { args = JsonConvert.DeserializeObject<Dictionary<string, object?>>(funcArgs) ?? new(); }
                    catch (JsonException ex)
                    {
                        AgentDiagnostics.Warn("DeepSeekProvider.ParseResponse",
                            $"LLM returned malformed tool-call arguments for '{funcName}' — " +
                            "discarding the invalid call; no default-valued execution will occur.", ex);
                        continue;
                    }

                    resp.ToolCalls.Add(new ToolCall { Id = tcId, Name = funcName, Arguments = args });
                }
            }

            resp.Content = msg?.content?.ToString() ?? "";
            resp.ReasoningContent = msg?.reasoning_content?.ToString();

            return resp;
        }

        private static LLMResponse BuildError(string message, LLMErrorKind kind, bool retryable, int attempts = 1) => new()
        {
            Success = false,
            ErrorKind = kind,
            ErrorMessage = message,
            Retryable = retryable,
            Attempts = attempts,
            Content = message
        };

        private static LLMErrorKind ClassifyStatus(int statusCode) => statusCode switch
        {
            401 or 403 => LLMErrorKind.Authentication,
            408 => LLMErrorKind.Timeout,
            429 => LLMErrorKind.RateLimit,
            >= 500 => LLMErrorKind.Server,
            _ => LLMErrorKind.Unknown
        };

        private static bool IsRetryableStatus(int statusCode) => statusCode is 408 or 429 || statusCode >= 500;

        private static TimeSpan BackoffDelay(int attempt) =>
            TimeSpan.FromMilliseconds(600 * Math.Pow(2, attempt - 1));

        private static string NormalizeThinkingEffort(string effort) =>
            effort.Equals("max", StringComparison.OrdinalIgnoreCase) ||
            effort.Equals("xhigh", StringComparison.OrdinalIgnoreCase)
                ? "max"
                : "high";

        private static string TryExtractError(string body)
        {
            try
            {
                dynamic? err = JsonConvert.DeserializeObject<dynamic>(body);
                return err?.error?.message?.ToString() ?? body;
            }
            catch
            {
                return body;
            }
        }

        public void Dispose()
        {
            _http?.Dispose();
        }
    }
}
