namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Causal interpretation of a host revision check. Scheduler policy binds
    /// to this enum, not to free-text reasons.
    /// </summary>
    public enum HostRevisionDecision
    {
        /// <summary>Fingerprints match; safe to execute.</summary>
        Current,
        /// <summary>
        /// Diff is explained by this plan's expected mutations or a prior
        /// successful write in the same plan transaction.
        /// </summary>
        SelfCommitted,
        /// <summary>Reliable external change outside the plan mutation budget.</summary>
        ExternalChange,
        /// <summary>One or more revision probes are unreliable.</summary>
        ProbeUnreliable,
        /// <summary>Partially expected and partially external; conservative stop.</summary>
        Mixed
    }

    /// <summary>
    /// Optimistic concurrency boundary between planning and host execution.
    /// ArcGIS state is mutable by the user and by other extensions, so every
    /// tool step must prove that it still sees the revision on which its plan
    /// was based — or that the diff is explained by the plan itself.
    /// </summary>
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
