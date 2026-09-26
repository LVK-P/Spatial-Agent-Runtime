using System.Net;
using System.Text;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using SpatialAgent.Adapters.LLM;
using SpatialAgent.Runtime.Contracts;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    public sealed class DeepSeekProviderContractTests
    {
        private sealed class CaptureHandler : HttpMessageHandler
        {
            public string RequestBody { get; private set; } = "";

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                const string response = """
                {
                  "choices": [{
                    "finish_reason": "tool_calls",
                    "message": {
                      "content": "",
                      "reasoning_content": "opaque-state",
                      "tool_calls": [{
                        "id": "call-1",
                        "function": { "name": "agent_list_layers", "arguments": "{}" }
                      }]
                    }
                  }],
                  "usage": { "prompt_tokens": 11, "completion_tokens": 7 }
                }
                """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(response, Encoding.UTF8, "application/json")
                };
            }
        }

        private sealed class MalformedArgumentsHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                const string response = """
                {
                  "choices": [{
                    "message": {
                      "content": "",
                      "tool_calls": [{
                        "id": "bad-call",
                        "function": {
                          "name": "management_Delete",
                          "arguments": "{not-json"
                        }
                      }]
                    }
                  }]
                }
                """;
                return Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            response,
                            Encoding.UTF8,
                            "application/json")
                    });
            }
        }

        [Fact]
        public async Task Thinking_request_uses_supported_shape_and_preserves_reasoning_state()
        {
            var handler = new CaptureHandler();
            using var provider = new DeepSeekProvider("deepseek-test", handler);
            provider.SetApiKey("test-key");
            provider.SetThinkingEffort("max");

            var response = await provider.ChatAsync(
                new[]
                {
                    new Message
                    {
                        Role = "assistant",
                        ReasoningContent = "prior-state",
                        ToolCalls = new()
                        {
                            new ToolCall { Id = "prior", Name = "agent_get_map_info" }
                        }
                    },
                    new Message { Role = "tool", ToolCallId = "prior", Content = "{}" }
                },
                "system");

            var body = JObject.Parse(handler.RequestBody);
            body["thinking"]?["type"]?.Value<string>().Should().Be("enabled");
            body["reasoning_effort"]?.Value<string>().Should().Be("max");
            body["temperature"].Should().BeNull();
            body["messages"]![1]!["reasoning_content"]!.Value<string>().Should().Be("prior-state");
            response.ReasoningContent.Should().Be("opaque-state");
            response.FinishReason.Should().Be("tool_calls");
            response.ToolCalls.Should().ContainSingle(call => call.Name == "agent_list_layers");
        }

        [Fact]
        public async Task Caller_cancellation_is_not_converted_to_provider_error()
        {
            var handler = new NeverCompletesHandler();
            using var provider = new DeepSeekProvider("deepseek-test", handler);
            provider.SetApiKey("test-key");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

            Func<Task> action = () => provider.ChatAsync(
                Array.Empty<Message>(),
                "system",
                cancellation: cancellation.Token);

            await action.Should().ThrowAsync<OperationCanceledException>();
        }

        [Fact]
        public async Task Malformed_tool_arguments_cannot_execute_with_defaults()
        {
            using var provider = new DeepSeekProvider(
                "deepseek-test",
                new MalformedArgumentsHandler());
            provider.SetApiKey("test-key");

            LLMResponse response = await provider.ChatAsync(
                Array.Empty<Message>(),
                "system");

            response.Success.Should().BeTrue();
            response.ToolCalls.Should().BeEmpty();
            response.Content.Should().BeEmpty();
            response.IsSemanticallyEmpty.Should().BeTrue(
                "discarding an invalid tool call must not be disguised as a completed text response");
        }

        private sealed class NeverCompletesHandler : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException();
            }
        }
    }
}
