using System.Text.RegularExpressions;
using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Core
{
    /// <summary>
    /// Validates a physical plan against an already compiled goal. It cannot
    /// resolve, broaden or commit goal state.
    /// </summary>
    public sealed class DefaultPlanCoverageValidator : IPlanCoverageValidator
    {
        public TargetCoverageResult Validate(
            ResolvedGoalScope scope,
            AgentPlan plan,
            DomainContext context)
        {
            var knownTargets = context.Layers
                .Select(layer => layer.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToList();
            bool hasBusinessWrites = plan.Steps.Any(IsWriteStep);
            bool outputOnlyWrites = hasBusinessWrites
                && plan.Steps
                    .Where(IsWriteStep)
                    .All(IsNewOutputOnlyStep);
            var planned = ResolvePlannedTargets(
                plan,
                knownTargets,
                writesOnly: hasBusinessWrites);

            if (!hasBusinessWrites)
            {
                return new TargetCoverageResult
                {
                    IsApplicable = false,
                    Applicability = CoverageApplicability.NotInScope,
                    IsComplete = scope.UnresolvedTargets.Count == 0
                        && scope.UnresolvedRequirements.Count == 0,
                    ScopeBasis = scope.ScopeBasis,
                    PlannedTargets = planned.OrderByName(),
                    ReferenceTargets = scope.ReferenceTargets.ToList(),
                    UnresolvedTargets = scope.UnresolvedTargets.ToList(),
                    UnresolvedRequirements =
                        scope.UnresolvedRequirements.ToList()
                };
            }

            if (outputOnlyWrites
                && scope.ExplicitTargets.Count == 0
                && scope.UnresolvedTargets.Count == 0)
            {
                return new TargetCoverageResult
                {
                    IsApplicable = false,
                    Applicability = CoverageApplicability.OutputOnly,
                    IsComplete = true,
                    ScopeBasis = "output-only",
                    PlannedTargets = planned.OrderByName(),
                    ReferenceTargets = scope.ReferenceTargets.ToList()
                };
            }

            // The goal compiler declared mutating work without a determinate
            // target set. This uncertainty cannot be converted into write
            // authorization by the physical plan.
            if (scope.ScopeSufficiency is GoalScopeSufficiency.Insufficient
                    or GoalScopeSufficiency.ClarificationRequired)
            {
                return new TargetCoverageResult
                {
                    IsApplicable = true,
                    Applicability = CoverageApplicability.ScopeInsufficient,
                    IsComplete = false,
                    ScopeBasis = scope.ScopeBasis,
                    PlannedTargets = planned.OrderByName(),
                    ReferenceTargets = scope.ReferenceTargets.ToList(),
                    UnresolvedTargets = scope.UnresolvedTargets.ToList(),
                    UnresolvedRequirements =
                        scope.UnresolvedRequirements.ToList(),
                    UnexpectedTargets = planned.OrderByName()
                };
            }

            bool freeFormMutation =
                scope.ScopeSufficiency == GoalScopeSufficiency.NotRequired
                && scope.ExplicitTargets.Count == 0
                && scope.DomainCategories.Count == 0
                && scope.UnresolvedTargets.Count == 0
                && scope.Quantifier == GoalTargetQuantifier.Unspecified;

            if (freeFormMutation)
            {
                // Existing-state mutation without a compiled target boundary is
                // not authorized. New artifacts are handled by OutputOnly above.
                return new TargetCoverageResult
                {
                    IsApplicable = true,
                    Applicability = CoverageApplicability.ScopeInsufficient,
                    IsComplete = false,
                    ScopeBasis = "scope-insufficient:free-form-mutation",
                    PlannedTargets = planned.OrderByName(),
                    ReferenceTargets = scope.ReferenceTargets.ToList(),
                    UnresolvedTargets = scope.UnresolvedTargets.ToList(),
                    UnresolvedRequirements =
                        scope.UnresolvedRequirements.ToList(),
                    UnexpectedTargets = planned.OrderByName()
                };
            }

            var candidates = ResolveGoalCandidates(scope, context);
            IReadOnlyList<LayerInfo> goalLayers = scope.Quantifier ==
                GoalTargetQuantifier.RepresentativePerLineage
                ? CollapseLineage(candidates)
                : candidates;
            var goalTargets = goalLayers
                .Select(layer => layer.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var alreadySatisfied = goalLayers
                .Where(layer => IsAlreadyInDesiredState(layer, scope))
                .Select(layer => layer.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var satisfied = scope.SatisfiedTargets
                .Concat(alreadySatisfied)
                .Where(target => goalTargets.Contains(
                    target,
                    StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var expected = goalTargets
                .Where(target => !satisfied.Contains(
                    target,
                    StringComparer.OrdinalIgnoreCase))
                .ToList();
            var missing = expected
                .Where(target => !planned.Contains(target))
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var allowed = goalTargets
                .Concat(scope.ReferenceTargets)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unexpected = planned
                .Where(target => !allowed.Contains(target))
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var redundant = scope.Quantifier ==
                GoalTargetQuantifier.RepresentativePerLineage
                ? FindRedundantLineageTargets(
                    candidates,
                    planned,
                    goalTargets,
                    scope.ExplicitTargets)
                : new List<string>();

            bool applicable = goalTargets.Count > 0
                || scope.UnresolvedTargets.Count > 0
                || scope.Quantifier != GoalTargetQuantifier.Unspecified
                || scope.ScopeSufficiency == GoalScopeSufficiency.Sufficient;
            return new TargetCoverageResult
            {
                IsApplicable = applicable,
                Applicability = applicable
                    ? CoverageApplicability.Applicable
                    : CoverageApplicability.NotInScope,
                IsComplete = applicable
                    && missing.Count == 0
                    && redundant.Count == 0
                    && unexpected.Count == 0
                    && scope.UnresolvedTargets.Count == 0
                    && scope.UnresolvedRequirements.Count == 0,
                IsGoalComplete = applicable
                    && goalTargets.Count > 0
                    && expected.Count == 0
                    && redundant.Count == 0
                    && unexpected.Count == 0
                    && scope.UnresolvedTargets.Count == 0
                    && scope.UnresolvedRequirements.Count == 0,
                ScopeBasis = scope.ScopeBasis,
                GoalTargets = goalTargets.OrderByName(),
                SatisfiedTargets = satisfied.OrderByName(),
                AlreadySatisfiedTargets = alreadySatisfied.OrderByName(),
                ExpectedTargets = expected.OrderByName(),
                PlannedTargets = planned.OrderByName(),
                MissingTargets = missing,
                ReferenceTargets = scope.ReferenceTargets.OrderByName(),
                RedundantTargets = redundant,
                UnexpectedTargets = unexpected,
                UnresolvedTargets = scope.UnresolvedTargets.OrderByName(),
                UnresolvedRequirements =
                    scope.UnresolvedRequirements.OrderByName()
            };
        }

        private static IReadOnlyList<LayerInfo> ResolveGoalCandidates(
            ResolvedGoalScope scope,
            DomainContext context)
        {
            IEnumerable<LayerInfo> candidates;
            if (scope.ExplicitTargets.Count > 0)
            {
                candidates = context.Layers.Where(layer =>
                    scope.ExplicitTargets.Contains(
                        layer.Name,
                        StringComparer.OrdinalIgnoreCase));
            }
            else if (scope.DomainCategories.Count > 0)
            {
                candidates = context.Layers.Where(layer =>
                    scope.DomainCategories.Contains(
                        NormalizeGeometry(layer.GeometryType),
                        StringComparer.OrdinalIgnoreCase));
            }
            else
            {
                candidates = Array.Empty<LayerInfo>();
            }

            return candidates.ToList();
        }

        private static bool IsAlreadyInDesiredState(
            LayerInfo layer,
            ResolvedGoalScope scope)
        {
            string category = NormalizeGeometry(layer.GeometryType);
            var requirements = scope.Requirements
                .Where(requirement =>
                    requirement.GroundingStatus ==
                        GoalRequirementGroundingStatus.Grounded
                    && requirement.DomainCategory.Equals(
                        category,
                        StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (requirements.Count == 0)
            {
                requirements = scope.FieldPredicates
                    .Where(predicate => predicate.DomainCategory.Equals(
                        category,
                        StringComparison.OrdinalIgnoreCase))
                    .Select(predicate => new GoalRequirement
                    {
                        DomainCategory = predicate.DomainCategory,
                        BoundFields = predicate.Fields.ToList(),
                        DesiredState = predicate.DesiredState,
                        GroundingStatus =
                            GoalRequirementGroundingStatus.Grounded
                    })
                    .ToList();
            }
            if (requirements.Count == 0)
                return false;
            var absent = requirements
                .Where(requirement =>
                    requirement.DesiredState ==
                        TargetStateExpectation.Absent
                    && requirement.BoundFields.Count > 0)
                .ToList();
            if (absent.Count == 0)
                return false;
            return absent.All(requirement =>
                requirement.BoundFields.All(field =>
                    !layer.Fields.Contains(
                        field,
                        StringComparer.OrdinalIgnoreCase)));
        }

        private static HashSet<string> ResolvePlannedTargets(
            AgentPlan plan,
            IReadOnlyList<string> knownTargets,
            bool writesOnly)
        {
            var planned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var step in plan.Steps)
            {
                if (writesOnly && !IsWriteStep(step))
                    continue;
                AddIfKnown(step.Effect?.Target.Layer, knownTargets, planned);
                AddIfKnown(step.Effect?.Target.Dataset, knownTargets, planned);
                foreach (string target in writesOnly ? step.WriteSet : step.ReadSet)
                    AddIfKnown(target, knownTargets, planned);
            }
            return planned;
        }

        private static IReadOnlyList<LayerInfo> CollapseLineage(
            IEnumerable<LayerInfo> layers) =>
            layers
                .GroupBy(
                    layer => string.IsNullOrWhiteSpace(layer.LineageRoot)
                        ? layer.Name
                        : layer.LineageRoot,
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(layer => layer.LineageDepth)
                    .ThenBy(layer => layer.Name, StringComparer.OrdinalIgnoreCase)
                    .First())
                .ToList();

        private static List<string> FindRedundantLineageTargets(
            IReadOnlyList<LayerInfo> layers,
            IReadOnlySet<string> planned,
            IReadOnlyCollection<string> preferred,
            IEnumerable<string> explicitlyRequested)
        {
            var explicitSet = explicitlyRequested.ToHashSet(
                StringComparer.OrdinalIgnoreCase);
            var preferredSet = preferred.ToHashSet(
                StringComparer.OrdinalIgnoreCase);
            var byName = layers.ToDictionary(
                layer => layer.Name,
                StringComparer.OrdinalIgnoreCase);
            var preferredRoots = preferred
                .Where(byName.ContainsKey)
                .Select(name => LineageKey(byName[name]))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return planned
                .Where(byName.ContainsKey)
                .Where(name => preferredRoots.Contains(LineageKey(byName[name])))
                .Where(name => !preferredSet.Contains(name))
                .Where(name => !explicitSet.Contains(name))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string LineageKey(LayerInfo layer) =>
            string.IsNullOrWhiteSpace(layer.LineageRoot)
                ? layer.Name
                : layer.LineageRoot;

        private static bool IsWriteStep(PlanStep step)
        {
            if (step.WriteSet.Count > 0)
                return true;
            if (step.Effect == null)
                return false;
            var effects = step.Effect.Profile.Effects;
            if (effects == DataEffectKind.ReadData)
                return false;
            return effects != DataEffectKind.None
                || !string.IsNullOrWhiteSpace(step.Effect.Target.Layer)
                || !string.IsNullOrWhiteSpace(step.Effect.Target.Dataset);
        }

        private static bool IsNewOutputOnlyStep(PlanStep step)
        {
            InvocationEffect? effect = step.Effect;
            if (effect == null
                || effect.Scope != EffectTargetScope.OutputOnly
                || effect.OutputExists)
            {
                return false;
            }

            DataEffectKind effects = effect.Profile.Effects;
            const DataEffectKind existingStateMutation =
                DataEffectKind.AppendRows
                | DataEffectKind.UpdateRows
                | DataEffectKind.DeleteRows
                | DataEffectKind.AlterSchema
                | DataEffectKind.DeleteDataset
                | DataEffectKind.ModifyGeometry
                | DataEffectKind.ModifySpatialReference
                | DataEffectKind.OverwriteOutput;
            bool createsContainedOutput = effects.HasFlag(
                    DataEffectKind.CreateDataset)
                || effects.HasFlag(DataEffectKind.ExternalPublish);
            return createsContainedOutput
                && (effects & existingStateMutation) == DataEffectKind.None;
        }

        private static void AddIfKnown(
            string? candidate,
            IReadOnlyList<string> knownTargets,
            ISet<string> result)
        {
            string? match = MatchKnownTarget(candidate, knownTargets);
            if (match != null)
                result.Add(match);
        }

        private static string? MatchKnownTarget(
            string? candidate,
            IReadOnlyList<string> knownTargets)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                return null;
            string normalized = NormalizeTarget(candidate);
            return knownTargets.FirstOrDefault(known =>
                NormalizeTarget(known).Equals(
                    normalized,
                    StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith(
                    "\\" + NormalizeTarget(known),
                    StringComparison.OrdinalIgnoreCase));
        }

        private static string NormalizeGeometry(string value) =>
            GisCategoryNormalizer.NormalizeGeometry(value);

        private static string NormalizeTarget(string value) =>
            Regex.Replace(
                value.Trim().Replace('/', '\\').TrimEnd('\\'),
                @"\s+",
                " ");
    }

    internal static class GoalCoverageOrdering
    {
        public static List<string> OrderByName(this IEnumerable<string> source) =>
            source
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                .ToList();
    }
}
