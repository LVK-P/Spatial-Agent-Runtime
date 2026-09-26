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
    public interface IWorkspaceAwareTool
    {
        /// <summary>Attach the initialized agent workspace. Idempotent.</summary>
        void AttachWorkspace(IWorkspaceService workspace);
    }
}
