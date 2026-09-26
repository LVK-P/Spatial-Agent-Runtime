using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using SpatialAgent.Runtime;
using SpatialAgent.Runtime.Contracts;
using SpatialAgent.Core;
using SpatialAgent.Tools.Core;

// ────────────────────────────────────────────────────────────────
// DynamicToolLoader.cs — Runtime tool registration from manifest JSON.
//
// Reads an optional tools_manifest.json generated on the user's licensed
// ArcGIS workstation and registers each local tool as a SchemaGPToolAdapter.
// Called during ViewModel initialization (LoadAndRegister).
// ────────────────────────────────────────────────────────────────
namespace SpatialAgent.Services
{
    public class ToolManifestEntry
    {
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("function_name")] public string? FunctionName { get; set; }
        [JsonProperty("display_name")] public string? DisplayName { get; set; }
        [JsonProperty("execution_name")] public string? ExecutionName { get; set; }
        [JsonProperty("summary")] public string Summary { get; set; } = "";
        [JsonProperty("description")] public string Description { get; set; } = "";
        [JsonProperty("destructive")] public bool Destructive { get; set; }
        [JsonProperty("risk_level")] public string? RiskLevel { get; set; }
        [JsonProperty("verification")] public string? Verification { get; set; }
        [JsonProperty("parameter_order")] public List<string>? ParameterOrder { get; set; }
        [JsonProperty("input_params")] public List<string>? InputParams { get; set; }
        [JsonProperty("output_params")] public List<string>? OutputParams { get; set; }
        [JsonProperty("primary_output")] public string? PrimaryOutput { get; set; }
        [JsonProperty("capability_claims")]
        public List<ToolManifestCapabilityClaim>? CapabilityClaims { get; set; }
        [JsonProperty("params")] public List<ToolManifestParam> Params { get; set; } = new();
    }

    /// <summary>
    /// Manifest transport shape. Keep snake_case concerns in Services instead
    /// of coupling the pure Contracts DTO to a JSON implementation.
    /// </summary>
    public sealed class ToolManifestCapabilityClaim
    {
        [JsonProperty("capability_id")]
        public string CapabilityId { get; set; } = "";

        [JsonProperty("applicable_geometry_types")]
        public List<string>? ApplicableGeometryTypes { get; set; }

        [JsonProperty("requires_postcondition_evidence")]
        public bool RequiresPostconditionEvidence { get; set; }

        [JsonProperty("evidence")]
        public string? Evidence { get; set; }

        [JsonProperty("confidence")]
        public double? Confidence { get; set; }
    }

    public class ToolManifestParam
    {
        [JsonProperty("name")]
        public string Name { get; set; } = "";

        [JsonProperty("type")]
        public string Type { get; set; } = "string";

        [JsonProperty("title")]
        public string Title { get; set; } = "";

        [JsonProperty("description")]
        public string Description { get; set; } = "";

        [JsonProperty("required")]
        public bool Required { get; set; }

        [JsonProperty("direction")]
        public string? Direction { get; set; }

        [JsonProperty("parameter_type")]
        public string? ParameterType { get; set; }

        [JsonProperty("order")]
        public int? Order { get; set; }

        [JsonProperty("enum")]
        public List<string>? Enum { get; set; }

        [JsonProperty("enum_labels")]
        public Dictionary<string, string>? EnumLabels { get; set; }

        [JsonProperty("datatype")]
        public string? DataType { get; set; }

        [JsonProperty("min_value")]
        public double? MinValue { get; set; }

        [JsonProperty("max_value")]
        public double? MaxValue { get; set; }

        [JsonProperty("default_value_text")]
        public string? DefaultValueText { get; set; }

        [JsonProperty("semantic_role")]
        public string? SemanticRole { get; set; }

        [JsonProperty("is_path_candidate")]
        public bool IsPathCandidate { get; set; }

        [JsonProperty("is_spatial_ref_candidate")]
        public bool IsSpatialRefCandidate { get; set; }

        [JsonProperty("is_field_candidate")]
        public bool IsFieldCandidate { get; set; }

        [JsonProperty("is_where_clause_candidate")]
        public bool IsWhereClauseCandidate { get; set; }

        [JsonProperty("multi_value")]
        public bool MultiValue { get; set; }
    }

    public class DynamicToolLoader
    {
        /// <summary>
        /// Load tools from manifest JSON and register into ToolRegistry.
        /// </summary>
        /// <param name="manifestPath">Path to tools_manifest.json</param>
        /// <param name="registry">Target ToolRegistry</param>
        /// <returns>Number of tools registered</returns>
        public static int LoadAndRegister(string manifestPath, ToolRegistry registry, IWorkspaceService? workspace = null)
        {
            if (!File.Exists(manifestPath))
                return 0;

            var riskCatalog = ToolRiskCatalog.Instance;
            var schemaReg = new ToolSchemaRegistry();
            // Register core definitions for contracts (needed for verification lookup)
            schemaReg.RegisterAllDefaults();

            Dictionary<string, List<ToolManifestEntry>>? manifest;
            try
            {
                string json = File.ReadAllText(manifestPath);
                manifest = JsonConvert.DeserializeObject<Dictionary<string, List<ToolManifestEntry>>>(json);
            }
            catch (Exception ex)
            {
                AgentDiagnostics.Warn("DynamicToolLoader.Load",
                    "本地工具 manifest 解析失败，动态工具将零注册", ex);
                return 0;
            }

            if (manifest == null) return 0;

            int count = 0;
            foreach (var tbKv in manifest)
            {
                string tbName = tbKv.Key;
                string alias = TbNameToAlias(tbName);

                foreach (var tool in tbKv.Value)
                {
                    string schemaName = $"{alias}_{tool.Name}";
                    var def = BuildToolDefinition(schemaName, tool, alias, riskCatalog);
                    if (def != null && registry.GetDescriptor(def.Name) == null)
                    {
                        registry.Register(new SchemaGPToolAdapter(def.Name, def, schemaReg, workspace));
                        count++;
                    }
                }
            }

            return count;
        }

        private static ToolDefinition? BuildToolDefinition(
            string schemaName,
            ToolManifestEntry entry,
            string alias,
            ToolRiskCatalog riskCatalog)
        {
            if (entry.Params == null || entry.Params.Count == 0)
                return null;

            var props = new Dictionary<string, ParameterProperty>();
            var required = new List<string>();

            var orderedParams = entry.Params
                .Select((p, index) => new
                {
                    Param = p,
                    Index = index,
                    Name = string.IsNullOrWhiteSpace(p.Name) ? $"param_{index}" : p.Name
                })
                .OrderBy(x => x.Param.Order ?? x.Index)
                .ToList();

            foreach (var item in orderedParams)
            {
                var p = item.Param;
                string pname = item.Name;
                List<string>? enumList = null;
                if (p.Enum != null && p.Enum.Count > 0)
                    enumList = p.Enum;

                props[pname] = new ParameterProperty
                {
                    Type = p.Type ?? "string",
                    Description = (p.Description ?? p.Title ?? pname).Length > 150
                        ? (p.Description ?? p.Title ?? pname)[..150]
                        : (p.Description ?? p.Title ?? pname),
                    Enum = enumList,
                    DataType = p.DataType,
                    MinValue = p.MinValue,
                    MaxValue = p.MaxValue,
                    DefaultValue = p.DefaultValueText,
                    SemanticRole = p.SemanticRole,
                    IsPath = p.IsPathCandidate,
                    IsSpatialRef = p.IsSpatialRefCandidate,
                    IsField = p.IsFieldCandidate,
                    IsWhereClause = p.IsWhereClauseCandidate,
                    MultiValue = p.MultiValue
                };

                if (IsRequiredInput(p))
                    required.Add(pname);
            }

            VerificationRule? verification = null;
            if (!string.IsNullOrEmpty(entry.Verification) && entry.Verification != "null")
            {
                verification = new VerificationRule
                {
                    Type = entry.Verification,
                    FixHint = entry.Verification switch
                    {
                        "verify_exists" => "检查输出",
                        "verify_field" => "检查字段",
                        "verify_spatial_ref" => "检查坐标系",
                        _ => ""
                    }
                };
            }

            // Preserve explicit parameter order from manifest. New manifests write
            // parameter_order from arcpy.GetParameterInfo(); old manifests fall
            // back to their current order.
            var paramOrder = entry.ParameterOrder?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            if (paramOrder == null || paramOrder.Count == 0)
                paramOrder = orderedParams.Select(p => p.Name).ToList();

            var inputParams = entry.InputParams?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList()
                ?? InferInputParams(orderedParams.Select(p => p.Param));
            var outputParams = entry.OutputParams?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList()
                ?? InferOutputParams(orderedParams.Select(p => p.Param));
            string? primaryOutput = !string.IsNullOrWhiteSpace(entry.PrimaryOutput)
                ? entry.PrimaryOutput
                : outputParams.FirstOrDefault();
            ToolRiskLevel riskLevel = NormalizeRiskLevel(entry.RiskLevel)
                ?? InferRiskLevel(entry, outputParams);

            // Read pre-computed identities from manifest (generated at build time)
            string funcName = entry.FunctionName ?? SanitizeFunctionName($"{alias}_{entry.Name}");
            string displayName = entry.DisplayName ?? entry.Name;
            string execName = entry.ExecutionName ?? $"{alias}.{entry.Name.Replace(" ", "")}";
            // Fallback: sanitize for safety if manifest fields are missing
            if (string.IsNullOrEmpty(funcName))
                funcName = SanitizeFunctionName($"{alias}_{entry.Name}");

            ToolRiskCatalogEntry? riskOverride = riskCatalog.TryGet(funcName, out var catalogEntry)
                ? catalogEntry
                : riskCatalog.TryGet(execName, out catalogEntry)
                    ? catalogEntry
                    : null;

            if (riskOverride.HasValue)
                riskLevel = riskOverride.Value.RiskLevel;

            var safetyProfile = BuildSafetyProfile(
                riskLevel,
                outputParams,
                entry.Name,
                riskOverride);
            var governance = BuildGovernance(riskOverride);
            return new ToolDefinition
            {
                FunctionName = funcName,
                DisplayName = displayName,
                ExecutionName = execName,
                Name = funcName, // Use safe FunctionName for IToolDescriptor.Name
                Description = (entry.Summary ?? entry.Description ?? entry.Name).Length > 300
                    ? (entry.Summary ?? entry.Description ?? entry.Name)[..300]
                    : (entry.Summary ?? entry.Description ?? entry.Name),
                Category = "gp",
                Destructive = riskLevel == ToolRiskLevel.Destructive,
                RiskLevel = riskLevel,
                SafetyProfile = safetyProfile,
                Governance = governance,
                CapabilityClaims = entry.CapabilityClaims?
                    .Where(claim =>
                        !string.IsNullOrWhiteSpace(claim.CapabilityId))
                    .Select(claim => new ToolCapabilityClaim
                    {
                        CapabilityId = claim.CapabilityId.Trim(),
                        ApplicableGeometryTypes =
                            claim.ApplicableGeometryTypes?
                                .Where(item =>
                                    !string.IsNullOrWhiteSpace(item))
                                .Select(item => item.Trim())
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToList()
                            ?? new List<string>(),
                        RequiresPostconditionEvidence =
                            claim.RequiresPostconditionEvidence,
                        Evidence = string.IsNullOrWhiteSpace(claim.Evidence)
                            ? "manifest"
                            : claim.Evidence.Trim(),
                        Confidence = Math.Clamp(
                            claim.Confidence ?? 1,
                            0,
                            1)
                    })
                    .ToList(),
                EffectProfile = BuildEffectProfile(
                    riskLevel,
                    safetyProfile,
                    governance,
                    entry.Name,
                    riskOverride?.BucketReason,
                    riskOverride.HasValue),
                Verification = verification,
                ParameterOrder = paramOrder,
                InputParams = inputParams,
                OutputParams = outputParams,
                PrimaryOutput = primaryOutput,
                Parameters = new ToolParameters
                {
                    Type = "object",
                    Properties = props,
                    Required = required
                }
            };
        }

        private static EffectProfile BuildEffectProfile(
            ToolRiskLevel riskLevel,
            ToolSafetyProfile safety,
            ToolGovernanceMetadata? governance,
            string toolName,
            string? bucketReason,
            bool hasRiskOverride)
        {
            string reason = bucketReason ?? "";
            string lowerName = toolName.ToLowerInvariant();
            DataEffectKind effects = DataEffectKind.ReadData;
            string id = "read-data";
            string probe = "none";
            string approvalGroup = "read-data";
            EvidenceConfidence confidence = EvidenceConfidence.Medium;
            ReversibilityKind reversibility = ReversibilityKind.NaturallyReversible;
            bool backup = false;

            if (riskLevel == ToolRiskLevel.Increment)
            {
                if (reason.Contains("appends new rows", StringComparison.OrdinalIgnoreCase))
                {
                    effects |= DataEffectKind.AppendRows;
                    id = "append-rows";
                    probe = "target-count";
                }
                else if (reason.Contains("schema addition", StringComparison.OrdinalIgnoreCase))
                {
                    effects |= DataEffectKind.AlterSchema;
                    id = "schema-addition";
                    probe = "schema";
                }
                else if (reason.Contains("virtual join", StringComparison.OrdinalIgnoreCase))
                {
                    effects |= DataEffectKind.ModifyMapState;
                    id = "virtual-join";
                    probe = "map-state";
                }
                else
                {
                    effects |= DataEffectKind.CreateDataset;
                    id = "create-dataset";
                    probe = "output-collision";
                    confidence = EvidenceConfidence.High;
                }
                approvalGroup = id;
            }
            else if (riskLevel == ToolRiskLevel.Modify)
            {
                effects |= governance?.ModifySubtype == ToolModifySubtype.SchemaOrSystemModify
                    ? DataEffectKind.AlterSchema
                    : DataEffectKind.UpdateRows;
                id = governance?.ModifySubtype switch
                {
                    ToolModifySubtype.QuantityGateCandidate => "row-overwrite",
                    ToolModifySubtype.SchemaOrSystemModify => "schema-or-system-modify",
                    ToolModifySubtype.SpecializedModify => "specialized-modify-unresolved",
                    ToolModifySubtype.ManualReviewModify => "manual-review-modify",
                    _ => "modify-unresolved"
                };
                probe = governance?.ModifySubtype == ToolModifySubtype.SchemaOrSystemModify
                    ? "schema"
                    : governance?.ModifySubtype == ToolModifySubtype.QuantityGateCandidate
                        ? "row-scope"
                        : "family-specific";
                approvalGroup = id;
                confidence = governance?.ModifySubtype == ToolModifySubtype.QuantityGateCandidate
                    ? EvidenceConfidence.High
                    : EvidenceConfidence.Low;
            }
            else if (riskLevel == ToolRiskLevel.Destructive)
            {
                effects |= safety.Capabilities.HasFlag(ToolSafetyCapability.ChangesSchema)
                    || lowerName.Contains("deletefield")
                    || lowerName.Contains("alterfield")
                        ? DataEffectKind.AlterSchema
                        : lowerName.Contains("truncate")
                          || lowerName.Contains("deleterow")
                          || lowerName.Contains("deletefeature")
                            ? DataEffectKind.DeleteRows
                            : DataEffectKind.DeleteDataset;
                id = effects.HasFlag(DataEffectKind.AlterSchema)
                    ? "destructive-schema"
                    : effects.HasFlag(DataEffectKind.DeleteRows)
                        ? "delete-rows"
                        : "delete-dataset";
                probe = effects.HasFlag(DataEffectKind.DeleteRows)
                    ? "row-scope"
                    : effects.HasFlag(DataEffectKind.AlterSchema)
                        ? "schema"
                        : "dataset-exists";
                approvalGroup = id;
                reversibility = ReversibilityKind.BackupRequired;
                backup = true;
            }

            return new EffectProfile
            {
                Id = id,
                Effects = effects,
                Reversibility = reversibility,
                RequiresInfluenceProbe =
                    governance?.ModifySubtype == ToolModifySubtype.QuantityGateCandidate
                    || effects.HasFlag(DataEffectKind.DeleteRows),
                RequiresBackup = backup,
                SingleWriterRequired = riskLevel != ToolRiskLevel.ReadOnly,
                ProbeKind = probe,
                ApprovalGroupKey = approvalGroup,
                Source = hasRiskOverride ? "local-risk-catalog+manifest" : "manifest",
                Confidence = confidence
            };
        }

        private static ToolSafetyProfile BuildSafetyProfile(
            ToolRiskLevel riskLevel,
            IReadOnlyList<string> outputParams,
            string toolName,
            ToolRiskCatalogEntry? riskOverride)
        {
            ToolSafetyCapability capabilities = ToolSafetyCapability.ReadsData;

            if (outputParams.Count > 0)
                capabilities |= ToolSafetyCapability.CreatesOutput;

            if (riskLevel == ToolRiskLevel.Destructive)
                capabilities |= ToolSafetyCapability.DeletesData;

            if (riskLevel == ToolRiskLevel.Modify)
                capabilities |= ToolSafetyCapability.MutatesExistingData | ToolSafetyCapability.RequiresReliableScope;

            if (toolName.Contains("AddField", StringComparison.OrdinalIgnoreCase) ||
                toolName.Contains("AlterField", StringComparison.OrdinalIgnoreCase) ||
                toolName.Contains("DeleteField", StringComparison.OrdinalIgnoreCase))
            {
                capabilities |= ToolSafetyCapability.ChangesSchema;
            }

            if (riskOverride.HasValue
                && riskOverride.Value.ModifySubtype == ToolModifySubtype.SchemaOrSystemModify)
            {
                capabilities |= ToolSafetyCapability.ChangesSchema;
            }

            return new ToolSafetyProfile
            {
                DeclaredRisk = riskLevel,
                MaxInternalRisk = riskLevel,
                Capabilities = capabilities
            };
        }

        private static ToolGovernanceMetadata? BuildGovernance(ToolRiskCatalogEntry? riskOverride)
        {
            if (!riskOverride.HasValue)
                return null;

            return new ToolGovernanceMetadata
            {
                ModifySubtype = riskOverride.Value.ModifySubtype,
                BucketReason = riskOverride.Value.BucketReason
            };
        }

        private static bool IsOutput(ToolManifestParam p)
        {
            string direction = p.Direction ?? "";
            string parameterType = p.ParameterType ?? "";
            string name = p.Name ?? "";
            return direction.Equals("Output", StringComparison.OrdinalIgnoreCase)
                || direction.Equals("Derived", StringComparison.OrdinalIgnoreCase)
                || parameterType.Equals("Derived", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("out_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("output", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("derived_", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsRequiredInput(ToolManifestParam p)
        {
            if (!string.IsNullOrWhiteSpace(p.ParameterType))
            {
                return p.ParameterType.Equals("Required", StringComparison.OrdinalIgnoreCase)
                    && !IsOutput(p);
            }

            return p.Required && !IsOutput(p);
        }

        private static List<string> InferInputParams(IEnumerable<ToolManifestParam> parameters)
        {
            return parameters
                .Where(p => !IsOutput(p))
                .Where(p => p.Name.StartsWith("in_", StringComparison.OrdinalIgnoreCase)
                    || p.Name.StartsWith("input", StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Name)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList();
        }

        private static List<string> InferOutputParams(IEnumerable<ToolManifestParam> parameters)
        {
            return parameters
                .Where(IsOutput)
                .Select(p => p.Name)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList();
        }

        private static ToolRiskLevel? NormalizeRiskLevel(string? level)
        {
            return ToolRiskMapper.TryParse(level, out var riskLevel)
                ? riskLevel
                : null;
        }

        /// <summary>
        /// ★ 安全门一致性（2026-07-24）：此方法在本地 manifest 条目缺少
        /// risk_level 时作为防御性回退被调用。
        /// 回退策略：未知工具归类为 Destructive（fail-safe），防止未评估工具被静默放行。
        /// 原关键词启发式（LooksDestructive/LooksModifying）已删除——它们与 catalog 分类
        /// 不一致，且作为死代码无法被测试覆盖。
        /// </summary>
        private static ToolRiskLevel InferRiskLevel(ToolManifestEntry entry, IReadOnlyList<string> outputParams)
        {
            AgentDiagnostics.Warn("DynamicToolLoader.InferRiskLevel",
                $"工具「{entry.Name}」在 manifest 中缺少 risk_level 字段。" +
                "默认归类为 Destructive（fail-safe）。" +
                "请在本地 tools_manifest.json 中添加 risk_level，或在本地风险目录中显式审计该工具。");
            return ToolRiskLevel.Destructive;
        }

        private static string SanitizeForMatch(string value) =>
            (value ?? "").Replace(" ", "").Replace("_", "").Replace("-", "").ToLowerInvariant();

        private static string TbNameToAlias(string tbName)
        {
            return tbName switch
            {
                "Analysis Tools.tbx" => "analysis",
                "Data Management Tools.tbx" => "management",
                "Conversion Tools.tbx" => "conversion",
                "Cartography Tools.tbx" => "cartography",
                "Spatial Analyst Tools.tbx" => "sa",
                "Spatial Statistics Tools.tbx" => "stats",
                "3D Analyst Tools.tbx" => "3d",
                "Editing Tools.tbx" => "editing",
                "Geostatistical Analyst Tools.tbx" => "ga",
                "Network Analyst Tools.tbx" => "na",
                "Linear Referencing Tools.tbx" => "lr",
                "Server Tools.tbx" => "server",
                "Geocoding Tools.tbx" => "geocoding",
                "Multidimension Tools.tbx" => "md",
                "Parcel Tools.tbx" => "parcel",
                "Trace Network Tools.tbx" => "tn",
                "Utility Network Tools.tbx" => "un",
                "Topographic Production Tools.tbx" => "topographic",
                "Location Referencing Tools.tbx" => "locref",
                "Territory Design Tools.tbx" => "td",
                "Space Time Pattern Mining Tools.tbx" => "stpm",
                "GeoAnalytics Tools.tbx" => "geoanalytics",
                "Image Analyst Tools.tbx" => "ia",
                "Business Analyst Tools.tbx" => "ba",
                "Aviation Tools.tbx" => "aviation",
                "Maritime Tools.tbx" => "maritime",
                "Defense Tools.tbx" => "defense",
                "Intelligence Tools.tbx" => "intelligence",
                "Indoors Tools.tbx" => "indoors",
                "Indoor Positioning Tools.tbx" => "indoorpositioning",
                "Knowledge Graph Tools.tbx" => "knowledge",
                "Data Reviewer Tools.tbx" => "reviewer",
                "Workflow Manager Tools.tbx" => "wfm",
                "Network Diagram Tools.tbx" => "nd",
                "Reality Mapping Tools.tbx" => "reality",
                "Bathymetry Tools.tbx" => "bathymetry",
                "Oriented Imagery Tools.tbx" => "oriented",
                "Public Transit Tools.tbx" => "transit",
                "GeoAI Tools.tbx" => "geoai",
                "Raster Analysis Tools.tbx" => "rasteranalysis",
                _ => tbName.Replace(" Tools.tbx", "").ToLower().Replace(" ", "_")
            };
        }

        /// <summary>Sanitize a tool name to alphanumeric + underscore for Function Calling.</summary>
        private static string SanitizeFunctionName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "unknown_tool";
            var sb = new System.Text.StringBuilder();
            bool lastWasUnderscore = false;
            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c) || c == '_')
                {
                    sb.Append(c);
                    lastWasUnderscore = c == '_';
                }
                else if (!lastWasUnderscore)
                {
                    sb.Append('_');
                    lastWasUnderscore = true;
                }
            }
            string result = sb.ToString().Trim('_');
            // Prepend underscore if starts with digit
            if (result.Length > 0 && char.IsDigit(result[0]))
                result = "_" + result;
            return string.IsNullOrEmpty(result) ? "unknown_tool" : result;
        }
    }
}
