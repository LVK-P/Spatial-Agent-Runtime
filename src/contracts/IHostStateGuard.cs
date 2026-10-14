namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Causal interpretation of a host revision check. Scheduler policy binds
    /// to this enum, not to free-text reasons.
    /// </summary>
    /// <remarks>中文：宿主修订检查的因果解释。Scheduler 策略绑定到此枚举，而不是自由文本原因。</remarks>
    public enum HostRevisionDecision
    {
        /// <summary>Fingerprints match; safe to execute.</summary>
        /// <remarks>中文：指纹匹配，可以安全执行。</remarks>
        Current,
        /// <summary>
        /// Diff is explained by this plan's expected mutations or a prior
        /// successful write in the same plan transaction.
        /// </summary>
        /// <remarks>中文：差异可由该计划的预期修改，或同一计划事务中先前成功的写入来解释。</remarks>
        SelfCommitted,
        /// <summary>Reliable external change outside the plan mutation budget.</summary>
        /// <remarks>中文：计划修改预算之外的可靠外部变更。</remarks>
        ExternalChange,
        /// <summary>One or more revision probes are unreliable.</summary>
        /// <remarks>中文：一个或多个修订探针不可靠。</remarks>
        ProbeUnreliable,
        /// <summary>Partially expected and partially external; conservative stop.</summary>
        /// <remarks>中文：变更一部分符合预期、一部分来自外部；采取保守策略停止执行。</remarks>
        Mixed
    }

    /// <summary>
    /// Optimistic concurrency boundary between planning and host execution.
    /// ArcGIS state is mutable by the user and by other extensions, so every
    /// tool step must prove that it still sees the revision on which its plan
    /// was based — or that the diff is explained by the plan itself.
    /// </summary>
    /// <remarks>中文：规划与宿主执行之间的乐观并发边界。用户和其他扩展都能修改 ArcGIS 状态，因此每个工具步骤必须证明当前仍是计划所基于的修订，或证明差异由计划本身造成。</remarks>
    public interface IHostStateGuard
    {
        Task<DataRevisionSnapshot> CaptureAsync(
            CancellationToken cancellation = default);

        Task<HostStateGuardResult> ValidateAsync(
            DataRevisionSnapshot expected,
            CancellationToken cancellation = default);

        /// <summary>
        /// Execution-time check that may accept self-committed mutations inside
        /// the current plan transaction.
        /// </summary>
        /// <remarks>中文：执行时检查，可接受当前计划事务中由计划自身提交的修改。</remarks>
        Task<HostStateGuardResult> ValidateForExecutionAsync(
            DataRevisionSnapshot expected,
            ExpectedMutationSet? expectedMutations,
            bool planHasCommittedMutation,
            CancellationToken cancellation = default);
    }

    public sealed class HostStateGuardResult
    {
        public bool IsCurrent { get; set; }
        public HostRevisionDecision Decision { get; set; } =
            HostRevisionDecision.ExternalChange;
        public string Reason { get; set; } = "";
        public DataRevisionSnapshot Current { get; set; } = new();
        public List<string> ChangedComponents { get; set; } = new();
    }

    /// <summary>
    /// One resource the physical plan expects to mutate. Built from resolved
    /// invocation effects — never from user-intent keywords.
    /// </summary>
    /// <remarks>中文：物理计划预期修改的资源。它根据已解析的调用效果构建，绝不依赖用户意图关键词。</remarks>
    public sealed class ExpectedMutation
    {
        public string ResourceKey { get; set; } = "";
        public string Dataset { get; set; } = "";
        public string Layer { get; set; } = "";
        public string OutputTarget { get; set; } = "";
        public DataEffectKind Effects { get; set; }
        public int StepId { get; set; }
    }

    /// <summary>
    /// Plan-level mutation budget used by the causal host guard.
    /// </summary>
    /// <remarks>中文：因果宿主守卫使用的计划级修改预算。</remarks>
    public sealed class ExpectedMutationSet
    {
        public List<ExpectedMutation> Items { get; set; } = new();

        public bool IsEmpty => Items.Count == 0;

        public bool CoversResource(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;
            return Items.Any(item =>
                NameEquals(item.ResourceKey, name)
                || NameEquals(item.Dataset, name)
                || NameEquals(item.Layer, name)
                || NameEquals(item.OutputTarget, name)
                || EndsWithName(item.OutputTarget, name)
                || EndsWithName(item.Dataset, name));
        }

        public bool HasMutatingEffects =>
            Items.Any(item =>
                (item.Effects & ~DataEffectKind.ReadData) != DataEffectKind.None);

        private static bool NameEquals(string? left, string right) =>
            !string.IsNullOrWhiteSpace(left)
            && left.Equals(right, StringComparison.OrdinalIgnoreCase);

        private static bool EndsWithName(string? path, string name)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(name))
                return false;
            return path.EndsWith("\\" + name, StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase)
                || path.Equals(name, StringComparison.OrdinalIgnoreCase);
        }
    }
}
