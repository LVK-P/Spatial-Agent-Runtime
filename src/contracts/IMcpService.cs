namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Narrow tool-directory boundary used by transport adapters. It prevents
    /// MCP/HTTP code from depending on the concrete Runtime orchestration
    /// assembly while preserving canonical name resolution and execution.
    /// </summary>
    public interface IReadOnlyToolGateway
    {
        IReadOnlyList<IToolDescriptor> GetAllDescriptors();
        string? ResolveName(string calledName);
        IToolDescriptor? GetDescriptor(string name);
        Task<ToolResult> ExecuteAsync(
            ToolCall call,
            CancellationToken cancellation = default);
    }

    /// <summary>
    /// MCP server lifecycle contract. Implementations live in adapter projects;
    /// hosts wire them into the runtime. The runtime starts the server on first
    /// use and stops it on disposal — it does not depend on any HTTP stack.
    /// </summary>
    public interface IMcpService
    {
        bool IsRunning { get; }
        string Url { get; }
        string ConfigJson { get; }
        void Start();
        Task StopAsync();
    }
}
