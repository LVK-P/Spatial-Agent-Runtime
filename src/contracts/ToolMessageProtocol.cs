namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Provider-neutral validation for assistant tool-call batches. A batch is
    /// closed only after every opaque call id has exactly one tool response and
    /// before any user/assistant message starts the next conversational phase.
    /// </summary>
    /// <remarks>中文：对助手工具调用批次进行提供程序中立的验证。只有每个不透明调用 ID 恰好对应一个工具响应，且尚未由用户/助手消息开始下一对话阶段时，批次才算闭合。</remarks>
    public static class ToolMessageProtocol
    {
        public static ToolMessageProtocolResult Validate(
            IReadOnlyList<Message> messages)
        {
            var pending = new HashSet<string>(StringComparer.Ordinal);
            var answered = new HashSet<string>(StringComparer.Ordinal);

            for (int index = 0; index < messages.Count; index++)
            {
                Message message = messages[index];
                bool isTool = message.Role.Equals(
                    "tool",
                    StringComparison.OrdinalIgnoreCase);

                if (pending.Count > 0 && !isTool)
                {
                    return ToolMessageProtocolResult.Invalid(
                        index,
                        "A message was appended before all assistant tool calls " +
                        $"were answered. Missing: {string.Join(", ", pending)}.");
                }

                if (isTool)
                {
                    if (string.IsNullOrWhiteSpace(message.ToolCallId))
                    {
                        return ToolMessageProtocolResult.Invalid(
                            index,
                            "A tool message is missing tool_call_id.");
                    }
                    if (!pending.Remove(message.ToolCallId))
                    {
                        string detail = answered.Contains(message.ToolCallId)
                            ? "duplicate tool response"
                            : "unknown tool_call_id";
                        return ToolMessageProtocolResult.Invalid(
                            index,
                            $"{detail}: {message.ToolCallId}.");
                    }
                    answered.Add(message.ToolCallId);
                    continue;
                }

                if (!message.Role.Equals(
                        "assistant",
                        StringComparison.OrdinalIgnoreCase)
                    || message.ToolCalls == null
                    || message.ToolCalls.Count == 0)
                {
                    continue;
                }

                answered.Clear();
                foreach (ToolCall call in message.ToolCalls)
                {
                    if (string.IsNullOrWhiteSpace(call.Id))
                    {
                        return ToolMessageProtocolResult.Invalid(
                            index,
                            "An assistant tool call is missing its id.");
                    }
                    if (!pending.Add(call.Id))
                    {
                        return ToolMessageProtocolResult.Invalid(
                            index,
                            $"Duplicate assistant tool_call_id: {call.Id}.");
                    }
                }
            }

            return pending.Count == 0
                ? ToolMessageProtocolResult.Valid()
                : ToolMessageProtocolResult.Invalid(
                    messages.Count,
                    "The message list ended before all assistant tool calls " +
                    $"were answered. Missing: {string.Join(", ", pending)}.");
        }

        public static ToolMessageProtocolResult ValidateToolCallBatch(
            IReadOnlyList<ToolCall> calls)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < calls.Count; index++)
            {
                if (string.IsNullOrWhiteSpace(calls[index].Id))
                {
                    return ToolMessageProtocolResult.Invalid(
                        index,
                        "An assistant tool call is missing its id.");
                }
                if (!ids.Add(calls[index].Id))
                {
                    return ToolMessageProtocolResult.Invalid(
                        index,
                        $"Duplicate assistant tool_call_id: {calls[index].Id}.");
                }
            }
            return ToolMessageProtocolResult.Valid();
        }
    }

    public sealed class ToolMessageProtocolResult
    {
        public bool IsValid { get; private init; }
        public int MessageIndex { get; private init; } = -1;
        public string Error { get; private init; } = "";

        public static ToolMessageProtocolResult Valid() => new()
        {
            IsValid = true
        };

        public static ToolMessageProtocolResult Invalid(
            int messageIndex,
            string error) => new()
        {
            IsValid = false,
            MessageIndex = messageIndex,
            Error = error
        };
    }
}
