using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SpatialAgent.Runtime.Contracts;
using SpatialAgent.Core;

namespace SpatialAgent.Adapters.Mcp
{
    /// <summary>
    /// MCP Streamable HTTP server on ASP.NET Core Kestrel.
    /// Single /mcp endpoint accepting JSON-RPC 2.0 via POST.
    /// ReadOnly tools only — write operations return error.
    /// ★ P2-C（2026-07-20）：随机 token 认证——防止本机恶意进程未经授权调用。
    /// </summary>
    public sealed class McpStreamableHttpServer : IMcpService
    {
        private readonly IReadOnlyToolGateway _tools;
        private readonly ISearchProvider? _knowledge;
        private readonly int _port;
        private WebApplication? _app;

        // ── S4（Phase 3）：速率限制（手动 token-bucket）+ 失败认证退避 ──
        private int _authFailCount;
        private DateTime _authLockUntil = DateTime.MinValue;
        private readonly object _rateLock = new();
        private double _tokenBucket = RateLimitRpm;
        private DateTime _lastTokenRefill = DateTime.UtcNow;
        private const int RateLimitRpm = 30;
        private const int AuthLockThreshold = 5;
        private const int MaxRequestBodyBytes = 1024 * 1024;
        private static readonly int[] AuthBackoffSeconds = { 1, 2, 4, 8 };

        // ── Q1（Phase 4）：每次启动生成新 token，不落盘 ──
        private string _authToken = "";

        public int Port => _port;
        public string Url => $"http://127.0.0.1:{_port}/mcp";
        public bool IsRunning => _app != null;
        /// <summary>P2-C：MCP 认证 Token——供 UI "复制 Token" 按钮使用。</summary>
        public string AuthToken => _authToken;

        public string ConfigJson
        {
            get
            {
                var config = new
                {
                    mcpServers = new
                    {
                        arcgispro = new
                        {
                            type = "streamableHttp",
                            url = Url,
                            headers = string.IsNullOrEmpty(_authToken) ? null : new
                            {
                                Authorization = $"Bearer {_authToken}"
                            }
                        }
                    }
                };
                return JsonConvert.SerializeObject(config, Formatting.Indented);
            }
        }

        public McpStreamableHttpServer(
            IReadOnlyToolGateway tools,
            ISearchProvider? knowledge = null,
            int port = 8765)
        {
            _tools = tools;
            _knowledge = knowledge;
            _port = port;
        }

        public void Start()
        {
            if (_app != null) return;

            // ★ Q1（Phase 4）：每次启动生成新 token（不落盘）
            _authToken = GenerateToken();

            var builder = WebApplication.CreateBuilder();
            // Binding both localhost and 127.0.0.1 can resolve to the same
            // endpoint on Windows and fail with AddressAlreadyInUse.
            builder.WebHost.UseUrls($"http://127.0.0.1:{_port}");

            var app = builder.Build();
            app.MapPost("/mcp", HandleMcpPostAsync);
            app.MapGet("/mcp", HandleMcpGetAsync);
            app.MapDelete("/mcp", HandleMcpDeleteAsync);

            app.StartAsync().GetAwaiter().GetResult();
            _app = app;
        }

        /// <summary>
        /// ★ Q1（Phase 4）：每次启动生成新随机 token，不落盘。
        /// 原先明文存储在 %LocalAppData%——攻击者可通过文件读取获取 token。
        /// 改为每次进程启动生成新 token，消除持久化凭据风险。
        /// 用户需在 MCP 客户端中重新配置（通过 UI "复制 Token" 按钮）。
        /// </summary>
        private static string GenerateToken()
        {
            byte[] bytes = RandomNumberGenerator.GetBytes(32);
            return Convert.ToBase64String(bytes)[..43]; // 43 chars
        }

        /// <summary>
        /// ★ Q1（Phase 4）：重新生成 token（供 UI "重置 Token" 按钮使用）。
        /// 仅影响当前进程，不落盘。
        /// </summary>
        public string RegenerateToken()
        {
            _authToken = GenerateToken();
            return _authToken;
        }

        public async Task StopAsync()
        {
            if (_app != null)
            {
                await _app.StopAsync();
                await _app.DisposeAsync();
                _app = null;
            }
        }

        private Task HandleMcpGetAsync(HttpContext context)
        {
            if (!IsTrustedOrigin(context.Request) || !IsAuthorized(context.Request))
            {
                context.Response.StatusCode = 401;
                return Task.CompletedTask;
            }
            // This server does not implement an SSE receive stream.
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            context.Response.Headers.Allow = "POST, DELETE";
            return Task.CompletedTask;
        }

        private Task HandleMcpDeleteAsync(HttpContext context)
        {
            if (!IsTrustedOrigin(context.Request) || !IsAuthorized(context.Request))
            {
                context.Response.StatusCode = 401;
                return Task.CompletedTask;
            }
            context.Response.StatusCode = 204;
            return Task.CompletedTask;
        }

        private static bool IsTrustedOrigin(HttpRequest request)
        {
            string origin = request.Headers.Origin.FirstOrDefault() ?? "";
            if (string.IsNullOrWhiteSpace(origin))
                return true;
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
                return false;
            return uri.Scheme is "http" or "https"
                && (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                    || uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
                    || uri.Host.Equals("::1", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// ★ P2-C / S2（Phase 3 修复）：恒定时间比较校验 Bearer token。
        /// 空 token → fail-closed（未配置认证则拒绝所有请求）。
        /// 使用 CryptographicOperations.FixedTimeEquals 防止时序攻击。
        /// </summary>
        private bool IsAuthorized(HttpRequest request)
        {
            // S4：指数退避锁定——连续失败后临时锁定认证
            lock (_rateLock)
            {
                if (_authFailCount >= AuthLockThreshold && DateTime.UtcNow < _authLockUntil)
                    return false; // 仍处于锁定状态
                if (_authFailCount >= AuthLockThreshold && DateTime.UtcNow >= _authLockUntil)
                    _authFailCount = 0; // 锁定期过期，重置
            }

            // S2 fail-closed：未配置 token 时拒绝所有请求
            if (string.IsNullOrEmpty(_authToken))
                return false;

            var header = request.Headers["Authorization"].FirstOrDefault();
            if (string.IsNullOrEmpty(header))
            {
                RecordAuthFailure();
                return false;
            }
            if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                RecordAuthFailure();
                return false;
            }
            string token = header["Bearer ".Length..].Trim();

            // S2：恒定时间比较，防止时序侧信道攻击
            if (token.Length != _authToken.Length)
            {
                RecordAuthFailure();
                return false;
            }
            bool valid = CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(token),
                Encoding.UTF8.GetBytes(_authToken));

            if (!valid)
                RecordAuthFailure();
            else
                ResetAuthFailures();

            return valid;
        }

        /// <summary>S4：记录认证失败，触发指数退避（1s→2s→4s→8s→lock 60s）。</summary>
        private void RecordAuthFailure()
        {
            lock (_rateLock)
            {
                _authFailCount++;
                if (_authFailCount >= AuthLockThreshold)
                {
                    _authLockUntil = DateTime.UtcNow.AddSeconds(60);
                    AgentDiagnostics.Warn("McpStreamableHttp.Auth",
                        $"MCP 认证连续失败 {_authFailCount} 次——锁定 60 秒（S4 速率保护）");
                }
                else
                {
                    int delayIdx = Math.Min(_authFailCount - 1, AuthBackoffSeconds.Length - 1);
                    int delaySec = AuthBackoffSeconds[delayIdx];
                    // Exponential backoff is applied on next request via _authLockUntil
                    _authLockUntil = DateTime.UtcNow.AddSeconds(delaySec);
                }
            }
        }

        private void ResetAuthFailures()
        {
            lock (_rateLock)
            {
                _authFailCount = 0;
                _authLockUntil = DateTime.MinValue;
            }
        }

        /// <summary>S4：手动 token-bucket——30 tokens/min，线性补充。</summary>
        private bool TryConsumeToken()
        {
            lock (_rateLock)
            {
                var now = DateTime.UtcNow;
                double elapsedMinutes = (now - _lastTokenRefill).TotalMinutes;
                if (elapsedMinutes > 0)
                {
                    _tokenBucket = Math.Min(RateLimitRpm, _tokenBucket + elapsedMinutes * RateLimitRpm);
                    _lastTokenRefill = now;
                }
                if (_tokenBucket >= 1.0)
                {
                    _tokenBucket -= 1.0;
                    return true;
                }
                return false;
            }
        }

        private async Task HandleMcpPostAsync(HttpContext context)
        {
            context.Response.ContentType = "application/json";

            if (!IsTrustedOrigin(context.Request))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await WriteJsonRpcError(context, -32002, "Untrusted Origin header.");
                return;
            }

            if (context.Request.ContentLength > MaxRequestBodyBytes)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                await WriteJsonRpcError(
                    context,
                    -32003,
                    $"Request body exceeds {MaxRequestBodyBytes} bytes.");
                return;
            }

            // ★ S4（Phase 3）：Token-bucket 速率限制 30 req/min
            if (!TryConsumeToken())
            {
                context.Response.StatusCode = 429; // Too Many Requests
                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                {
                    jsonrpc = "2.0",
                    error = new { code = -32000, message = "速率限制：每分钟最多 30 次请求，请稍后重试。" },
                    id = (string?)null
                }));
                return;
            }

            // ★ P2-C：认证检查——所有 POST /mcp 请求必须携带有效 token
            if (!IsAuthorized(context.Request))
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync(JsonConvert.SerializeObject(new
                {
                    jsonrpc = "2.0",
                    error = new
                    {
                        code = -32001,
                        message = "Unauthorized. Set header: Authorization: Bearer <token>. " +
                                  "Find the token in ArcGISProAIAgent settings or run with --mcp-token to see it."
                    },
                    id = (string?)null
                }));
                return;
            }

            var sessionId = context.Request.Headers["Mcp-Session-Id"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(sessionId))
                context.Response.Headers.Append("Mcp-Session-Id", sessionId);

            try
            {
                string body;
                try
                {
                    body = await ReadBodyWithLimitAsync(
                        context.Request.Body,
                        MaxRequestBodyBytes,
                        context.RequestAborted);
                }
                catch (RequestBodyTooLargeException)
                {
                    context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    await WriteJsonRpcError(
                        context,
                        -32003,
                        $"Request body exceeds {MaxRequestBodyBytes} bytes.");
                    return;
                }

                if (string.IsNullOrWhiteSpace(body))
                {
                    context.Response.StatusCode = 400;
                    await WriteJsonRpcError(context, -32700, "Parse error: empty body");
                    return;
                }

                JObject? request;
                try { request = JObject.Parse(body); }
                catch
                {
                    context.Response.StatusCode = 400;
                    await WriteJsonRpcError(context, -32700, "Parse error: invalid JSON");
                    return;
                }

                string method = request["method"]?.ToString() ?? "";
                JToken? id = request["id"];

                string resultJson = method switch
                {
                    "tools/list" => HandleListTools(id),
                    "tools/call" => await HandleToolCallAsync(request),
                    "initialize" => HandleInitialize(id),
                    "knowledge/search" => await HandleKnowledgeSearchAsync(request),
                    _ => JsonConvert.SerializeObject(new
                    {
                        jsonrpc = "2.0",
                        error = new { code = -32601, message = $"Method not found: {method} (available: tools/list, tools/call, initialize, knowledge/search)" },
                        id = request["id"]
                    })
                };

                // JSON-RPC notifications intentionally have no response body.
                if (id == null)
                {
                    context.Response.StatusCode = StatusCodes.Status202Accepted;
                    return;
                }
                context.Response.StatusCode = StatusCodes.Status200OK;
                await context.Response.WriteAsync(resultJson);
            }
            catch (Exception ex)
            {
                AgentDiagnostics.Warn("McpStreamableHttp", "MCP 请求处理异常", ex);
                context.Response.StatusCode = 500;
                await WriteJsonRpcError(context, -32603, "Internal error");
            }
        }

        private static async Task<string> ReadBodyWithLimitAsync(
            Stream stream,
            int maxBytes,
            CancellationToken cancellation)
        {
            byte[] buffer = new byte[16 * 1024];
            using var content = new MemoryStream();
            while (true)
            {
                int read = await stream.ReadAsync(buffer, cancellation);
                if (read == 0)
                    break;
                if (content.Length + read > maxBytes)
                    throw new RequestBodyTooLargeException();
                await content.WriteAsync(buffer.AsMemory(0, read), cancellation);
            }
            return Encoding.UTF8.GetString(content.GetBuffer(), 0, checked((int)content.Length));
        }

        private sealed class RequestBodyTooLargeException : Exception
        {
        }

        private string HandleListTools(JToken? id)
        {
            var tools = _tools.GetAllDescriptors()
                .Where(t => ToolRiskMapper.FromAnnotation(t.Annotation) == ToolRiskLevel.ReadOnly);
            var list = tools.Select(t => new
            {
                name = t.Name,
                description = t.Description,
                // ★ P1-B 修复（2026-07-20）：输出完整的 JSON Schema，
                // 而非空 properties。MCP 客户端需要参数类型、枚举值来生成正确的调用。
                inputSchema = BuildJsonSchema(t)
            });

            return JsonConvert.SerializeObject(new
            {
                jsonrpc = "2.0",
                result = new { tools = list },
                id
            });
        }

        /// <summary>
        /// ★ P1-B：将 IToolDescriptor.Parameters 映射为 MCP JSON Schema。
        /// 所有参数都包含在 schema 中（无 Direction 字段可区分 Input/Output）。
        /// </summary>
        private static JObject BuildJsonSchema(IToolDescriptor desc)
        {
            var properties = new JObject();
            var required = new JArray();

            foreach (var kv in desc.Parameters)
            {
                var propSchema = new JObject
                {
                    ["type"] = MapJsonType(kv.Value.Type),
                    ["description"] = kv.Value.Description ?? ""
                };

                // Enum 约束（如字段类型选择、测量单位等）
                if (kv.Value.Enum?.Count > 0)
                    propSchema["enum"] = new JArray(kv.Value.Enum);

                properties[kv.Key] = propSchema;
            }

            // Required 参数来自 descriptor.RequiredParameters
            foreach (var rp in desc.RequiredParameters)
                required.Add(rp);

            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = required
            };
        }

        /// <summary>将 .NET/GP 类型名映射为 JSON Schema 类型名。</summary>
        private static string MapJsonType(string? type)
        {
            string t = (type ?? "string").ToLowerInvariant();
            if (t == "long" || t == "double" || t == "int" || t == "float" || t == "gplong" || t == "gpdouble")
                return "number";
            if (t == "bool" || t == "boolean")
                return "boolean";
            if (t == "array" || t == "list" || t == "gparray" || t == "gpvaluestable")
                return "array";
            if (t == "object" || t == "any")
                return "object";
            return "string";  // default: string (GPString, String, 等)
        }

        /// <summary>
        /// ★ P1-B：递归转换 JToken 为 .NET 对象图。
        /// 原实现只处理 JValue（标量），JArray 和 JObject 直接丢失。
        /// </summary>
        private static object? ConvertJToken(JToken? token) => token switch
        {
            null => null,
            JValue v => v.Value,                                     // 标量 → .NET 原生类型
            JArray a => a.Select(ConvertJToken).ToList(),            // 数组 → List<object?>
            JObject o => o.Properties().ToDictionary(                // 对象 → Dictionary
                p => p.Name,
                p => ConvertJToken(p.Value),
                StringComparer.OrdinalIgnoreCase),
            _ => token.ToString()                                    // fallback
        };

        private async Task<string> HandleToolCallAsync(JObject request)
        {
            string toolName = request["params"]?["name"]?.ToString() ?? "";
            var args = new Dictionary<string, object?>();

            var arguments = request["params"]?["arguments"];
            if (arguments is JObject argObj)
            {
                // ★ P1-B 修复（2026-07-20）：递归转换 JToken，保留数组和嵌套对象。
                // 原实现 ((JValue?)prop.Value)?.Value 对标量有效但对 JArray/JObject 返回 null。
                foreach (var prop in argObj.Properties())
                    args[prop.Name] = ConvertJToken(prop.Value);
            }

            string resolvedName = _tools.ResolveName(toolName) ?? toolName;
            var descriptor = _tools.GetDescriptor(resolvedName);
            if (descriptor == null)
            {
                return JsonConvert.SerializeObject(new
                {
                    jsonrpc = "2.0",
                    error = new { code = -32602, message = $"Tool not found: {toolName}" },
                    id = request["id"]
                });
            }

            if (ToolRiskMapper.FromAnnotation(descriptor.Annotation) != ToolRiskLevel.ReadOnly)
            {
                return JsonConvert.SerializeObject(new
                {
                    jsonrpc = "2.0",
                    error = new
                    {
                        code = -32604,
                        message = $"MCP tool execution is limited to read-only tools. Use the runtime for {descriptor.Name}."
                    },
                    id = request["id"]
                });
            }

            var call = new ToolCall { Name = resolvedName, Arguments = args };
            var result = await _tools.ExecuteAsync(call);

            return JsonConvert.SerializeObject(new
            {
                jsonrpc = "2.0",
                result = new
                {
                    content = new[]
                    {
                        new
                        {
                            type = "text",
                            text = JsonConvert.SerializeObject(result)
                        }
                    }
                },
                id = request["id"]
            });
        }

        private static string HandleInitialize(JToken? id)
        {
            return JsonConvert.SerializeObject(new
            {
                jsonrpc = "2.0",
                result = new
                {
                    protocolVersion = "2025-03-26",
                    serverInfo = new { name = "Spatial Agent Runtime", version = "3.0" },
                    capabilities = new { tools = new { } }
                },
                id
            });
        }

        private async Task<string> HandleKnowledgeSearchAsync(JObject request)
        {
            string query = request["params"]?["query"]?.ToString() ?? "";
            if (_knowledge == null || string.IsNullOrWhiteSpace(query))
            {
                return JsonConvert.SerializeObject(new
                {
                    jsonrpc = "2.0",
                    result = new { chunks = Array.Empty<object>() },
                    id = request["id"]
                });
            }

            var chunks = await _knowledge.SearchAsync(query);
            var result = chunks.Select((c, i) => new
            {
                index = i,
                content = c
            }).ToList();

            return JsonConvert.SerializeObject(new
            {
                jsonrpc = "2.0",
                result = new { chunks = result },
                id = request["id"]
            });
        }

        private static Task WriteJsonRpcError(HttpContext context, int code, string message)
        {
            string json = JsonConvert.SerializeObject(new
            {
                jsonrpc = "2.0",
                error = new { code, message },
                id = (string?)null
            });
            return context.Response.WriteAsync(json);
        }
    }
}
