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
        public IToolDescriptor? GetDescriptor(string name) =>
            _descriptors.TryGetValue(name, out var d) ? d : null;

        /// <summary>Get executor by schema name.</summary>
        public IToolExecutor? GetExecutor(string name) =>
            _executors.TryGetValue(name, out var e) ? e : null;

        /// <summary>
        /// Resolve any tool name form to canonical schema name.
        /// Uses ToolNameResolver for normalization and fuzzy matching.
        /// </summary>
        public string? ResolveName(string calledName)
        {
            // First try exact match (fast path)
            if (_executors.ContainsKey(calledName))
                return calledName;

            // Use resolver for normalization + fuzzy matching
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
            if (_executors.TryGetValue(call.Name, out var executor))
                return await executor.ExecuteAsync(call, ct);

            // Fuzzy match via resolver
            string? resolved = ResolveName(call.Name);
            if (resolved != null && _executors.TryGetValue(resolved, out executor))
                return await executor.ExecuteAsync(call, ct);

            return new ToolResult { ToolName = call.Name, Success = false, Error = $"Tool '{call.Name}' not found." };
        }
    }
}
