using System.Text.RegularExpressions;
using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Core
{
    /// <summary>
    /// Compiles user language into a planner-independent semantic contract.
    /// Physical tools and proposed calls are deliberately unavailable here.
    /// </summary>
    /// <remarks>中文：将用户语言编译成与规划器无关的语义契约。这里故意没有提供实体工具和拟议通话。</remarks>
    public sealed class DefaultGoalContractCompiler : IGoalContractCompiler
    {
        private static readonly IReadOnlyDictionary<string, string[]> CategoryTerms =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["point"] = new[] { "点图层", "点要素", "point layer", "point layers", "points" },
                ["line"] = new[] { "线图层", "线要素", "线状图层", "line layer", "line layers", "polyline" },
                ["polygon"] = new[]
                {
                    "面图层", "面要素", "面状图层", "polygon layer", "polygon layers", "polygons"
                },
                ["raster"] = new[] { "栅格图层", "raster layer", "raster layers", "rasters" },
                ["table"] = new[] { "独立表", "所有表", "standalone table", "standalone tables" }
            };

        private static readonly string[] LineagePreferenceTerms =
            GisCategoryNormalizer.LineagePreferenceTerms;

        private static readonly string[] ReferenceTerms =
        {
            "参考", "基准", "依据", "参照", "采用", "按照",
            " reference", " based on", " according to", " using "
        };

        private static readonly string[] CreationTerms =
        {
            "创建", "新建", "生成", "产出", "另存", "输出为", "得到一个", "派生",
            "create ", "generate ", "produce ", "derive ", "save as ", "output as "
        };

        private static readonly Regex ChineseTargetPattern = new(
            @"(?:^|[。；，,\r\n])\s*(?:请)?对(?!齐)(?<name>.{1,48}?)(?=(?:进行|执行|计算|赋值|删除|新增|添加|修改|更新|转换|投影|裁剪|统计|检查|验证|读取|导出|合并|融合|处理|映射|写入|填充))",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex EnglishTargetPattern = new(
            @"\b(?:on|within|inspect)\s+[""']?(?<name>[\p{L}\p{N}_\-. ]{2,80}?)[""']?(?=\s+(?:using|with|and|to|from)|[.,;]|$)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex ChineseCategoryFieldPattern = new(
            @"(?<category>线|面|点|栅格)(?:图层|要素|数据)?(?:中|上的|上|的)?\s*(?<field>[^，,。；;\r\n]{1,64}?)\s*字段",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex EnglishDeleteFieldPattern = new(
            @"(?:remove|delete|drop)\s+(?:the\s+)?(?<field>[\p{L}\p{N}_\-. ]{2,64}?)\s+(?:field|property)\s+from\s+(?:all\s+)?(?<category>line|polygon|point|raster)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex FieldRemovalIntentPattern = new(
            @"(?:删除|移除|去掉|remove|delete|drop).{0,192}(?:字段|field|property)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);

        private static readonly Regex ClassCountPattern = new(
            @"(?<count>[2-9]|1[0-2])\s*(?:个)?(?:等级|级别|分类|classes?|breaks?)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public CompiledGoalContract Compile(string goal, DomainContext context)
        {
            string source = goal?.Trim() ?? "";
            var bindings = ResolveExactBindings(source, context);
            AddSemanticBindings(source, context, bindings);

            string scopeText = source;
            foreach (var binding in bindings)
                scopeText = ReplaceMention(scopeText, binding.Mention);

            foreach (string candidate in ExtractUnboundCandidates(source))
            {
                if (bindings.Any(binding =>
                        binding.Mention.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (context.Layers.Any(layer =>
                        layer.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
                    continue;

                bool creation = HasCreationIntentForTarget(source, candidate);
                bindings.Add(new GoalResourceBinding
                {
                    Mention = candidate,
                    Role = creation ? GoalResourceRole.Output : GoalResourceRole.Target,
                    Status = creation
                        ? GoalResourceResolutionStatus.Derivable
                        : GoalResourceResolutionStatus.Missing,
                    Confidence = 1,
                    Evidence = creation
                        ? "explicit-creation-intent"
                        : "user-resource-mention-not-in-project"
                });
                scopeText = ReplaceMention(scopeText, candidate);
            }

            var categories = ResolveCategories(scopeText);
            var requirements = ResolveRequirements(source, categories, context);
            var predicates = requirements
                .Where(requirement =>
                    requirement.GroundingStatus ==
                        GoalRequirementGroundingStatus.Grounded
                    && requirement.BoundFields.Count > 0)
                .Select(requirement => new GoalFieldPredicate
                {
                    DomainCategory = requirement.DomainCategory,
                    Fields = requirement.BoundFields.ToList(),
                    DesiredState = requirement.DesiredState
                })
                .ToList();
            var requiredCapabilities = ResolveCapabilities(
                scopeText,
                requirements);
            var parameterConstraints = ResolveParameterConstraints(
                scopeText,
                requiredCapabilities);
            var deliverables = BuildDeliverables(source, requiredCapabilities);
            var proofs = BuildProofObligations(
                source,
                requiredCapabilities,
                parameterConstraints);
            var explicitTargets = bindings
                .Where(binding =>
                    binding.Status == GoalResourceResolutionStatus.Resolved
                    && binding.Role == GoalResourceRole.Target)
                .SelectMany(binding => binding.ResolvedTargets)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            bool lineagePreference = ContainsAny(
                $" {scopeText.ToLowerInvariant()} ",
                LineagePreferenceTerms);
            GoalTargetQuantifier quantifier;
            if (explicitTargets.Count > 0)
                quantifier = GoalTargetQuantifier.Explicit;
            else if (requirements.Any(requirement =>
                         requirement.DesiredState == TargetStateExpectation.Absent))
                quantifier = GoalTargetQuantifier.AllMatchingState;
            else if (lineagePreference)
                quantifier = GoalTargetQuantifier.RepresentativePerLineage;
            else if (categories.Count > 0)
                quantifier = GoalTargetQuantifier.AllPhysicalTargets;
            else
                quantifier = GoalTargetQuantifier.Unspecified;

            return new CompiledGoalContract
            {
                SourceGoal = source,
                ScopeText = scopeText,
                ResourceBindings = bindings
                    .OrderBy(binding => Math.Max(
                        0,
                        source.IndexOf(
                            binding.Mention,
                            StringComparison.OrdinalIgnoreCase)))
                    .ThenByDescending(binding => binding.Confidence)
                    .ToList(),
                Quantifier = quantifier,
                DomainCategories = categories.Order().ToList(),
                Requirements = requirements,
                ParameterConstraints = parameterConstraints,
                FieldPredicates = predicates,
                RequiredCapabilities = requiredCapabilities,
                Deliverables = deliverables,
                ProofObligations = proofs
            };
        }

        private static List<GoalResourceBinding> ResolveExactBindings(
            string source,
            DomainContext context)
        {
            var bindings = new List<GoalResourceBinding>();
            foreach (var layer in context.Layers
                         .Where(layer => !string.IsNullOrWhiteSpace(layer.Name))
                         .OrderByDescending(layer => layer.Name.Length))
            {
                if (!Mentions(source, layer.Name))
                    continue;
                GoalResourceRole role = HasCreationIntentForTarget(source, layer.Name)
                    ? GoalResourceRole.Output
                    : IsReferenceRole(source, layer.Name)
                        ? GoalResourceRole.Reference
                        : GoalResourceRole.Target;
                bindings.Add(new GoalResourceBinding
                {
                    Mention = layer.Name,
                    Role = role,
                    Status = GoalResourceResolutionStatus.Resolved,
                    ResolvedTargets = new() { layer.Name },
                    Candidates = new() { BuildCandidate(layer) },
                    SemanticType = InferLayerSemanticType(layer),
                    Confidence = 1,
                    Evidence = "exact-project-resource"
                });
            }
            return bindings;
        }

        private static void AddSemanticBindings(
            string source,
            DomainContext context,
            ICollection<GoalResourceBinding> bindings)
        {
            string lower = source.ToLowerInvariant();
            bool aggregateIntent = ContainsAny(
                lower,
                new[] { "密度", "每平方", "数量", "个数", "计数", "density", "count", "per square" });
            if (aggregateIntent)
            {
                foreach (var binding in bindings.Where(binding =>
                             binding.SemanticType.Equals(
                                 "settlement.village",
                                 StringComparison.OrdinalIgnoreCase)
                             && binding.Role == GoalResourceRole.Target))
                    binding.Role = GoalResourceRole.Reference;
            }

            AddSemanticBinding(
                source,
                context,
                bindings,
                semanticType: "administrative-unit.town",
                mentions: new[] { "乡镇", "每个镇", "各镇", "township", "towns" },
                role: GoalResourceRole.Target,
                layer => IsPolygon(layer)
                    && (ContainsAny(layer.Name, new[] { "乡镇", "镇界", "镇边界", "township" })
                        || ContainsAny(string.Join(" ", layer.Fields), new[] { "xiang", "town" })));

            AddSemanticBinding(
                source,
                context,
                bindings,
                semanticType: "settlement.village",
                mentions: new[] { "村庄", "行政村", "村位置", "villages", "village" },
                role: aggregateIntent
                    ? GoalResourceRole.Reference
                    : GoalResourceRole.Target,
                layer => IsPoint(layer)
                    && (ContainsAny(layer.Name, new[] { "村", "village" })
                        || ContainsAny(string.Join(" ", layer.Fields), new[] { "cuncode", "cun_", "village" })));
        }

        private static void AddSemanticBinding(
            string source,
            DomainContext context,
            ICollection<GoalResourceBinding> bindings,
            string semanticType,
            IReadOnlyList<string> mentions,
            GoalResourceRole role,
            Func<LayerInfo, bool> predicate)
        {
            string? mention = mentions.FirstOrDefault(term => Mentions(source, term));
            if (string.IsNullOrWhiteSpace(mention))
                return;
            if (bindings.Any(binding =>
                    binding.SemanticType.Equals(
                        semanticType,
                        StringComparison.OrdinalIgnoreCase)
                    || binding.ResolvedTargets.Any(target =>
                        context.Layers.Any(layer =>
                            layer.Name.Equals(target, StringComparison.OrdinalIgnoreCase)
                            && predicate(layer)))))
                return;

            var candidates = context.Layers.Where(predicate).ToList();
            bindings.Add(new GoalResourceBinding
            {
                Mention = mention,
                Role = role,
                Status = candidates.Count switch
                {
                    0 => GoalResourceResolutionStatus.Missing,
                    1 => GoalResourceResolutionStatus.Resolved,
                    _ => GoalResourceResolutionStatus.Ambiguous
                },
                ResolvedTargets = candidates.Count == 1
                    ? new() { candidates[0].Name }
                    : new(),
                Candidates = candidates.Select(BuildCandidate).ToList(),
                SemanticType = semanticType,
                Confidence = candidates.Count == 1 ? 0.9 : 0.55,
                Evidence = candidates.Count switch
                {
                    0 => "semantic-resource-not-in-project",
                    1 => "gis-ontology-and-project-schema",
                    _ => "semantic-resource-has-multiple-candidates"
                }
            });
        }

        private static List<string> ResolveCategories(string scopeText)
        {
            string searchable = $" {scopeText.ToLowerInvariant()} ";
            return CategoryTerms
                .Where(pair => pair.Value.Any(term =>
                    searchable.Contains(term, StringComparison.OrdinalIgnoreCase)))
                .Select(pair => pair.Key)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<GoalRequirement> ResolveRequirements(
            string source,
            IReadOnlyList<string> categories,
            DomainContext context)
        {
            var requirements = new List<GoalRequirement>();
            if (!FieldRemovalIntentPattern.IsMatch(source))
                return requirements;

            foreach (Match match in ChineseCategoryFieldPattern.Matches(source))
            {
                AddSchemaRequirement(
                    requirements,
                    match.Groups["category"].Value,
                    match.Groups["field"].Value,
                    context);
            }
            foreach (Match match in EnglishDeleteFieldPattern.Matches(source))
            {
                AddSchemaRequirement(
                    requirements,
                    match.Groups["category"].Value,
                    match.Groups["field"].Value,
                    context);
            }

            // A single category may be provided elsewhere in the sentence.
            // Preserve it for a conventional "delete <field> field" clause,
            // but never guess among multiple resource categories.
            // 中文：句子的其他地方可能只给出一个类别。保留它作为传统的“删除字段”子句，但切勿在多个资源类别之间猜测。
            if (requirements.Count == 0 && categories.Count == 1)
            {
                var fallback = Regex.Match(
                    source,
                    @"(?:删除|移除|去掉)\s*(?:所有|全部)?(?<field>[\p{L}\p{N}_\-.]{1,64})\s*字段",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (fallback.Success)
                {
                    AddSchemaRequirement(
                        requirements,
                        categories[0],
                        fallback.Groups["field"].Value,
                        context);
                }
            }
            return requirements;
        }

        private static void AddSchemaRequirement(
            ICollection<GoalRequirement> requirements,
            string rawCategory,
            string rawField,
            DomainContext context)
        {
            string category = NormalizeCategory(rawCategory);
            string mention = NormalizeArtifactMention(rawField);
            if (string.IsNullOrWhiteSpace(category)
                || string.IsNullOrWhiteSpace(mention))
            {
                return;
            }

            string semanticType = ResolveArtifactSemanticType(mention);
            var boundFields = ResolveBoundFields(
                category,
                mention,
                semanticType,
                context);
            bool semanticReference = !string.IsNullOrWhiteSpace(semanticType);
            var status = semanticReference && boundFields.Count == 0
                ? GoalRequirementGroundingStatus.Unresolved
                : GoalRequirementGroundingStatus.Grounded;
            if (!semanticReference && boundFields.Count == 0)
                boundFields.Add(mention);

            requirements.Add(new GoalRequirement
            {
                Id = $"requirement-{requirements.Count + 1}",
                OperationFamily = GoalOperationFamily.SchemaTransition,
                DomainCategory = category,
                ArtifactMention = mention,
                ArtifactSemanticType = semanticType,
                BoundFields = boundFields,
                DesiredState = TargetStateExpectation.Absent,
                RequiredCapability = GisCapabilityIds.AlterFieldSchema,
                GroundingStatus = status,
                GroundingEvidence = status ==
                    GoalRequirementGroundingStatus.Grounded
                    ? semanticReference
                        ? "field-artifact-ledger-or-live-schema"
                        : "literal-field-name"
                    : "semantic-field-has-no-provenance-binding"
            });
        }

        private static List<string> ResolveBoundFields(
            string category,
            string mention,
            string semanticType,
            DomainContext context)
        {
            var categoryLayers = context.Layers
                .Where(layer => NormalizeCategory(layer.GeometryType)
                    .Equals(category, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var exact = categoryLayers
                .SelectMany(layer => layer.Fields)
                .Where(field => field.Equals(
                    mention,
                    StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (exact.Count > 0)
                return exact;
            if (string.IsNullOrWhiteSpace(semanticType))
                return new List<string>();

            var categoryLayerNames = categoryLayers
                .Select(layer => layer.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return context.Artifacts
                .Where(artifact => categoryLayerNames.Contains(
                    artifact.OwnerLayer))
                .Where(artifact => artifact.SemanticType.Equals(
                    semanticType,
                    StringComparison.OrdinalIgnoreCase))
                .Where(artifact => categoryLayers.Any(layer =>
                    layer.Name.Equals(
                        artifact.OwnerLayer,
                        StringComparison.OrdinalIgnoreCase)
                    && layer.Fields.Contains(
                        artifact.FieldName,
                        StringComparer.OrdinalIgnoreCase)))
                .Select(artifact => artifact.FieldName)
                .Where(field => !string.IsNullOrWhiteSpace(field))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string ResolveArtifactSemanticType(string mention)
        {
            if (ContainsAny(
                    mention,
                    new[] { "长度", "里程", "length", "distance" }))
            {
                return GisCapabilityIds.MeasureLength;
            }
            if (ContainsAny(
                    mention,
                    new[] { "面积", "area" }))
            {
                return GisCapabilityIds.MeasureArea;
            }
            return "";
        }

        private static string NormalizeArtifactMention(string value)
        {
            string normalized = Regex.Replace(value.Trim(), @"\s+", " ");
            normalized = Regex.Replace(
                normalized,
                @"^(?:的|the\s+)",
                "",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return normalized.Trim();
        }

        private static List<string> ResolveCapabilities(
            string source,
            IReadOnlyCollection<GoalRequirement> requirements)
        {
            string lower = source.ToLowerInvariant();
            var capabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool schemaTransition = requirements.Any(requirement =>
                requirement.OperationFamily ==
                    GoalOperationFamily.SchemaTransition);
            if (ContainsAny(lower, new[] { "密度", "每平方", "density", "per square" }))
            {
                capabilities.Add(GisCapabilityIds.SpatialAggregateWithin);
                capabilities.Add(GisCapabilityIds.ComputeMetric);
            }
            bool areaMeasurementIntent = ContainsAny(lower, new[]
                {
                    "计算面积", "面积计算", "量算面积", "椭球面积", "测地线面积",
                    "公顷", "平方米", "平方千米", "平方公里",
                    "calculate area", "area measurement", "measure area",
                    "hectare", "square meter", "square kilomet"
                })
                || Regex.IsMatch(
                    lower,
                    @"(?:计算|量算).{0,24}面积|面积.{0,12}(?:计算|量算)",
                    RegexOptions.CultureInvariant);
            if (!schemaTransition && areaMeasurementIntent)
                capabilities.Add(GisCapabilityIds.MeasureArea);
            if (!schemaTransition
                && ContainsAny(lower, new[] { "长度", "里程", "length", "distance" }))
                capabilities.Add(GisCapabilityIds.MeasureLength);
            if (ContainsAny(lower, new[] { "分级", "等级", "分类", "classify", "classes", "breaks" }))
                capabilities.Add(GisCapabilityIds.ClassifyNumeric);
            if (ContainsAny(lower, new[]
                {
                    "符号", "渲染", "颜色", "色带", "渐变", "symbology", "renderer", "color ramp"
                }))
                capabilities.Add(GisCapabilityIds.RenderMap);
            if (ContainsAny(lower, new[]
                {
                    "投影", "坐标系", "空间参考", "coordinate system",
                    "spatial reference"
                })
                || Regex.IsMatch(
                    lower,
                    @"\b(?:project|reproject)\b",
                    RegexOptions.CultureInvariant))
                capabilities.Add(GisCapabilityIds.TransformCrs);
            if (ContainsAny(lower, new[]
                {
                    "计算字段", "更新字段", "赋值", "calculate field",
                    "update field", "populate field"
                }))
                capabilities.Add(GisCapabilityIds.ComputeMetric);
            if (requirements.Count > 0
                || ContainsAny(lower, new[]
                {
                    "新增字段", "添加字段", "删除字段", "移除字段", "字段结构",
                    "add field", "delete field", "drop field", "schema"
                }))
                capabilities.Add(GisCapabilityIds.AlterFieldSchema);
            if (ContainsAny(lower, CreationTerms))
                capabilities.Add(GisCapabilityIds.CreateArtifact);
            return capabilities.Order().ToList();
        }

        private static List<GoalParameterConstraint> ResolveParameterConstraints(
            string source,
            IReadOnlyCollection<string> capabilities)
        {
            var constraints = new List<GoalParameterConstraint>();
            var subjects = Regex.Matches(
                    source,
                    @"(?:(?:geodesic|planar|projected)\s+)?(?:length|distance|area)|(?:地线|投影|平面)?长度|里程|(?:椭球|测地线|投影|平面)?面积",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                .Cast<Match>()
                .OrderBy(match => match.Index)
                .ToList();

            for (int index = 0; index < subjects.Count; index++)
            {
                Match subject = subjects[index];
                int end = index + 1 < subjects.Count
                    ? subjects[index + 1].Index
                    : source.Length;
                string segment = source.Substring(
                    subject.Index,
                    Math.Max(0, end - subject.Index));
                bool area = subject.Value.Contains("面积", StringComparison.OrdinalIgnoreCase)
                    || subject.Value.Contains("area", StringComparison.OrdinalIgnoreCase);
                string capability = area
                    ? GisCapabilityIds.MeasureArea
                    : GisCapabilityIds.MeasureLength;
                if (!capabilities.Contains(capability, StringComparer.OrdinalIgnoreCase))
                    continue;

                string unit = ResolveMeasurementUnit(segment, area);
                if (!string.IsNullOrWhiteSpace(unit))
                {
                    AddOrReplaceConstraint(
                        constraints,
                        new GoalParameterConstraint
                        {
                            Key = "unit",
                            Value = unit,
                            AppliesToCapability = capability,
                            SourceText = segment.Trim()
                        });
                }

                string method = ResolveMeasurementMethod(segment);
                if (!string.IsNullOrWhiteSpace(method))
                {
                    AddOrReplaceConstraint(
                        constraints,
                        new GoalParameterConstraint
                        {
                            Key = "measurement_method",
                            Value = method,
                            AppliesToCapability = capability,
                            SourceText = segment.Trim()
                        });
                }

                Match precision = Regex.Match(
                    segment,
                    @"(?:保留|精确到|精度(?:为)?|round(?:ed)?\s+to)\s*(?<value>\d+|[零一二两三四五六七八九十]+)\s*(?:位)?(?:小数|decimal(?:\s+places?)?)",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (precision.Success
                    && TryParseSmallInteger(
                        precision.Groups["value"].Value,
                        out int decimalPlaces))
                {
                    AddOrReplaceConstraint(
                        constraints,
                        new GoalParameterConstraint
                        {
                            Key = "decimal_places",
                            Value = decimalPlaces.ToString(
                                System.Globalization.CultureInfo.InvariantCulture),
                            AppliesToCapability = capability,
                            SourceText = precision.Value
                        });
                    AddOrReplaceConstraint(
                        constraints,
                        new GoalParameterConstraint
                        {
                            Key = "rounding_mode",
                            Value = Regex.IsMatch(
                                segment,
                                @"四舍五入|half[\s_-]*up",
                                RegexOptions.IgnoreCase)
                                ? "HALF_UP"
                                : "TO_EVEN",
                            AppliesToCapability = capability,
                            SourceText = precision.Value
                        });
                }
            }

            return constraints
                .OrderBy(item => item.AppliesToCapability)
                .ThenBy(item => item.Key)
                .ToList();
        }

        private static string ResolveMeasurementUnit(string segment, bool area)
        {
            if (area)
            {
                if (Regex.IsMatch(segment, @"公顷|\bhectares?\b|\bha\b", RegexOptions.IgnoreCase))
                    return "HECTARES";
                if (Regex.IsMatch(segment, @"平方千米|平方公里|\bsquare\s+kilomet(?:er|re)s?\b|\bsq\.?\s*km\b", RegexOptions.IgnoreCase))
                    return "SQUARE_KILOMETERS";
                if (Regex.IsMatch(segment, @"平方米|\bsquare\s+met(?:er|re)s?\b|\bsq\.?\s*m\b", RegexOptions.IgnoreCase))
                    return "SQUARE_METERS";
                if (Regex.IsMatch(segment, @"亩"))
                    return "MU";
                return "";
            }

            if (Regex.IsMatch(segment, @"千米|公里|\bkilomet(?:er|re)s?\b|\bkm\b", RegexOptions.IgnoreCase))
                return "KILOMETERS";
            if (Regex.IsMatch(segment, @"米级单位|(?<!平)方?米(?:为?单位)?|\bmet(?:er|re)s?\b|\bm\b", RegexOptions.IgnoreCase))
                return "METERS";
            if (Regex.IsMatch(segment, @"英里|\bmiles?\b", RegexOptions.IgnoreCase))
                return "MILES";
            return "";
        }

        private static string ResolveMeasurementMethod(string segment)
        {
            if (Regex.IsMatch(
                    segment,
                    @"椭球|测地线|地线|geodesic",
                    RegexOptions.IgnoreCase))
                return "GEODESIC";
            if (Regex.IsMatch(
                    segment,
                    @"投影|平面|planar|projected",
                    RegexOptions.IgnoreCase))
                return "PLANAR";
            return "";
        }

        private static bool TryParseSmallInteger(string source, out int value)
        {
            if (int.TryParse(source, out value))
                return value is >= 0 and <= 15;
            var digits = new Dictionary<char, int>
            {
                ['零'] = 0, ['一'] = 1, ['二'] = 2, ['两'] = 2,
                ['三'] = 3, ['四'] = 4, ['五'] = 5, ['六'] = 6,
                ['七'] = 7, ['八'] = 8, ['九'] = 9
            };
            if (source == "十")
            {
                value = 10;
                return true;
            }
            if (source.Length == 2 && source[0] == '十' && digits.TryGetValue(source[1], out int tail))
            {
                value = 10 + tail;
                return value <= 15;
            }
            return source.Length == 1 && digits.TryGetValue(source[0], out value);
        }

        private static void AddOrReplaceConstraint(
            List<GoalParameterConstraint> constraints,
            GoalParameterConstraint item)
        {
            constraints.RemoveAll(existing =>
                existing.Key.Equals(item.Key, StringComparison.OrdinalIgnoreCase)
                && existing.AppliesToCapability.Equals(
                    item.AppliesToCapability,
                    StringComparison.OrdinalIgnoreCase));
            constraints.Add(item);
        }

        private static List<GoalDeliverable> BuildDeliverables(
            string source,
            IReadOnlyCollection<string> capabilities)
        {
            var deliverables = new List<GoalDeliverable>();
            AddDeliverable(
                capabilities,
                deliverables,
                GisCapabilityIds.SpatialAggregateWithin,
                GoalDeliverableKind.Metric,
                "spatial-aggregate",
                "目标单元内的空间聚合结果");
            AddDeliverable(
                capabilities,
                deliverables,
                GisCapabilityIds.ComputeMetric,
                GoalDeliverableKind.Metric,
                "computed-metric",
                "可复核的业务指标");
            AddDeliverable(
                capabilities,
                deliverables,
                GisCapabilityIds.ClassifyNumeric,
                GoalDeliverableKind.Classification,
                "numeric-classification",
                "数值分级结果");
            AddDeliverable(
                capabilities,
                deliverables,
                GisCapabilityIds.RenderMap,
                GoalDeliverableKind.MapPresentation,
                "map-presentation",
                "地图表达与符号化");
            AddDeliverable(
                capabilities,
                deliverables,
                GisCapabilityIds.MeasureArea,
                GoalDeliverableKind.Field,
                "area-measurement",
                "面积量算结果");
            AddDeliverable(
                capabilities,
                deliverables,
                GisCapabilityIds.MeasureLength,
                GoalDeliverableKind.Field,
                "length-measurement",
                "长度量算结果");
            AddDeliverable(
                capabilities,
                deliverables,
                GisCapabilityIds.TransformCrs,
                GoalDeliverableKind.Dataset,
                "projected-dataset",
                "目标空间参考下的数据");
            AddDeliverable(
                capabilities,
                deliverables,
                GisCapabilityIds.AlterFieldSchema,
                GoalDeliverableKind.Field,
                "field-schema",
                "符合目标状态的字段结构");
            AddDeliverable(
                capabilities,
                deliverables,
                GisCapabilityIds.CreateArtifact,
                GoalDeliverableKind.Dataset,
                "created-artifact",
                "新建或派生的数据成果");
            return deliverables;
        }

        private static List<GoalProofObligation> BuildProofObligations(
            string source,
            IReadOnlyCollection<string> capabilities,
            IReadOnlyCollection<GoalParameterConstraint> parameterConstraints)
        {
            var proofs = new List<GoalProofObligation>();
            if (capabilities.Count > 0)
            {
                proofs.Add(new GoalProofObligation
                {
                    Id = "target-coverage",
                    Kind = GoalProofKind.TargetCoverage,
                    Description = "计划与执行覆盖完整目标范围"
                });
            }

            foreach (string capability in capabilities)
            {
                bool postcondition = capability is
                    GisCapabilityIds.SpatialAggregateWithin
                    or GisCapabilityIds.ComputeMetric
                    or GisCapabilityIds.ClassifyNumeric
                    or GisCapabilityIds.RenderMap
                    or GisCapabilityIds.AlterFieldSchema
                    or GisCapabilityIds.CreateArtifact;
                var proof = new GoalProofObligation
                {
                    Id = $"proof:{capability}",
                    Kind = capability == GisCapabilityIds.RenderMap
                        ? GoalProofKind.MapPresentation
                        : postcondition
                            ? GoalProofKind.VerifiedPostcondition
                            : GoalProofKind.CapabilityExecution,
                    CapabilityId = capability,
                    Description = $"能力 {capability} 已执行且形成可验证结果",
                    RequiresValidation = postcondition
                };
                if (capability == GisCapabilityIds.ClassifyNumeric
                    && ClassCountPattern.Match(source) is { Success: true } classMatch)
                    proof.Expected["class_count"] =
                        classMatch.Groups["count"].Value;
                if (capability == GisCapabilityIds.RenderMap)
                {
                    if (ContainsAny(source, new[] { "红色", "红色系", "红色渐变", "red" }))
                        proof.Expected["color_family"] = "red";
                    if (ContainsAny(source, new[] { "由浅到深", "浅到深", "light to dark" }))
                        proof.Expected["direction"] = "light-to-dark";
                }
                proofs.Add(proof);

                var expectedParameters = parameterConstraints
                    .Where(constraint => constraint.AppliesToCapability.Equals(
                        capability,
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (expectedParameters.Count > 0)
                {
                    proofs.Add(new GoalProofObligation
                    {
                        Id = $"proof:parameter:{capability}",
                        Kind = GoalProofKind.ParameterSemantics,
                        CapabilityId = capability,
                        Description = $"能力 {capability} 的单位、方法和数值精度符合目标合同",
                        RequiresValidation = true,
                        Expected = expectedParameters.ToDictionary(
                            constraint => constraint.Key,
                            constraint => constraint.Value,
                            StringComparer.OrdinalIgnoreCase)
                    });
                }
            }
            return proofs;
        }

        private static GoalResourceCandidate BuildCandidate(LayerInfo layer)
        {
            string candidateId = !string.IsNullOrWhiteSpace(layer.MapMemberId)
                ? "map:" + layer.MapMemberId
                : !string.IsNullOrWhiteSpace(layer.DatasetId)
                    ? "dataset:" + layer.DatasetId
                    : "name:" + layer.Name.Trim().ToLowerInvariant();
            return new GoalResourceCandidate
            {
                CandidateId = candidateId,
                DisplayName = layer.Name,
                MapMemberId = layer.MapMemberId,
                DatasetId = layer.DatasetId,
                GeometryType = layer.GeometryType,
                Fields = layer.Fields.ToList(),
                SchemaFingerprint = layer.SchemaFingerprint,
                LineageRoot = layer.LineageRoot,
                LineageDepth = layer.LineageDepth
            };
        }

        private static void AddDeliverable(
            IReadOnlyCollection<string> capabilities,
            ICollection<GoalDeliverable> deliverables,
            string capability,
            GoalDeliverableKind kind,
            string id,
            string description)
        {
            if (!capabilities.Contains(capability, StringComparer.OrdinalIgnoreCase))
                return;
            deliverables.Add(new GoalDeliverable
            {
                Id = id,
                Kind = kind,
                Description = description,
                CapabilityId = capability
            });
        }

        private static IEnumerable<string> ExtractUnboundCandidates(string source)
        {
            foreach (Match match in ChineseTargetPattern.Matches(source))
            {
                string candidate = NormalizeCandidate(match.Groups["name"].Value);
                if (IsPlausibleResource(candidate))
                    yield return candidate;
            }
            foreach (Match match in EnglishTargetPattern.Matches(source))
            {
                string candidate = NormalizeCandidate(match.Groups["name"].Value);
                if (IsPlausibleResource(candidate))
                    yield return candidate;
            }
        }

        private static string NormalizeCandidate(string value)
        {
            string normalized = Regex.Replace(value.Trim(), @"\s+", " ");
            normalized = Regex.Replace(
                normalized,
                @"^(?:当前地图中(?:的)?|当前地图的|当前|地图中的|地图中|该|这个)",
                "");
            return normalized.Trim(' ', '"', '\'', '“', '”', '《', '》');
        }

        private static bool IsPlausibleResource(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length < 2 || value.Length > 80)
                return false;
            if (value.EndsWith("字段", StringComparison.OrdinalIgnoreCase))
                return false;
            return !value.Equals("所有图层", StringComparison.OrdinalIgnoreCase)
                && !value.Equals("全部图层", StringComparison.OrdinalIgnoreCase)
                && !value.Equals("原始图层", StringComparison.OrdinalIgnoreCase)
                && !value.Equals("派生图层", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsReferenceRole(string goal, string target)
        {
            int index = goal.IndexOf(target, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                return false;
            int prefixStart = Math.Max(0, index - 18);
            string prefix = " "
                + goal.Substring(prefixStart, index - prefixStart)
                    .ToLowerInvariant()
                + " ";
            int suffixStart = index + target.Length;
            int suffixLength = Math.Min(
                16,
                Math.Max(0, goal.Length - suffixStart));
            string suffix = suffixLength == 0
                ? ""
                : goal.Substring(suffixStart, suffixLength)
                    .ToLowerInvariant();
            return ContainsAny(prefix, ReferenceTerms)
                || ContainsAny(suffix, new[]
                {
                    "作为参考", "用作参考", "作为基准",
                    " as reference", " as a reference"
                });
        }

        private static bool HasCreationIntentForTarget(string goal, string target)
        {
            int index = goal.IndexOf(target, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                return false;
            int start = Math.Max(0, index - 24);
            string prefix = goal.Substring(start, index - start).ToLowerInvariant();
            return ContainsAny(prefix, CreationTerms);
        }

        private static string InferLayerSemanticType(LayerInfo layer)
        {
            if (IsPolygon(layer)
                && ContainsAny(layer.Name, new[] { "乡镇", "镇界", "township" }))
                return "administrative-unit.town";
            if (IsPoint(layer)
                && ContainsAny(layer.Name, new[] { "村", "village" }))
                return "settlement.village";
            return NormalizeCategory(layer.GeometryType);
        }

        private static bool IsPolygon(LayerInfo layer) =>
            NormalizeCategory(layer.GeometryType) == "polygon";

        private static bool IsPoint(LayerInfo layer) =>
            NormalizeCategory(layer.GeometryType) == "point";

        private static string NormalizeCategory(string value)
        {
            string lower = (value ?? "").Trim().ToLowerInvariant();
            if (lower is "线" or "line" || lower.Contains("polyline")) return "line";
            if (lower is "面" or "polygon" || lower.Contains("polygon")) return "polygon";
            if (lower is "点" or "point" || lower.Contains("point")) return "point";
            if (lower is "栅格" or "raster" || lower.Contains("raster")) return "raster";
            if (lower.Contains("table") || lower.Contains("表")) return "table";
            return lower;
        }

        private static string ReplaceMention(string source, string mention) =>
            Regex.Replace(
                source,
                Regex.Escape(mention),
                " ",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static bool Mentions(string goal, string target)
        {
            if (string.IsNullOrWhiteSpace(target))
                return false;
            bool containsCjk = target.Any(ch => ch >= '\u3400' && ch <= '\u9fff');
            if (containsCjk)
                return target.Length >= 2
                    && goal.Contains(target, StringComparison.OrdinalIgnoreCase);
            return Regex.IsMatch(
                goal,
                $@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(target)}(?![\p{{L}}\p{{N}}_])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static bool ContainsAny(string source, IEnumerable<string> terms) =>
            terms.Any(term =>
                source.Contains(term, StringComparison.OrdinalIgnoreCase));

        private static string FirstNonEmpty(params string[] values) =>
            values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";
    }
}
