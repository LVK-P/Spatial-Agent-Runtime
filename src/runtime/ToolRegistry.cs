using System.Threading;
using SpatialAgent.Runtime.Contracts;
using SpatialAgent.Core;

namespace SpatialAgent.Runtime
{
    public class ToolRegistry : IReadOnlyToolGateway
    {
        private readonly Dictionary<string, IToolDescriptor> _descriptors = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IToolExecutor> _executors = new(StringComparer.OrdinalIgnoreCase);
        private readonly IEventBus? _eventBus;
        private readonly ToolNameResolver _resolver = new();

        public ToolNameResolver NameResolver => _resolver;

        public ToolRegistry(IEventBus? eventBus = null)
        {
            _eventBus = eventBus;
        }

        public void Register(ITool tool)
        {
            string name = tool.Name;
            // ★ Skip if already registered (core tools win over dynamic)
            // 中文：如果已经注册了，可以跳过（核心工具胜过动态工具）
            if (_executors.ContainsKey(name)) return;
            _descriptors[name] = tool;
            _executors[name] = tool;
            _resolver.Register(name);
            _eventBus?.Publish(new AgentEvent { EventType = "tool.registered", Metadata = new() { ["name"] = name } });
        }

        public void Register(IToolDescriptor descriptor, IToolExecutor executor)
        {
            string name = descriptor.Name;
            if (_executors.ContainsKey(name)) return;
            _descriptors[name] = descriptor;
            _executors[name] = executor;
            _resolver.Register(name);
            _eventBus?.Publish(new AgentEvent { EventType = "tool.registered", Metadata = new() { ["name"] = name } });
        }

        public IReadOnlyList<IToolDescriptor> GetAllDescriptors() => _descriptors.Values.ToList().AsReadOnly();

        /// <summary>Get descriptor by schema name.</summary>
        /// <remarks>中文：按模式名称获取描述符。</remarks>
        public IToolDescriptor? GetDescriptor(string name) =>
            _descriptors.TryGetValue(name, out var d) ? d : null;

        /// <summary>Get executor by schema name.</summary>
        /// <remarks>中文：通过模式名称获取执行人。</remarks>
        public IToolExecutor? GetExecutor(string name) =>
            _executors.TryGetValue(name, out var e) ? e : null;

        /// <summary>
        /// Resolve any tool name form to canonical schema name.
        /// Uses ToolNameResolver for normalization and fuzzy matching.
        /// </summary>
        /// <remarks>中文：将任何工具名表单解析为规范模式名称。使用ToolNameResolver进行归一化和模糊匹配。</remarks>
        public string? ResolveName(string calledName)
        {
            // First try exact match (fast path)
            // 中文：第一次尝试完全匹配（快速路径）
            if (_executors.ContainsKey(calledName))
                return calledName;

            // Use resolver for normalization + fuzzy matching
            // 中文：使用解析器进行归一化+模糊匹配
            string resolved = _resolver.Resolve(calledName);
            if (!string.IsNullOrEmpty(resolved) && _executors.ContainsKey(resolved))
                return resolved;

            return null;
        }

        public int Count => _executors.Count;
        public IEnumerable<string> ToolNames => _executors.Keys;

        /// <summary>
        /// Retrofit the agent workspace into all workspace-aware tools.
        /// Called at phase-2 project init — tools register in phase 1 before the
        /// workspace exists, so bare-name output routing is dead until this runs.
        /// Returns the number of tools attached.
        /// </summary>
        /// <remarks>中文：将代理工作区改造为所有工作区识别工具。在阶段2调用项目init——工具在工作区存在前注册于阶段1，因此裸名输出路由在工作区运行前是死的。返回连接的工具数量。</remarks>
        public int AttachWorkspace(IWorkspaceService workspace)
        {
            int attached = 0;
            foreach (var executor in _executors.Values)
            {
                if (executor is IWorkspaceAwareTool aware)
                {
                    aware.AttachWorkspace(workspace);
                    attached++;
                }
            }
            return attached;
        }

        public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken ct = default)
        {
            // Exact match first
            // 中文：先确认完全匹配
            if (_executors.TryGetValue(call.Name, out var executor))
                return await executor.ExecuteAsync(call, ct);

            // Fuzzy match via resolver
            // 中文：通过解析器实现模糊匹配
            string? resolved = ResolveName(call.Name);
            if (resolved != null && _executors.TryGetValue(resolved, out executor))
                return await executor.ExecuteAsync(call, ct);

            return new ToolResult { ToolName = call.Name, Success = false, Error = $"Tool '{call.Name}' not found." };
        }
    }
}
