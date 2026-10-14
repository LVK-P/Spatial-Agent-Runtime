namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Scheduler — reads a plan, dispatches tools sequentially.
    /// Every tool_call that is settled for model continuation produces exactly
    /// one StepExecutionResult. An approval boundary may suspend the plan with
    /// later calls still pending; the caller must not continue the model until
    /// that suspended transaction is resumed or explicitly rejected.
    /// </summary>
    /// <remarks>中文：Scheduler 读取计划并按顺序调度工具。每个已结算、可继续模型对话的 tool_call 恰好产生一个 StepExecutionResult。审批边界可能在后续调用仍待处理时暂停计划；暂停事务恢复或被明确拒绝前，调用方不得继续模型对话。</remarks>
    public interface IScheduler
    {
        /// <summary>
        /// Execute a plan step by step. Non-approval exits return exactly one
        /// result per active batch step, including blocked/skipped.
        /// </summary>
        /// <remarks>中文：逐步执行计划。除等待审批外，每种退出路径都为活动批次中的每个步骤返回且仅返回一个结果，包括被阻止或跳过的步骤。</remarks>
        Task<IReadOnlyList<StepExecutionResult>> RunAsync(AgentPlan plan, CancellationToken cancellation = default);
        AgentPermissionLevel CurrentPermissionLevel { get; set; }
    }
}
