namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Scheduler — reads a plan, dispatches tools sequentially.
    /// Every tool_call that is settled for model continuation produces exactly
    /// one StepExecutionResult. An approval boundary may suspend the plan with
    /// later calls still pending; the caller must not continue the model until
    /// that suspended transaction is resumed or explicitly rejected.
    /// </summary>
    public interface IScheduler
    {
        /// <summary>
        /// Execute a plan step by step. Non-approval exits return exactly one
        /// result per active batch step, including blocked/skipped.
        /// </summary>
        Task<IReadOnlyList<StepExecutionResult>> RunAsync(AgentPlan plan, CancellationToken cancellation = default);
        AgentPermissionLevel CurrentPermissionLevel { get; set; }
    }
}
