using System.Security.Cryptography;
using System.Text;
using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Core
{
    /// <summary>
    /// Promotes successful read-only support probes into typed binding patches.
    /// It consumes only physical input identity, typed observation, host schema
    /// and goal lineage constraints; model prose and tool names are unavailable.
    /// </summary>
    public sealed class DefaultResourceBindingEvidencePromoter :
        IResourceBindingEvidencePromoter
    {
        public ResourceBindingPromotionResult Evaluate(
            ResolvedGoalScope scope,
            AgentPlan plan,
            IReadOnlyList<StepExecutionResult> results,
            DomainContext domain)
        {
            ArgumentNullException.ThrowIfNull(scope);
            ArgumentNullException.ThrowIfNull(plan);
            ArgumentNullException.ThrowIfNull(results);
            ArgumentNullException.ThrowIfNull(domain);

            var output = new ResourceBindingPromotionResult();
            bool lineageRequired =
                GisCategoryNormalizer.HasLineagePreferenceLanguage(scope.SourceGoal);

            foreach (StepExecutionResult result in results.Where(IsEligibleProbe))
            {
                var step = plan.Steps.FirstOrDefault(item => item.Id == result.StepId);
                if (step == null)
                    continue;

                foreach (GoalResourceBinding binding in scope.ResourceBindings.Where(
                             item => item.Status is GoalResourceResolutionStatus.Ambiguous
                                 or GoalResourceResolutionStatus.Missing
                                 or GoalResourceResolutionStatus.CandidateFoundAwaitingConfirmation))
                {
                    GoalResourceCandidate? candidate = binding.Candidates
                        .FirstOrDefault(item => ProbeTargetsCandidate(result, step, item));
                    if (candidate == null)
                        continue;

                    bool identityMatched = candidate.HasStableIdentity
                        && ResourceNameMatches(
                            result.Observation?.Output.ResourceName,
                            candidate);
                    bool schemaMatched = SchemaMatches(
                        result.Observation?.Output,
                        candidate);
                    bool lineageMatched = !lineageRequired
                        || PreferredCandidates(binding).Contains(
                            candidate.CandidateId,
                            StringComparer.OrdinalIgnoreCase);
                    string evidenceId = ComputeEvidenceId(
                        scope.GoalId,
                        scope.Version,
                        binding.Mention,
                        candidate.CandidateId,
                        result.ToolCallId,
                        candidate.SchemaFingerprint);
                    output.Evidence.Add(new ResourceResolutionEvidence
                    {
                        EvidenceId = evidenceId,
                        BindingMention = binding.Mention,
                        CandidateId = candidate.CandidateId,
                        DisplayName = candidate.DisplayName,
                        MapMemberId = candidate.MapMemberId,
                        DatasetId = candidate.DatasetId,
                        SchemaFingerprint = candidate.SchemaFingerprint,
                        GeometryType = result.Observation?.Output.GeometryType ?? "",
                        Fields = result.Observation?.Output.Fields?.ToList()
                            ?? new List<string>(),
                        LineageRoot = candidate.LineageRoot,
                        LineageDepth = candidate.LineageDepth,
                        ToolCallId = result.ToolCallId,
                        StepId = result.StepId,
                        IdentityMatched = identityMatched,
                        SchemaMatched = schemaMatched,
                        LineageMatched = lineageMatched,
                        Detail = identityMatched && schemaMatched && lineageMatched
                            ? "stable identity, schema and lineage contracts matched"
                            : "probe evidence did not satisfy every binding contract"
                    });
                }
            }

            foreach (GoalResourceBinding binding in scope.ResourceBindings.Where(
                         item => item.Status is GoalResourceResolutionStatus.Ambiguous
                             or GoalResourceResolutionStatus.Missing
                             or GoalResourceResolutionStatus.CandidateFoundAwaitingConfirmation))
            {
                var evidence = output.Evidence
                    .Where(item => item.BindingMention.Equals(
                        binding.Mention,
                        StringComparison.OrdinalIgnoreCase))
                    .Where(item => item.IsAdmissible)
                    .GroupBy(item => item.CandidateId, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.Last())
                    .ToList();
                if (evidence.Count == 0)
                    continue;

                var admissibleCandidateIds = lineageRequired
                    ? PreferredCandidates(binding)
                    : binding.Candidates.Select(candidate => candidate.CandidateId).ToList();
                var evidenceForAdmissible = evidence
                    .Where(item => admissibleCandidateIds.Contains(
                        item.CandidateId,
                        StringComparer.OrdinalIgnoreCase))
                    .ToList();
                if (evidenceForAdmissible.Count == 0)
                    continue;

                bool uniqueAuthorization = admissibleCandidateIds
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count() == 1
                    && evidenceForAdmissible.Any(item => item.CandidateId.Equals(
                        admissibleCandidateIds[0],
                        StringComparison.OrdinalIgnoreCase));
                ResourceResolutionEvidence? selected = uniqueAuthorization
                    ? evidenceForAdmissible.First(item => item.CandidateId.Equals(
                        admissibleCandidateIds[0],
                        StringComparison.OrdinalIgnoreCase))
                    : null;

                output.Patches.Add(new ResourceBindingPatch
                {
                    BindingMention = binding.Mention,
                    PreviousStatus = binding.Status,
                    NextStatus = selected == null
                        ? GoalResourceResolutionStatus.CandidateFoundAwaitingConfirmation
                        : GoalResourceResolutionStatus.Resolved,
                    ResolvedCandidateId = selected?.CandidateId ?? "",
                    ResolvedTarget = selected?.DisplayName ?? "",
                    EvidenceCandidateIds = evidenceForAdmissible
                        .Select(item => item.CandidateId)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    EvidenceIds = evidenceForAdmissible
                        .Select(item => item.EvidenceId)
                        .ToList(),
                    Reason = selected == null
                        ? "probe confirmed candidates but deterministic constraints remain non-unique"
                        : "probe and deterministic identity/schema/lineage constraints selected one candidate"
                });
            }

            return output;
        }

        private static bool IsEligibleProbe(StepExecutionResult result)
        {
            if (!result.IsSuccessful
                || result.Purpose != PlanStepPurpose.SupportProbe
                || result.Observation == null)
                return false;
            DataEffectKind effects =
                result.Effect?.Profile.Effects ?? DataEffectKind.None;
            return (effects & ~DataEffectKind.ReadData) == DataEffectKind.None;
        }

        private static bool ProbeTargetsCandidate(
            StepExecutionResult result,
            PlanStep step,
            GoalResourceCandidate candidate)
        {
            IEnumerable<string> values = result.InputParameters.Values
                .SelectMany(FlattenValues)
                .Concat(step.ReadSet);
            return values.Any(value => Matches(value, candidate.DisplayName)
                || Matches(value, candidate.DatasetId));
        }

        private static IEnumerable<string> FlattenValues(object? value)
        {
            if (value == null)
                yield break;
            if (value is string text)
            {
                yield return text;
                yield break;
            }
            if (value is System.Collections.IEnumerable sequence)
            {
                foreach (object? item in sequence)
                foreach (string nested in FlattenValues(item))
                    yield return nested;
                yield break;
            }
            yield return value.ToString() ?? "";
        }

        private static bool ResourceNameMatches(
            string? observed,
            GoalResourceCandidate candidate) =>
            !string.IsNullOrWhiteSpace(observed)
            && (Matches(observed, candidate.DisplayName)
                || Matches(observed, candidate.DatasetId));

        private static bool SchemaMatches(
            ObservationOutput? observed,
            GoalResourceCandidate candidate)
        {
            if (observed == null
                || observed.Fields == null
                || string.IsNullOrWhiteSpace(candidate.SchemaFingerprint)
                || candidate.Fields.Count == 0)
                return false;
            bool fieldsMatch = observed.Fields.ToHashSet(
                    StringComparer.OrdinalIgnoreCase)
                .SetEquals(candidate.Fields);
            bool geometryMatch = string.IsNullOrWhiteSpace(candidate.GeometryType)
                || GisCategoryNormalizer.NormalizeGeometry(observed.GeometryType)
                    .Equals(
                        GisCategoryNormalizer.NormalizeGeometry(candidate.GeometryType),
                        StringComparison.OrdinalIgnoreCase);
            return fieldsMatch && geometryMatch;
        }

        private static List<string> PreferredCandidates(GoalResourceBinding binding)
        {
            var layers = binding.Candidates.Select(candidate => new LayerInfo
            {
                Name = candidate.DisplayName,
                MapMemberId = candidate.MapMemberId,
                DatasetId = candidate.DatasetId,
                GeometryType = candidate.GeometryType,
                Fields = candidate.Fields.ToList(),
                SchemaFingerprint = candidate.SchemaFingerprint,
                LineageRoot = candidate.LineageRoot,
                LineageDepth = candidate.LineageDepth
            });
            var preferredNames = DefaultScopeExpander
                .CollapseToOperableRepresentatives(layers);
            return binding.Candidates
                .Where(candidate => preferredNames.Contains(
                    candidate.DisplayName,
                    StringComparer.OrdinalIgnoreCase))
                .Select(candidate => candidate.CandidateId)
                .ToList();
        }

        private static bool Matches(string? left, string? right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
                return false;
            string a = left.Trim().Replace('/', '\\').TrimEnd('\\');
            string b = right.Trim().Replace('/', '\\').TrimEnd('\\');
            return a.Equals(b, StringComparison.OrdinalIgnoreCase)
                || a.EndsWith("\\" + b, StringComparison.OrdinalIgnoreCase)
                || b.EndsWith("\\" + a, StringComparison.OrdinalIgnoreCase);
        }

        private static string ComputeEvidenceId(params object[] values)
        {
            string canonical = string.Join("|", values.Select(value => value?.ToString() ?? ""));
            return "binding-evidence:"
                + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
                    .ToLowerInvariant();
        }
    }

    public static class ResourceBindingPatchApplier
    {
        public static void Apply(
            ResolvedGoalScope scope,
            ResourceBindingPromotionResult promotion,
            DomainContext domain)
        {
            foreach (ResourceBindingPatch patch in promotion.Patches)
            {
                GoalResourceBinding? binding = scope.ResourceBindings.FirstOrDefault(item =>
                    item.Mention.Equals(
                        patch.BindingMention,
                        StringComparison.OrdinalIgnoreCase));
                if (binding == null)
                    continue;
                binding.Status = patch.NextStatus;
                binding.EvidenceBackedCandidateIds = patch.EvidenceCandidateIds.ToList();
                binding.Evidence = patch.Reason;
                binding.ResolvedTargets.Clear();

                if (patch.NextStatus != GoalResourceResolutionStatus.Resolved)
                    continue;
                GoalResourceCandidate? candidate = binding.Candidates.FirstOrDefault(item =>
                    item.CandidateId.Equals(
                        patch.ResolvedCandidateId,
                        StringComparison.OrdinalIgnoreCase));
                if (candidate == null)
                    continue;
                binding.ResolvedTargets.Add(candidate.DisplayName);
                scope.UnresolvedTargets.RemoveAll(item => item.Equals(
                    binding.Mention,
                    StringComparison.OrdinalIgnoreCase));
                List<string> destination = binding.Role == GoalResourceRole.Reference
                    ? scope.ReferenceTargets
                    : scope.ExplicitTargets;
                if (!destination.Contains(
                        candidate.DisplayName,
                        StringComparer.OrdinalIgnoreCase))
                    destination.Add(candidate.DisplayName);
                UpsertHandle(scope, candidate, domain);
            }

            if (scope.UnresolvedTargets.Count == 0
                && scope.ExplicitTargets.Count > 0)
            {
                scope.ScopeSufficiency = GoalScopeSufficiency.Sufficient;
                scope.ScopeExpansionRule = "binding-evidence-promotion";
                scope.ScopeBasis = "resolved:binding-evidence";
            }
            scope.ContractFingerprint =
                DefaultGoalScopeResolver.ComputeFingerprint(scope);
        }

        private static void UpsertHandle(
            ResolvedGoalScope scope,
            GoalResourceCandidate candidate,
            DomainContext domain)
        {
            LayerInfo? layer = domain.Layers.FirstOrDefault(item =>
                (!string.IsNullOrWhiteSpace(candidate.MapMemberId)
                    && item.MapMemberId.Equals(
                        candidate.MapMemberId,
                        StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(candidate.DatasetId)
                    && item.DatasetId.Equals(
                        candidate.DatasetId,
                        StringComparison.OrdinalIgnoreCase)));
            scope.ResourceHandles.RemoveAll(handle =>
                (!string.IsNullOrWhiteSpace(candidate.MapMemberId)
                    && handle.MapMemberId.Equals(
                        candidate.MapMemberId,
                        StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(candidate.DatasetId)
                    && handle.DatasetId.Equals(
                        candidate.DatasetId,
                        StringComparison.OrdinalIgnoreCase)));
            scope.ResourceHandles.Add(new GoalResourceHandle
            {
                DisplayName = layer?.Name ?? candidate.DisplayName,
                MapMemberId = layer?.MapMemberId ?? candidate.MapMemberId,
                DatasetId = layer?.DatasetId ?? candidate.DatasetId,
                SchemaFingerprint = layer?.SchemaFingerprint
                    ?? candidate.SchemaFingerprint,
                StateFingerprint = layer?.StateFingerprint ?? "",
                StateFingerprintReliable =
                    layer?.StateFingerprintReliable ?? false,
                LineageRoot = layer?.LineageRoot ?? candidate.LineageRoot,
                LineageDepth = layer?.LineageDepth ?? candidate.LineageDepth
            });
        }
    }
}
