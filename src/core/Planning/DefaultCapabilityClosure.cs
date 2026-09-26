using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Core
{
    /// <summary>
    /// Maps ArcGIS tools to stable logical capabilities. Tool names live only
    /// at this adapter boundary; goal and completion layers consume ids.
    ///
    /// Also provides the reverse check: when a capability is missing from the
    /// declared set, PlausiblyProvidesCapability checks whether a tool's identity
    /// (name + description) suggests it COULD provide the capability even though
    /// the catalog's keyword heuristics missed it. This prevents architecture
    /// starvation — a catalog mapping gap must not hard-block a correct tool plan.
    /// </summary>
    public sealed class DefaultCapabilityCatalog : ICapabilityCatalog
    {
        public IReadOnlyList<string> ResolveProvidedCapabilities(
            IToolDescriptor? descriptor,
            ToolCall call,
            InvocationEffect? effect)
        {
            string identity = BuildIdentity(call.Name, descriptor);
            var capabilities = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

            if (descriptor is IToolCapabilityDescriptor declared)
            {
                foreach (ToolCapabilityClaim claim in declared.CapabilityClaims)
                {
                    if (!string.IsNullOrWhiteSpace(claim.CapabilityId))
                        capabilities.Add(claim.CapabilityId);
                }
            }

            if (ContainsAny(identity, new[]
                {
                    "summarizewithin", "summarize nearby", "summarizenearby",
                    "spatialjoin", "spatial join", "按范围汇总", "邻近汇总"
                }))
                capabilities.Add(GisCapabilityIds.SpatialAggregateWithin);
            if (ContainsAny(identity, new[]
                {
                    "calculatearea", "calculate area", "cc_calculate_area",
                    "cc_calculatearea", "面积计算"
                }))
                capabilities.Add(GisCapabilityIds.MeasureArea);
            if (ContainsAny(identity, new[]
                {
                    "calculatelength", "calculate length", "cc_calculate_length",
                    "cc_calculatelength", "长度计算", "地线长度", "geodesic length"
                }))
                capabilities.Add(GisCapabilityIds.MeasureLength);
            if (ContainsAny(identity, new[]
                {
                    "calculatefield", "calculate field", "calculatefields",
                    "字段计算", "计算字段"
                }))
                capabilities.Add(GisCapabilityIds.ComputeMetric);
            if (ContainsAny(identity, new[]
                {
                    "reclassifyfield", "reclassify field", "slice",
                    "classbreak", "graduated", "natural breaks", "分级", "重分类"
                }))
                capabilities.Add(GisCapabilityIds.ClassifyNumeric);
            if (ContainsAny(identity, new[]
                {
                    "applysymbology", "apply symbology", "renderer",
                    "classbreak", "graduated", "symbol", "符号", "渲染"
                }))
                capabilities.Add(GisCapabilityIds.RenderMap);
            if (ContainsAny(identity, new[]
                {
                    "project", "投影", "define projection"
                }))
                capabilities.Add(GisCapabilityIds.TransformCrs);
            if (ContainsAny(identity, new[]
                {
                    "addfield", "deletefield", "alterfield", "字段管理",
                    "添加字段", "删除字段"
                })
                || effect?.Profile.Effects.HasFlag(DataEffectKind.AlterSchema) == true)
                capabilities.Add(GisCapabilityIds.AlterFieldSchema);
            if (effect?.Profile.Effects.HasFlag(DataEffectKind.CreateDataset) == true)
                capabilities.Add(GisCapabilityIds.CreateArtifact);
            if (effect?.Profile.Effects.HasFlag(DataEffectKind.ModifyMapState) == true)
                capabilities.Add(GisCapabilityIds.RenderMap);

            return capabilities.Order().ToList();
        }

        public IReadOnlyList<string> SelectToolNames(
            IEnumerable<string> requiredCapabilities,
            IEnumerable<IToolDescriptor> descriptors)
        {
            var required = requiredCapabilities.ToHashSet(
                StringComparer.OrdinalIgnoreCase);
            if (required.Count == 0)
                return Array.Empty<string>();

            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var descriptor in descriptors)
            {
                var call = new ToolCall { Name = descriptor.Name };
                var provided = ResolveProvidedCapabilities(
                    descriptor,
                    call,
                    effect: null);
                if (provided.Any(required.Contains))
                    result.Add(descriptor.Name);
            }
            return result.Order().ToList();
        }

        // ════════════════════════════════════════════════════════════════
        // Reverse mapping: capability → identity keywords for plausible check.
        // These are the SAME keyword families used in ResolveProvidedCapabilities,
        // plus broader generics for defense-in-depth. When a tool's identity hits
        // one of these but the catalog didn't declare the capability, the gap is
        // Soft (Warning) rather than Hard (Error).
        // ════════════════════════════════════════════════════════════════

        private static readonly IReadOnlyDictionary<string, string[]> PlausibleKeywords =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                [GisCapabilityIds.SpatialAggregateWithin] = new[]
                {
                    "summarize", "spatialjoin", "spatial join", "aggregate",
                    "汇总", "邻近", "范围内"
                },
                [GisCapabilityIds.MeasureArea] = new[]
                {
                    "area", "calculate_area",
                    "面积", "量算", "椭球"
                },
                [GisCapabilityIds.MeasureLength] = new[]
                {
                    "length", "calculate_length",
                    "长度", "里程", "地线", "geodesic"
                },
                [GisCapabilityIds.ComputeMetric] = new[]
                {
                    "calculate", "compute", "statistics",
                    "计算", "统计", "赋值", "字段计算", "添加字段"
                },
                [GisCapabilityIds.ClassifyNumeric] = new[]
                {
                    "classify", "reclass", "slice", "breaks", "graduated",
                    "分级", "分类", "等级", "重分类", "自然断点"
                },
                [GisCapabilityIds.RenderMap] = new[]
                {
                    "symbology", "renderer", "symbol", "color",
                    "符号", "渲染", "颜色", "色带", "渐变"
                },
                [GisCapabilityIds.TransformCrs] = new[]
                {
                    "project", "reproject", "coordinate", "spatial reference",
                    "投影", "坐标系", "空间参考"
                },
                [GisCapabilityIds.AlterFieldSchema] = new[]
                {
                    "addfield", "deletefield", "alterfield",
                    "字段管理", "添加字段", "删除字段", "alter"
                },
                [GisCapabilityIds.CreateArtifact] = new[]
                {
                    "create", "generate", "derive", "save as",
                    "创建", "生成", "派生", "新建", "输出"
                }
            };

        /// <summary>
        /// Check whether a tool's identity (name + description) plausibly relates
        /// to a required capability, even though the catalog's keyword heuristics
        /// didn't produce a match. Used by the closure validator to distinguish
        /// Hard gaps (genuinely missing capability) from Soft gaps (catalog
        /// mapping is incomplete but the tool probably provides it).
        /// </summary>
        public static bool PlausiblyProvidesCapability(
            string toolName,
            IToolDescriptor? descriptor,
            string capabilityId)
            => PlausiblyProvidesCapability(
                toolName,
                descriptor?.Description,
                descriptor?.Category,
                capabilityId);

        public static bool PlausiblyProvidesCapability(
            string toolName,
            string? description,
            string? category,
            string capabilityId)
        {
            if (string.IsNullOrWhiteSpace(toolName))
                return false;

            string identity = string.Join(
                " ",
                toolName,
                description,
                category).ToLowerInvariant();

            if (!PlausibleKeywords.TryGetValue(capabilityId, out var keywords))
                return false;

            return keywords.Any(keyword =>
                identity.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        private static string BuildIdentity(string callName, IToolDescriptor? descriptor) =>
            string.Join(
                " ",
                callName,
                descriptor?.Name,
                descriptor?.Description,
                descriptor?.Category).ToLowerInvariant();

        private static bool ContainsAny(string source, IEnumerable<string> terms) =>
            terms.Any(term =>
                source.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    public sealed class DefaultCapabilityClosureValidator :
        ICapabilityClosureValidator
    {
        public CapabilityClosureResult Validate(
            ResolvedGoalScope goal,
            AgentPlan plan)
        {
            var required = goal.RequiredCapabilities
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order()
                .ToList();
            var provided = plan.Steps
                .SelectMany(step => step.ProvidedCapabilities)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order()
                .ToList();
            var missing = required
                .Except(provided, StringComparer.OrdinalIgnoreCase)
                .Order()
                .ToList();

            // ── Soft gate: plausible-but-unconfirmed capabilities ──
            // For each missing capability, check if ANY plan step's tool
            // identity plausibly relates to it. If yes, the gap is a catalog
            // mapping issue — not a genuine missing capability. Treat as Soft.
            var plausible = new List<string>();
            foreach (string capabilityId in missing)
            {
                bool anyPlausible = plan.Steps.Any(step =>
                {
                    if (step.ToolCall == null) return false;
                    string canonical = step.ToolCall.Name;
                    return DefaultCapabilityCatalog.PlausiblyProvidesCapability(
                        canonical,
                        step.Description,
                        step.DomainPackId,
                        capabilityId);
                });
                if (anyPlausible)
                    plausible.Add(capabilityId);
            }

            return new CapabilityClosureResult
            {
                RequiredCapabilities = required,
                ProvidedCapabilities = provided,
                MissingCapabilities = missing,
                PlausibleButUnconfirmed = plausible
            };
        }

    }

    public sealed class DefaultGoalClosureEvaluator : IGoalClosureEvaluator
    {
        public GoalProofEvaluation Evaluate(
            ResolvedGoalScope goal,
            TargetCoverageResult? coverage,
            IReadOnlyList<StepExecutionResult> results)
        {
            var required = goal.ProofObligations
                .Where(proof => proof.Required)
                .Select(proof => proof.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var satisfied = goal.SatisfiedProofObligations
                .Where(required.Contains)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var proof in goal.ProofObligations.Where(item => item.Required))
            {
                if (IsSatisfied(proof, coverage, results))
                    satisfied.Add(proof.Id);
            }

            return new GoalProofEvaluation
            {
                RequiredProofs = required.Order().ToList(),
                SatisfiedProofs = satisfied.Order().ToList(),
                MissingProofs = required
                    .Where(proof => !satisfied.Contains(proof))
                    .Order()
                    .ToList()
            };
        }

        private static bool IsSatisfied(
            GoalProofObligation proof,
            TargetCoverageResult? coverage,
            IReadOnlyList<StepExecutionResult> results)
        {
            if (proof.Kind == GoalProofKind.TargetCoverage)
            {
                if (coverage == null)
                    return false;
                if (coverage.UnresolvedRequirements.Count > 0)
                    return false;
                if (coverage.Applicability == CoverageApplicability.NotInScope)
                    return coverage.UnresolvedTargets.Count == 0;
                if (coverage.Applicability == CoverageApplicability.OutputOnly)
                    return coverage.UnresolvedTargets.Count == 0
                        && coverage.UnexpectedTargets.Count == 0;
                if (coverage.Applicability == CoverageApplicability.ScopeInsufficient)
                    return false;
                return coverage.UnresolvedTargets.Count == 0
                    && coverage.UnexpectedTargets.Count == 0
                    && (!coverage.IsApplicable
                        || (coverage.ProgressEvaluated
                            ? coverage.IsGoalComplete
                            : coverage.IsComplete));
            }

            var supplyingResults = results
                .Where(result =>
                    result.IsSuccessful
                    && result.ProvidedCapabilities.Contains(
                        proof.CapabilityId,
                        StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (supplyingResults.Count == 0)
                return false;
            if (proof.Kind == GoalProofKind.ParameterSemantics)
            {
                return supplyingResults.Any(result =>
                    result.Validations.Any(validation =>
                        validation.Verdict == ValidationVerdict.PASS
                        && validation.SatisfiedProofObligationIds.Contains(
                            proof.Id,
                            StringComparer.OrdinalIgnoreCase)));
            }
            if (!proof.RequiresValidation
                && proof.Kind == GoalProofKind.CapabilityExecution)
                return true;
            return supplyingResults.Any(result =>
                result.Validations.Any(validation =>
                    validation.Verdict == ValidationVerdict.PASS));
        }
    }
}
