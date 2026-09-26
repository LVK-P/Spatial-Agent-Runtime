using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Core
{
    public sealed class DefaultTurnRelationResolver : ITurnRelationResolver
    {
        private static readonly string[] CancelTerms =
        {
            "取消", "停止", "算了", "不要继续", "cancel", "stop", "never mind"
        };

        private static readonly string[] CorrectionTerms =
        {
            "不是", "纠正", "更正", "说错", "应该是", "correction", "correct that", "not "
        };

        private static readonly string[] RefinementTerms =
        {
            "我的意思", "改成", "改为", "调整为", "重新", "换成", "细化",
            "i mean", "change to", "update to", "instead", "refine"
        };

        private static readonly string[] ContinueTerms =
        {
            "继续", "接着", "剩余", "其余", "补齐", "也要", "也需要", "下一批",
            "continue", "remaining", "rest of", "also ", "next batch"
        };

        public GoalTurnRelation Resolve(
            string userMessage,
            ResolvedGoalScope? previousGoal,
            CompiledGoalContract? patch = null)
        {
            if (previousGoal == null)
                return GoalTurnRelation.New;
            string source = $" {userMessage?.Trim().ToLowerInvariant()} ";
            if (ContainsAny(source, CancelTerms))
                return GoalTurnRelation.Cancel;
            if (ContainsAny(source, CorrectionTerms))
                return GoalTurnRelation.Correct;
            if (ContainsAny(source, RefinementTerms))
                return GoalTurnRelation.Refine;
            if (ContainsAny(source, ContinueTerms))
                return GoalTurnRelation.Continue;

            // A turn that only changes typed fulfillment parameters is a goal
            // patch even when the user naturally omits words such as “change”.
            // This is structural comparison, not an operation-specific phrase
            // exception: any domain pack can contribute parameter constraints.
            if (patch?.ParameterConstraints.Count > 0
                && !patch.ResourceBindings.Any(binding =>
                    binding.Role == GoalResourceRole.Target
                    && binding.Status is GoalResourceResolutionStatus.Missing
                        or GoalResourceResolutionStatus.Ambiguous)
                && patch.ResourceBindings
                    .Where(binding =>
                        binding.Role == GoalResourceRole.Target
                        && binding.Status == GoalResourceResolutionStatus.Resolved)
                    .SelectMany(binding => binding.ResolvedTargets)
                    .All(target => previousGoal.ExplicitTargets.Contains(
                        target,
                        StringComparer.OrdinalIgnoreCase))
                && (patch.RequiredCapabilities.Count == 0
                    || patch.RequiredCapabilities.All(capability =>
                        previousGoal.RequiredCapabilities.Contains(
                            capability,
                            StringComparer.OrdinalIgnoreCase))))
            {
                return GoalTurnRelation.Refine;
            }
            return GoalTurnRelation.New;
        }

        private static bool ContainsAny(string source, IEnumerable<string> terms) =>
            terms.Any(term =>
                source.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Owns goal identity and conversational inheritance. The resolver consumes
    /// only the user contract and the host catalog; a proposed physical plan
    /// cannot influence or commit semantic state.
    /// </summary>
    public sealed class DefaultGoalScopeResolver : IGoalScopeResolver
    {
        private readonly IGoalContractCompiler _compiler;
        private readonly ITurnRelationResolver _turnRelations;

        public DefaultGoalScopeResolver(
            IGoalContractCompiler? compiler = null,
            ITurnRelationResolver? turnRelations = null)
        {
            _compiler = compiler ?? new DefaultGoalContractCompiler();
            _turnRelations = turnRelations ?? new DefaultTurnRelationResolver();
        }

        public ResolvedGoalScope Resolve(
            string goal,
            ResolvedGoalScope? previousGoal,
            DomainContext context)
        {
            CompiledGoalContract compiled = _compiler.Compile(goal, context);
            GoalTurnRelation relation = _turnRelations.Resolve(
                goal,
                previousGoal,
                compiled);
            bool inherits = previousGoal != null
                && relation is GoalTurnRelation.Continue
                    or GoalTurnRelation.Refine
                    or GoalTurnRelation.Correct;
            var reconciled = inherits
                ? Reconcile(previousGoal!, context)
                : ReconciledGoal.Empty;

            var categories = compiled.DomainCategories.ToList();
            var requirements = CloneRequirements(compiled.Requirements);
            var parameterConstraints = CloneParameterConstraints(
                compiled.ParameterConstraints);
            var predicates = ClonePredicates(compiled.FieldPredicates);
            var capabilities = compiled.RequiredCapabilities.ToList();
            var deliverables = CloneDeliverables(compiled.Deliverables);
            var proofs = CloneProofs(compiled.ProofObligations);
            var explicitTargets = ResolvedTargets(
                compiled,
                GoalResourceRole.Target);
            var referenceTargets = ResolvedTargets(
                compiled,
                GoalResourceRole.Reference);
            GoalTargetQuantifier quantifier = compiled.Quantifier;

            if (inherits)
            {
                if (categories.Count == 0)
                    categories.AddRange(previousGoal!.DomainCategories);
                if (predicates.Count == 0)
                    predicates.AddRange(ClonePredicates(previousGoal!.FieldPredicates));
                if (requirements.Count == 0)
                    requirements.AddRange(CloneRequirements(
                        previousGoal!.Requirements));
                parameterConstraints = MergeParameterConstraints(
                    previousGoal!.ParameterConstraints,
                    parameterConstraints);
                if (capabilities.Count == 0)
                    capabilities.AddRange(previousGoal!.RequiredCapabilities);
                if (deliverables.Count == 0)
                    deliverables.AddRange(CloneDeliverables(previousGoal!.Deliverables));
                if (proofs.Count == 0)
                    proofs.AddRange(CloneProofs(previousGoal!.ProofObligations));
                if (explicitTargets.Count == 0
                    && compiled.DomainCategories.Count == 0
                    && compiled.Quantifier == GoalTargetQuantifier.Unspecified)
                    explicitTargets.AddRange(reconciled.ExplicitTargets);
                if (referenceTargets.Count == 0)
                    referenceTargets.AddRange(reconciled.ReferenceTargets);
                if (quantifier == GoalTargetQuantifier.Unspecified)
                    quantifier = previousGoal!.Quantifier;
            }

            categories = Distinct(categories);
            capabilities = Distinct(capabilities);
            explicitTargets = Distinct(explicitTargets);
            referenceTargets = Distinct(referenceTargets);

            var unresolved = compiled.ResourceBindings
                .Where(binding => binding.Status is
                    GoalResourceResolutionStatus.Missing
                    or GoalResourceResolutionStatus.Ambiguous)
                .Select(binding => binding.Mention)
                .Concat(inherits ? reconciled.MissingResources : Array.Empty<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var unresolvedRequirements = requirements
                .Where(requirement =>
                    requirement.GroundingStatus ==
                        GoalRequirementGroundingStatus.Unresolved)
                .Select(requirement => requirement.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                .ToList();

            bool canCarryEvidence = relation == GoalTurnRelation.Continue
                && previousGoal != null
                && SameSemanticWork(
                    previousGoal,
                    capabilities,
                    predicates,
                    requirements);
            var satisfiedTargets = canCarryEvidence
                ? Distinct(reconciled.SatisfiedTargets)
                : new List<string>();
            var satisfiedProofs = canCarryEvidence
                ? previousGoal!.SatisfiedProofObligations
                    .Where(proof => proofs.Any(current =>
                        current.Id.Equals(proof, StringComparison.OrdinalIgnoreCase)))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : new List<string>();

            string goalId = inherits
                ? previousGoal!.GoalId
                : Guid.NewGuid().ToString("N");
            int version = inherits ? previousGoal!.Version + 1 : 1;
            var scope = new ResolvedGoalScope
            {
                GoalId = goalId,
                Version = version,
                ParentGoalId = inherits ? previousGoal!.GoalId : "",
                TurnRelation = relation,
                SourceGoal = compiled.SourceGoal,
                ScopeBasis = BuildScopeBasis(
                    relation,
                    quantifier,
                    explicitTargets,
                    categories),
                Quantifier = quantifier,
                InheritedConstraints = inherits,
                DomainCategories = categories,
                ExplicitTargets = explicitTargets,
                ReferenceTargets = referenceTargets,
                UnresolvedTargets = unresolved,
                SatisfiedTargets = satisfiedTargets,
                SatisfiedProofObligations = satisfiedProofs,
                Requirements = requirements,
                ParameterConstraints = parameterConstraints,
                UnresolvedRequirements = unresolvedRequirements,
                FieldPredicates = predicates,
                RequiredCapabilities = capabilities,
                Deliverables = deliverables,
                ProofObligations = proofs,
                ResourceBindings = compiled.ResourceBindings,
                ResourceHandles = BuildResourceHandles(
                    context,
                    explicitTargets,
                    referenceTargets,
                    categories)
            };
            // Default product policy: Expand when a unique rule applies.
            DefaultScopeExpander.ExpandInPlace(scope, context);
            scope.ContractFingerprint = ComputeFingerprint(scope);
            return scope;
        }

        private static ReconciledGoal Reconcile(
            ResolvedGoalScope previous,
            DomainContext context)
        {
            if (previous.ResourceHandles.Count == 0)
            {
                return new ReconciledGoal
                {
                    ExplicitTargets = RebindNames(previous.ExplicitTargets, context),
                    ReferenceTargets = RebindNames(previous.ReferenceTargets, context),
                    SatisfiedTargets = RebindNames(previous.SatisfiedTargets, context)
                };
            }

            var names = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            var unchanged = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            var missing = new List<string>();
            foreach (var old in previous.ResourceHandles)
            {
                var current = context.Layers.FirstOrDefault(layer =>
                    !string.IsNullOrWhiteSpace(old.MapMemberId)
                    && layer.MapMemberId.Equals(
                        old.MapMemberId,
                        StringComparison.OrdinalIgnoreCase))
                    ?? context.Layers.FirstOrDefault(layer =>
                        !string.IsNullOrWhiteSpace(old.DatasetId)
                        && layer.DatasetId.Equals(
                            old.DatasetId,
                            StringComparison.OrdinalIgnoreCase))
                    ?? context.Layers.FirstOrDefault(layer =>
                        string.IsNullOrWhiteSpace(old.MapMemberId)
                        && string.IsNullOrWhiteSpace(old.DatasetId)
                        && layer.Name.Equals(
                            old.DisplayName,
                            StringComparison.OrdinalIgnoreCase));
                if (current == null)
                {
                    missing.Add(old.DisplayName);
                    continue;
                }

                names[old.DisplayName] = current.Name;
                bool schemaOnlyGoal = previous.RequiredCapabilities.Count > 0
                    && previous.RequiredCapabilities.All(capability =>
                        capability.Equals(
                            GisCapabilityIds.AlterFieldSchema,
                            StringComparison.OrdinalIgnoreCase));
                bool sameRelevantRevision = schemaOnlyGoal
                    && !string.IsNullOrWhiteSpace(old.SchemaFingerprint)
                    ? old.SchemaFingerprint.Equals(
                        current.SchemaFingerprint,
                        StringComparison.OrdinalIgnoreCase)
                    : old.StateFingerprint.Equals(
                        current.StateFingerprint,
                        StringComparison.OrdinalIgnoreCase);
                if (string.IsNullOrWhiteSpace(old.StateFingerprint)
                    || (old.StateFingerprintReliable
                        && current.StateFingerprintReliable
                        && sameRelevantRevision))
                    unchanged.Add(old.DisplayName);
            }

            List<string> Rebind(
                IEnumerable<string> source,
                bool requireUnchanged = false) =>
                source
                    .Where(name => !requireUnchanged || unchanged.Contains(name))
                    .Select(name => names.GetValueOrDefault(name, ""))
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

            return new ReconciledGoal
            {
                ExplicitTargets = Rebind(previous.ExplicitTargets),
                ReferenceTargets = Rebind(previous.ReferenceTargets),
                SatisfiedTargets = Rebind(
                    previous.SatisfiedTargets,
                    requireUnchanged: true),
                MissingResources = missing
            };
        }

        private static List<string> RebindNames(
            IEnumerable<string> names,
            DomainContext context) =>
            names
                .Select(name => context.Layers.FirstOrDefault(layer =>
                    layer.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        private static List<GoalResourceHandle> BuildResourceHandles(
            DomainContext context,
            IReadOnlyCollection<string> explicitTargets,
            IReadOnlyCollection<string> referenceTargets,
            IReadOnlyCollection<string> categories)
        {
            return context.Layers
                .Where(layer =>
                    explicitTargets.Contains(layer.Name, StringComparer.OrdinalIgnoreCase)
                    || referenceTargets.Contains(layer.Name, StringComparer.OrdinalIgnoreCase)
                    || categories.Contains(
                        NormalizeGeometry(layer.GeometryType),
                        StringComparer.OrdinalIgnoreCase))
                .Select(layer => new GoalResourceHandle
                {
                    DisplayName = layer.Name,
                    MapMemberId = layer.MapMemberId,
                    DatasetId = layer.DatasetId,
                    SchemaFingerprint = layer.SchemaFingerprint,
                    StateFingerprint = layer.StateFingerprint,
                    StateFingerprintReliable = layer.StateFingerprintReliable,
                    LineageRoot = layer.LineageRoot,
                    LineageDepth = layer.LineageDepth
                })
                .ToList();
        }

        private static bool SameSemanticWork(
            ResolvedGoalScope previous,
            IReadOnlyCollection<string> capabilities,
            IReadOnlyCollection<GoalFieldPredicate> predicates,
            IReadOnlyCollection<GoalRequirement> requirements)
        {
            if (!previous.RequiredCapabilities.ToHashSet(StringComparer.OrdinalIgnoreCase)
                    .SetEquals(capabilities))
                return false;
            string Normalize(GoalFieldPredicate predicate) =>
                $"{predicate.DomainCategory}:{predicate.DesiredState}:"
                + string.Join(
                    ",",
                    predicate.Fields.OrderBy(
                        item => item,
                        StringComparer.OrdinalIgnoreCase));
            bool samePredicates = previous.FieldPredicates
                .Select(Normalize)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(predicates.Select(Normalize));
            string NormalizeRequirement(GoalRequirement requirement) =>
                $"{requirement.OperationFamily}:"
                + $"{requirement.DomainCategory}:"
                + $"{requirement.ArtifactSemanticType}:"
                + $"{requirement.DesiredState}:"
                + string.Join(
                    ",",
                    requirement.BoundFields.OrderBy(
                        item => item,
                        StringComparer.OrdinalIgnoreCase));
            return samePredicates
                && previous.Requirements
                    .Select(NormalizeRequirement)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                    .SetEquals(requirements.Select(
                        NormalizeRequirement));
        }

        private static List<GoalRequirement> CloneRequirements(
            IEnumerable<GoalRequirement> source) =>
            source.Select(item => new GoalRequirement
            {
                Id = item.Id,
                OperationFamily = item.OperationFamily,
                DomainCategory = item.DomainCategory,
                ArtifactMention = item.ArtifactMention,
                ArtifactSemanticType = item.ArtifactSemanticType,
                BoundFields = item.BoundFields.ToList(),
                DesiredState = item.DesiredState,
                RequiredCapability = item.RequiredCapability,
                GroundingStatus = item.GroundingStatus,
                GroundingEvidence = item.GroundingEvidence
            }).ToList();

        private static List<GoalParameterConstraint> CloneParameterConstraints(
            IEnumerable<GoalParameterConstraint> source) =>
            source.Select(item => new GoalParameterConstraint
            {
                Key = item.Key,
                Value = item.Value,
                AppliesToCapability = item.AppliesToCapability,
                SourceText = item.SourceText
            }).ToList();

        private static List<GoalParameterConstraint> MergeParameterConstraints(
            IEnumerable<GoalParameterConstraint> inherited,
            IEnumerable<GoalParameterConstraint> patch)
        {
            var merged = CloneParameterConstraints(inherited);
            foreach (GoalParameterConstraint item in patch)
            {
                merged.RemoveAll(existing =>
                    existing.Key.Equals(item.Key, StringComparison.OrdinalIgnoreCase)
                    && existing.AppliesToCapability.Equals(
                        item.AppliesToCapability,
                        StringComparison.OrdinalIgnoreCase));
                merged.Add(new GoalParameterConstraint
                {
                    Key = item.Key,
                    Value = item.Value,
                    AppliesToCapability = item.AppliesToCapability,
                    SourceText = item.SourceText
                });
            }
            return merged;
        }

        internal static string ComputeFingerprint(ResolvedGoalScope scope)
        {
            var canonical = new
            {
                quantifier = scope.Quantifier.ToString(),
                categories = scope.DomainCategories
                    .OrderBy(item => item, StringComparer.OrdinalIgnoreCase),
                targets = scope.ExplicitTargets
                    .OrderBy(item => item, StringComparer.OrdinalIgnoreCase),
                references = scope.ReferenceTargets
                    .OrderBy(item => item, StringComparer.OrdinalIgnoreCase),
                capabilities = scope.RequiredCapabilities
                    .OrderBy(item => item, StringComparer.OrdinalIgnoreCase),
                parameters = scope.ParameterConstraints
                    .Select(item =>
                        $"{item.AppliesToCapability}:{item.Key}:{item.Value}")
                    .OrderBy(item => item, StringComparer.OrdinalIgnoreCase),
                deliverables = scope.Deliverables
                    .Select(item => $"{item.Id}:{item.CapabilityId}")
                    .OrderBy(item => item, StringComparer.OrdinalIgnoreCase),
                requirements = scope.Requirements
                    .Select(item =>
                        $"{item.Id}:{item.OperationFamily}:"
                        + $"{item.DomainCategory}:{item.ArtifactSemanticType}:"
                        + $"{item.DesiredState}:"
                        + string.Join(
                            ",",
                            item.BoundFields.OrderBy(
                                field => field,
                                StringComparer.OrdinalIgnoreCase)))
                    .OrderBy(item => item, StringComparer.OrdinalIgnoreCase),
                predicates = scope.FieldPredicates
                    .Select(item =>
                        $"{item.DomainCategory}:{item.DesiredState}:"
                        + string.Join(
                            ",",
                            item.Fields.OrderBy(
                                field => field,
                                StringComparer.OrdinalIgnoreCase)))
                    .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            };
            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical));
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }

        private static List<string> ResolvedTargets(
            CompiledGoalContract contract,
            GoalResourceRole role) =>
            contract.ResourceBindings
                .Where(binding =>
                    binding.Status == GoalResourceResolutionStatus.Resolved
                    && binding.Role == role)
                .SelectMany(binding => binding.ResolvedTargets)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        private static string BuildScopeBasis(
            GoalTurnRelation relation,
            GoalTargetQuantifier quantifier,
            IReadOnlyCollection<string> targets,
            IReadOnlyCollection<string> categories)
        {
            string basis = targets.Count > 0
                ? "semantic-explicit-targets"
                : categories.Count > 0
                    ? $"domain-categories:{string.Join(",", categories.Order())}"
                    : quantifier == GoalTargetQuantifier.Unspecified
                        ? "not-set-valued"
                        : quantifier.ToString();
            return relation == GoalTurnRelation.New
                ? basis
                : $"{relation.ToString().ToLowerInvariant()}:{basis}";
        }

        private static List<GoalFieldPredicate> ClonePredicates(
            IEnumerable<GoalFieldPredicate> source) =>
            source.Select(item => new GoalFieldPredicate
            {
                DomainCategory = item.DomainCategory,
                DesiredState = item.DesiredState,
                Fields = item.Fields.ToList()
            }).ToList();

        private static List<GoalDeliverable> CloneDeliverables(
            IEnumerable<GoalDeliverable> source) =>
            source.Select(item => new GoalDeliverable
            {
                Id = item.Id,
                Kind = item.Kind,
                Description = item.Description,
                CapabilityId = item.CapabilityId,
                Required = item.Required
            }).ToList();

        private static List<GoalProofObligation> CloneProofs(
            IEnumerable<GoalProofObligation> source) =>
            source.Select(item => new GoalProofObligation
            {
                Id = item.Id,
                Kind = item.Kind,
                CapabilityId = item.CapabilityId,
                Description = item.Description,
                RequiresValidation = item.RequiresValidation,
                Required = item.Required,
                Expected = new Dictionary<string, string>(
                    item.Expected,
                    StringComparer.OrdinalIgnoreCase)
            }).ToList();

        private static List<string> Distinct(IEnumerable<string> source) =>
            source
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        private static string NormalizeGeometry(string value) =>
            GisCategoryNormalizer.NormalizeGeometry(value);

        private sealed class ReconciledGoal
        {
            public static ReconciledGoal Empty { get; } = new();
            public List<string> ExplicitTargets { get; init; } = new();
            public List<string> ReferenceTargets { get; init; } = new();
            public List<string> SatisfiedTargets { get; init; } = new();
            public List<string> MissingResources { get; init; } = new();
        }
    }
}
