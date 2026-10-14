using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Core
{
    /// <summary>
    /// Deterministic scope expansion. Default product policy is Expand when a
    /// unique expansion rule applies. No tool names and no physical plans.
    /// </summary>
    /// <remarks>中文：确定性范围扩展。默认产品策略是当唯一扩展规则适用时，为“扩展”。没有工具名称，也没有实体设计图。</remarks>
    public static class DefaultScopeExpander
    {
        private static readonly HashSet<string> AlignToReferenceCapabilities =
            new(StringComparer.OrdinalIgnoreCase)
            {
                GisCapabilityIds.TransformCrs
            };

        public static void ExpandInPlace(ResolvedGoalScope scope, DomainContext context)
        {
            if (scope.UnresolvedTargets.Count > 0
                && scope.Quantifier == GoalTargetQuantifier.Explicit)
            {
                scope.ScopeSufficiency =
                    GoalScopeSufficiency.ClarificationRequired;
                scope.ScopeExpansionRule = "explicit:unresolved";
                scope.ScopeBasis = "scope-unresolved:explicit";
                return;
            }

            if (scope.ExplicitTargets.Count > 0)
            {
                scope.ScopeSufficiency = GoalScopeSufficiency.Sufficient;
                if (string.IsNullOrWhiteSpace(scope.ScopeExpansionRule))
                    scope.ScopeExpansionRule = "explicit";
                return;
            }

            if (scope.DomainCategories.Count > 0
                && scope.Quantifier is GoalTargetQuantifier.AllPhysicalTargets
                    or GoalTargetQuantifier.AllMatchingState
                    or GoalTargetQuantifier.RepresentativePerLineage)
            {
                var byCategory = context.Layers
                    .Where(layer => scope.DomainCategories.Contains(
                        NormalizeGeometry(layer.GeometryType),
                        StringComparer.OrdinalIgnoreCase))
                    .Select(layer => layer.Name)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (byCategory.Count > 0)
                {
                    // Product rule: when the user names a geometry class without
                    // naming a specific layer, the set is "all members of that
                    // class" — raw + projected copies both count if both exist.
                    // That is not over-expansion; it is the default universal set.
                    // Collapse to lineage/name-family representatives only when
                    // the user (or quantifier) explicitly prefers a representative.
                    // 中文：乘积规则：当用户为几何类命名但未命名特定图层时，集合为“该类的所有成员”——如果两者都存在，原始+投影副本均计入。这不是过度扩张;它是默认的通用集。只有当使用者（或量词）明确偏好某个代表时，才会崩溃为血统/名称家族代表。
                    bool preferRepresentative =
                        scope.Quantifier ==
                            GoalTargetQuantifier.RepresentativePerLineage
                        || HasLineagePreferenceLanguage(scope.SourceGoal);
                    List<string> chosen = preferRepresentative
                        ? CollapseToOperableRepresentatives(
                            context.Layers.Where(layer => byCategory.Contains(
                                layer.Name,
                                StringComparer.OrdinalIgnoreCase)))
                        : byCategory;
                    scope.ExplicitTargets = chosen;
                    if (preferRepresentative)
                        scope.Quantifier =
                            GoalTargetQuantifier.RepresentativePerLineage;
                    else if (scope.Quantifier == GoalTargetQuantifier.Unspecified)
                        scope.Quantifier = GoalTargetQuantifier.AllPhysicalTargets;
                    scope.ScopeSufficiency = GoalScopeSufficiency.Sufficient;
                    scope.ScopeExpansionRule = preferRepresentative
                        ? "by_category:representative"
                        : "by_category:all_physical";
                    scope.ScopeBasis = preferRepresentative
                        ? "expanded:by_category:representative"
                        : "expanded:by_category:all_physical";
                    scope.ResourceHandles = RebuildHandles(
                        context,
                        scope.ExplicitTargets,
                        scope.ReferenceTargets);
                    return;
                }
            }

            bool hasAlignCapability = scope.RequiredCapabilities.Any(capability =>
                AlignToReferenceCapabilities.Contains(capability));
            if (hasAlignCapability && scope.ReferenceTargets.Count > 0)
            {
                var refs = scope.ReferenceTargets
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var expanded = context.Layers
                    .Select(layer => layer.Name)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Where(name => !refs.Contains(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (expanded.Count > 0)
                {
                    scope.ExplicitTargets = expanded;
                    scope.Quantifier = GoalTargetQuantifier.AllPhysicalTargets;
                    scope.ScopeSufficiency = GoalScopeSufficiency.Sufficient;
                    scope.ScopeExpansionRule = "except_reference";
                    scope.ScopeBasis = "expanded:except_reference";
                    scope.ResourceHandles = RebuildHandles(
                        context,
                        scope.ExplicitTargets,
                        scope.ReferenceTargets);
                    return;
                }
            }

            // Expansion rules that were applicable but yielded zero members are
            // insufficient. Free-form mutating capabilities without a matched
            // expansion rule stay NotRequired. The coverage/admissibility
            // boundary may authorize a genuinely new OutputOnly artifact, but
            // cannot authorize mutation of existing state without a target.
            // 中文：适用但成员数为零的扩展规则仍不充分。没有匹配扩展规则的自由变更能力依然不是必需项。覆盖范围/可采纳性边界可以授权真正的新 OutputOnly 产物，但不能授权在无目标时修改现有状态。
            bool alignAttempted = hasAlignCapability && scope.ReferenceTargets.Count > 0;
            bool categoryAttempted = scope.DomainCategories.Count > 0
                && scope.Quantifier is GoalTargetQuantifier.AllPhysicalTargets
                    or GoalTargetQuantifier.AllMatchingState
                    or GoalTargetQuantifier.RepresentativePerLineage;
            if (alignAttempted || categoryAttempted)
            {
                scope.ScopeSufficiency = GoalScopeSufficiency.Insufficient;
                scope.ScopeExpansionRule = alignAttempted
                    ? "except_reference:empty"
                    : "by_category:empty";
                scope.ScopeBasis = "scope-insufficient";
                return;
            }

            scope.ScopeSufficiency = GoalScopeSufficiency.NotRequired;
            if (string.IsNullOrWhiteSpace(scope.ScopeExpansionRule))
                scope.ScopeExpansionRule = "none";
        }

        /// <summary>
        /// Collapse physical candidates to operable representatives so safety
        /// contracts stay tight without starving the agent of executable work.
        /// </summary>
        /// <remarks>中文：将实体候选人整合为可操作的代表，以确保安全合同紧密，同时不使智能体缺乏可执行的工作。</remarks>
        internal static List<string> CollapseToOperableRepresentatives(
            IEnumerable<LayerInfo> layers)
        {
            var list = layers
                .Where(layer => !string.IsNullOrWhiteSpace(layer.Name))
                .ToList();
            if (list.Count == 0)
                return new List<string>();

            // 1) Prefer lineage depth when the host provides roots.
            // 中文：1）当宿主提供根节点时，优先使用谱系深度。
            var withLineage = list
                .Where(layer => !string.IsNullOrWhiteSpace(layer.LineageRoot))
                .GroupBy(
                    layer => layer.LineageRoot,
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(layer => layer.LineageDepth)
                    .ThenByDescending(layer => layer.Name.Length)
                    .First())
                .ToList();
            var lineageChosen = withLineage
                .Select(layer => layer.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var lineageRoots = withLineage
                .Select(layer => layer.LineageRoot)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // 2) Name-family collapse for layers without lineage
            //    (e.g. roads vs roads_4490 after Project).
            // 中文：2）无谱系层（例如项目后的道路与roads_4490层）名称族崩溃。
            var noLineage = list
                .Where(layer => string.IsNullOrWhiteSpace(layer.LineageRoot)
                    || !lineageRoots.Contains(layer.LineageRoot))
                .Where(layer => !lineageChosen.Contains(layer.Name))
                .ToList();
            var byFamily = noLineage
                .GroupBy(layer => NameFamilyKey(layer.Name), StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(ScoreDerivedPreference)
                    .ThenByDescending(layer => layer.Name.Length)
                    .ThenBy(layer => layer.Name, StringComparer.OrdinalIgnoreCase)
                    .First().Name);

            return lineageChosen
                .Concat(byFamily)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool HasLineagePreferenceLanguage(string? goal) =>
            GisCategoryNormalizer.HasLineagePreferenceLanguage(goal);

        private static string NameFamilyKey(string name)
        {
            string value = name.Trim();
            // Strip common CRS / projection suffixes produced by agent Project.
            // 中文：去除由Project代理产生的常见CRS / 投影后缀。
            value = System.Text.RegularExpressions.Regex.Replace(
                value,
                @"_(?:4490|4326|3857|4547|proj|project|projected)$",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return value;
        }

        private static int ScoreDerivedPreference(LayerInfo layer)
        {
            int score = layer.LineageDepth * 10;
            string name = layer.Name ?? "";
            if (System.Text.RegularExpressions.Regex.IsMatch(
                    name,
                    @"_(?:4490|4326|3857|4547|proj|project|projected)$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                score += 5;
            return score;
        }

        private static List<GoalResourceHandle> RebuildHandles(
            DomainContext context,
            IEnumerable<string> targets,
            IEnumerable<string> references)
        {
            var names = targets
                .Concat(references)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var handles = new List<GoalResourceHandle>();
            foreach (string name in names)
            {
                var layer = context.Layers.FirstOrDefault(item =>
                    item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (layer == null)
                    continue;
                handles.Add(new GoalResourceHandle
                {
                    DisplayName = layer.Name,
                    MapMemberId = layer.MapMemberId ?? "",
                    DatasetId = layer.DatasetId ?? "",
                    SchemaFingerprint = layer.SchemaFingerprint ?? "",
                    StateFingerprint = layer.StateFingerprint ?? "",
                    StateFingerprintReliable = layer.StateFingerprintReliable,
                    LineageRoot = layer.LineageRoot ?? "",
                    LineageDepth = layer.LineageDepth
                });
            }
            return handles;
        }

        private static string NormalizeGeometry(string? geometryType) =>
            GisCategoryNormalizer.NormalizeGeometry(geometryType);
    }
}
