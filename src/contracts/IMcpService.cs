namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Narrow tool-directory boundary used by transport adapters. It prevents
    /// MCP/HTTP code from depending on the concrete Runtime orchestration
    /// assembly while preserving canonical name resolution and execution.
    /// </summary>
    /// <remarks>中文：传输适配器使用的窄工具目录边界。它防止MCP/HTTP代码依赖于具体的运行时编排汇编，同时保持规范的名称解析和执行。</remarks>
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
    /// <remarks>中文：MCP 服务器生命周期契约。实现在适配器项目中，由宿主接入 Runtime。Runtime 首次使用时启动服务器，释放时停止服务器，且不依赖任何 HTTP 栈。</remarks>
    public interface IMcpService
    {
        bool IsRunning { get; }
        string Url { get; }
        string ConfigJson { get; }
        void Start();
        Task StopAsync();
    }
}
