namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Implemented by tools whose output routing depends on the agent workspace
    /// (agentworkspace.gdb / agentbackup.gdb).
    ///
    /// Lifecycle note: tools are registered during host construction (phase 1),
    /// but the workspace is created during project initialization (phase 2,
    /// first agent open with a project). The host retrofits the workspace into
    /// already-registered tools via this hook — without it, bare-name outputs
    /// fall through to the project default gdb.
    /// </summary>
    /// <remarks>中文：由输出路由依赖 Agent 工作区（agentworkspace.gdb / agentbackup.gdb）的工具实现。工具在宿主构造阶段（阶段 1）注册，工作区则在项目初始化期间（阶段 2，首次在项目中打开 Agent）创建。宿主通过此钩子把工作区回填到已注册工具；否则裸名称输出会落入项目默认 GDB。</remarks>
    public interface IWorkspaceAwareTool
    {
        /// <summary>Attach the initialized agent workspace. Idempotent.</summary>
        /// <remarks>中文：附加已初始化的 Agent 工作区；该操作必须幂等。</remarks>
        void AttachWorkspace(IWorkspaceService workspace);
    }
}
