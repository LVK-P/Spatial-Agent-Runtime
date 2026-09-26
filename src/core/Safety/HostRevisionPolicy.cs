using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Core
{
    /// <summary>
    /// Pure causal policy for host revision diffs. No ArcGIS calls.
    /// </summary>
    public static class HostRevisionPolicy
    {
        public static HostStateGuardResult Evaluate(
            DataRevisionSnapshot expected,
            DataRevisionSnapshot current,
            ExpectedMutationSet? expectedMutations = null,
            bool planHasCommittedMutation = false)
        {
            var changed = DiffComponents(expected, current);
            if (!expected.IsReliable
                || !current.IsReliable
                || (expected.ContentRevisionRequired && !expected.ContentRevisionReliable)
                || (current.ContentRevisionRequired && !current.ContentRevisionReliable))
            {
                string plannedReason = string.IsNullOrWhiteSpace(expected.ReliabilityReason)
                    ? "unspecified"
                    : expected.ReliabilityReason;
                string currentReason = string.IsNullOrWhiteSpace(current.ReliabilityReason)
                    ? "unspecified"
                    : current.ReliabilityReason;
                return new HostStateGuardResult
                {
                    IsCurrent = false,
                    Decision = HostRevisionDecision.ProbeUnreliable,
                    Reason =
                        "host revision probe is not reliable. " +
                        $"planned={plannedReason}; current={currentReason}",
                    Current = current,
                    ChangedComponents = changed
                };
            }

            if (changed.Count == 0)
            {
                return new HostStateGuardResult
                {
                    IsCurrent = true,
                    Decision = HostRevisionDecision.Current,
                    Reason = "",
                    Current = current,
                    ChangedComponents = changed
                };
            }

            bool onlySelfWriteSurfaces = changed.All(component =>
                component is "layer" or "content" or "map");
            bool hasBudget = expectedMutations != null
                && (expectedMutations.HasMutatingEffects || !expectedMutations.IsEmpty);
            // A mutation budget is only a forecast. Before this plan has
            // actually committed a write, it can never explain a live host
            // change: that change is external by definition.
            bool selfExplained = onlySelfWriteSurfaces
                && planHasCommittedMutation
                && hasBudget
                && !changed.Contains("project");

            if (selfExplained)
            {
                return new HostStateGuardResult
                {
                    // Treat as current for scheduling: adopt current as baseline.
                    IsCurrent = true,
                    Decision = HostRevisionDecision.SelfCommitted,
                    Reason =
                        "host revision changed within the plan mutation budget "
                        + $"({string.Join(",", changed)})",
                    Current = current,
                    ChangedComponents = changed
                };
            }

            if (!planHasCommittedMutation
                || changed.Contains("project")
                || changed.Contains("selection")
                || !onlySelfWriteSurfaces)
            {
                return new HostStateGuardResult
                {
                    IsCurrent = false,
                    Decision = HostRevisionDecision.ExternalChange,
                    Reason =
                        "ArcGIS project, map, schema, selection, or data content "
                        + "changed after planning",
                    Current = current,
                    ChangedComponents = changed
                };
            }

            return new HostStateGuardResult
            {
                IsCurrent = false,
                Decision = HostRevisionDecision.Mixed,
                Reason =
                    "host revision partially matches plan mutations and external edits",
                Current = current,
                ChangedComponents = changed
            };
        }

        public static List<string> DiffComponents(
            DataRevisionSnapshot expected,
            DataRevisionSnapshot current)
        {
            var changed = new List<string>();
            if (!Eq(expected.ProjectFingerprint, current.ProjectFingerprint))
                changed.Add("project");
            if (!Eq(expected.MapFingerprint, current.MapFingerprint))
                changed.Add("map");
            if (!Eq(expected.LayerFingerprint, current.LayerFingerprint))
                changed.Add("layer");
            if (!Eq(expected.SelectionFingerprint, current.SelectionFingerprint))
                changed.Add("selection");
            if (!Eq(expected.ContentFingerprint, current.ContentFingerprint))
                changed.Add("content");
            return changed;
        }

        private static bool Eq(string? left, string? right) =>
            string.Equals(
                left ?? "",
                right ?? "",
                StringComparison.OrdinalIgnoreCase);
    }
}
