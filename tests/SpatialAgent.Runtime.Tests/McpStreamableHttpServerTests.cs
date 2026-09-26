using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using SpatialAgent.Adapters.Mcp;
using SpatialAgent.Runtime;
using SpatialAgent.Runtime.Contracts;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    [CollectionDefinition("MCP HTTP", DisableParallelization = true)]
    public sealed class McpHttpCollection
    {
    }

    [Collection("MCP HTTP")]
    public sealed class McpStreamableHttpServerTests
    {
        private sealed class Descriptor : IToolDescriptor
        {
            public string Name { get; init; } = "";
            public string Description { get; init; } = "";
            public string Category { get; init; } = "test";
            public ToolAnnotation Annotation { get; init; }
            public IReadOnlyDictionary<string, ParameterProperty> Parameters { get; init; }
                = new Dictionary<string, ParameterProperty>();
            public IReadOnlyList<string> RequiredParameters { get; init; }
                = Array.Empty<string>();
            public VerificationRule? Verification => null;
            public IReadOnlyDictionary<string, string>? DefaultParameters => null;
        }

        private sealed class Executor : IToolExecutor
        {
            public int Calls { get; private set; }

            public Task<ToolResult> ExecuteAsync(
                ToolCall call,
                CancellationToken ct = default)
            {
                Calls++;
                return Task.FromResult(new ToolResult
                {
                    ToolName = call.Name,
                    Success = true,
                    Output = "ok"
                });
            }
        }

        private sealed class RunningServer : IAsyncDisposable
        {
            public RunningServer()
            {
                int port = ReservePort();
                Registry = new ToolRegistry();
                ReadExecutor = new Executor();
                WriteExecutor = new Executor();
                Registry.Register(
                    new Descriptor
                    {
                        Name = "agent_read",
                        Description = "read",
                        Annotation = ToolAnnotation.ReadOnly,
                        Parameters = new Dictionary<string, ParameterProperty>
                        {
                            ["layer"] = new()
                            {
                                Type = "string",
                                Description = "layer"
                            }
                        },
                        RequiredParameters = new[] { "layer" }
                    },
                    ReadExecutor);
                Registry.Register(
                    new Descriptor
                    {
                        Name = "agent_write",
                        Description = "write",
                        Annotation = ToolAnnotation.Modify
                    },
                    WriteExecutor);
                Server = new McpStreamableHttpServer(Registry, port: port);
                Server.Start();
                Client = new HttpClient
                {
                    BaseAddress = new Uri($"http://127.0.0.1:{port}/")
                };
            }

            public ToolRegistry Registry { get; }
            public Executor ReadExecutor { get; }
            public Executor WriteExecutor { get; }
            public McpStreamableHttpServer Server { get; }
            public HttpClient Client { get; }

            public HttpRequestMessage Request(
                HttpMethod method,
                string? json = null,
                bool authorize = true,
                string? origin = null)
            {
                var request = new HttpRequestMessage(method, "mcp");
                if (json != null)
                    request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                if (authorize)
                {
                    request.Headers.Authorization =
                        new AuthenticationHeaderValue("Bearer", Server.AuthToken);
                }
                if (origin != null)
                    request.Headers.TryAddWithoutValidation("Origin", origin);
                return request;
            }

            public async ValueTask DisposeAsync()
            {
                Client.Dispose();
                await Server.StopAsync();
            }

            private static int ReservePort()
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
                return port;
            }
        }

        [Fact]
        public async Task Authentication_origin_and_method_policy_are_enforced()
        {
            await using var host = new RunningServer();
            using var unauthorized = host.Request(
                HttpMethod.Post,
                """{"jsonrpc":"2.0","method":"initialize","id":1}""",
                authorize: false);
            (await host.Client.SendAsync(unauthorized)).StatusCode
                .Should().Be(HttpStatusCode.Unauthorized);

            await using var secondHost = new RunningServer();
            using var untrusted = secondHost.Request(
                HttpMethod.Post,
                """{"jsonrpc":"2.0","method":"initialize","id":1}""",
                origin: "https://evil.example");
            (await secondHost.Client.SendAsync(untrusted)).StatusCode
                .Should().Be(HttpStatusCode.Forbidden);

            using var get = secondHost.Request(HttpMethod.Get);
            var getResponse = await secondHost.Client.SendAsync(get);
            getResponse.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
            bool hasAllow = getResponse.Headers.TryGetValues("Allow", out var allowed)
                || getResponse.Content.Headers.TryGetValues("Allow", out allowed);
            hasAllow.Should().BeTrue();
            allowed.Should().Contain(value => value.Contains("POST"));
        }

        [Fact]
        public async Task Notification_id_fidelity_and_read_only_boundary_are_preserved()
        {
            await using var host = new RunningServer();

            using var notification = host.Request(
                HttpMethod.Post,
                """{"jsonrpc":"2.0","method":"initialize"}""");
            var notificationResponse = await host.Client.SendAsync(notification);
            notificationResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
            (await notificationResponse.Content.ReadAsStringAsync()).Should().BeEmpty();

            using var list = host.Request(
                HttpMethod.Post,
                """{"jsonrpc":"2.0","method":"tools/list","id":"list-7"}""");
            var listJson = JObject.Parse(
                await (await host.Client.SendAsync(list)).Content.ReadAsStringAsync());
            listJson["id"]!.Value<string>().Should().Be("list-7");
            listJson["result"]!["tools"]!.Select(tool => tool!["name"]!.ToString())
                .Should().Equal("agent_read");

            using var read = host.Request(
                HttpMethod.Post,
                """
                {"jsonrpc":"2.0","method":"tools/call","params":{"name":"agent_read","arguments":{"layer":"Parcels"}},"id":42}
                """);
            var readJson = JObject.Parse(
                await (await host.Client.SendAsync(read)).Content.ReadAsStringAsync());
            readJson["id"]!.Value<int>().Should().Be(42);
            readJson["result"].Should().NotBeNull();
            host.ReadExecutor.Calls.Should().Be(1);

            using var write = host.Request(
                HttpMethod.Post,
                """
                {"jsonrpc":"2.0","method":"tools/call","params":{"name":"agent_write","arguments":{}},"id":"write"}
                """);
            var writeJson = JObject.Parse(
                await (await host.Client.SendAsync(write)).Content.ReadAsStringAsync());
            writeJson["error"]!["code"]!.Value<int>().Should().Be(-32604);
            host.WriteExecutor.Calls.Should().Be(0);
        }

        [Fact]
        public async Task Oversized_and_concurrent_requests_are_bounded()
        {
            await using var oversizedHost = new RunningServer();
            string oversizedPayload =
                "{\"jsonrpc\":\"2.0\",\"method\":\"initialize\",\"id\":1,\"padding\":\""
                + new string('x', 1024 * 1024)
                + "\"}";
            using var oversized = oversizedHost.Request(
                HttpMethod.Post,
                oversizedPayload);
            (await oversizedHost.Client.SendAsync(oversized)).StatusCode
                .Should().Be(HttpStatusCode.RequestEntityTooLarge);

            await using var concurrentHost = new RunningServer();
            var calls = Enumerable.Range(0, 40)
                .Select(async id =>
                {
                    using var request = concurrentHost.Request(
                        HttpMethod.Post,
                        $$"""{"jsonrpc":"2.0","method":"initialize","id":{{id}}}""");
                    return await concurrentHost.Client.SendAsync(request);
                });
            var responses = await Task.WhenAll(calls);

            responses.Should().OnlyContain(response =>
                response.StatusCode == HttpStatusCode.OK
                || response.StatusCode == HttpStatusCode.TooManyRequests);
            responses.Count(response =>
                response.StatusCode == HttpStatusCode.TooManyRequests)
                .Should().BeGreaterThan(0);
            foreach (var response in responses)
                response.Dispose();
        }
    }
}
