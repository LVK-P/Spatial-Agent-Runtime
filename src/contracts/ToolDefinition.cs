using Newtonsoft.Json;

namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Full tool definition — name, description, category, parameters, verification, annotations.
    /// Used by ToolSchemaRegistry and SchemaGPToolAdapter for precise Function Calling schemas.
    /// </summary>
    public class ToolDefinition
    {
        [JsonProperty("name")] public string Name { get; set; } = "";
        /// <summary>Safe name for LLM Function Calling (alphanumeric + underscore only).</summary>
        [JsonProperty("function_name", NullValueHandling = NullValueHandling.Ignore)]
        public string? FunctionName { get; set; }
        /// <summary>Human-readable display name (may contain spaces and Unicode).</summary>
        [JsonProperty("display_name", NullValueHandling = NullValueHandling.Ignore)]
        public string? DisplayName { get; set; }
        /// <summary>Dot-format command name for ArcGIS Pro GP execution (e.g. analysis.Buffer3D).</summary>
        [JsonProperty("execution_name", NullValueHandling = NullValueHandling.Ignore)]
        public string? ExecutionName { get; set; }
        [JsonProperty("description")] public string Description { get; set; } = "";
        [JsonProperty("category")] public string Category { get; set; } = "gp";
        [JsonProperty("destructive")] public bool Destructive { get; set; }
        /// <summary>Three-tier risk level. SafetyGate.Classify() is the single source of truth at runtime.</summary>
        [JsonProperty("risk_level", NullValueHandling = NullValueHandling.Ignore)]
        public ToolRiskLevel? RiskLevel { get; set; }
        [JsonProperty("safety_profile", NullValueHandling = NullValueHandling.Ignore)]
        public ToolSafetyProfile? SafetyProfile { get; set; }
        [JsonProperty("governance", NullValueHandling = NullValueHandling.Ignore)]
        public ToolGovernanceMetadata? Governance { get; set; }
        [JsonProperty("effect_profile", NullValueHandling = NullValueHandling.Ignore)]
        public EffectProfile? EffectProfile { get; set; }
        [JsonProperty("capability_claims", NullValueHandling = NullValueHandling.Ignore)]
        public List<ToolCapabilityClaim>? CapabilityClaims { get; set; }
        [JsonProperty("verification")] public VerificationRule? Verification { get; set; }
        [JsonProperty("parameters")] public ToolParameters Parameters { get; set; } = new();
        /// <summary>Explicit parameter order for positional argument conversion. Falls back to Parameters.Properties.Keys if null.</summary>
        [JsonProperty("parameter_order", NullValueHandling = NullValueHandling.Ignore)]
        public List<string>? ParameterOrder { get; set; }
        /// <summary>Input parameters from the ArcGIS parameter contract.</summary>
        [JsonProperty("input_params", NullValueHandling = NullValueHandling.Ignore)]
        public List<string>? InputParams { get; set; }
        /// <summary>Output or derived output parameters from the ArcGIS parameter contract.</summary>
        [JsonProperty("output_params", NullValueHandling = NullValueHandling.Ignore)]
        public List<string>? OutputParams { get; set; }
        /// <summary>Primary output parameter used for output path normalization and validation.</summary>
        [JsonProperty("primary_output", NullValueHandling = NullValueHandling.Ignore)]
        public string? PrimaryOutput { get; set; }
    }

    public class ToolParameters
    {
        [JsonProperty("type")] public string Type { get; set; } = "object";
        [JsonProperty("properties")] public Dictionary<string, ParameterProperty> Properties { get; set; } = new();
        [JsonProperty("required")] public List<string> Required { get; set; } = new();
    }

    public class ParameterProperty
    {
        [JsonProperty("type")] public string Type { get; set; } = "string";
        [JsonProperty("description")] public string Description { get; set; } = "";
        [JsonProperty("enum", NullValueHandling = NullValueHandling.Ignore)]
        public List<string>? Enum { get; set; }

        // ── 校验元数据（可从本地 tools_manifest.json 生成）──
        // Phase 1: data_type, is_spatial_ref, is_path are populated from manifest.
        // Phase 2: min_value, max_value, pattern from tools_constraints.json (manual curation).

        /// <summary>ArcGIS GP parameter data type: GPString, GPLong, GPDouble, GPFeatureLayer…</summary>
        [JsonProperty("data_type", NullValueHandling = NullValueHandling.Ignore)]
        public string? DataType { get; set; }

        /// <summary>Minimum allowed numeric value (Phase 2 — needs tools_constraints.json).</summary>
        [JsonProperty("min_value", NullValueHandling = NullValueHandling.Ignore)]
        public double? MinValue { get; set; }

        /// <summary>Maximum allowed numeric value (Phase 2 — needs tools_constraints.json).</summary>
        [JsonProperty("max_value", NullValueHandling = NullValueHandling.Ignore)]
        public double? MaxValue { get; set; }

        /// <summary>Regex pattern for path/name validation (Phase 2).</summary>
        [JsonProperty("pattern", NullValueHandling = NullValueHandling.Ignore)]
        public string? Pattern { get; set; }

        /// <summary>True if this parameter accepts a spatial reference (WKID or prj path).</summary>
        [JsonProperty("is_spatial_ref", NullValueHandling = NullValueHandling.Ignore)]
        public bool IsSpatialRef { get; set; }

        /// <summary>True if this parameter is a filesystem path (needs normalization).</summary>
        [JsonProperty("is_path", NullValueHandling = NullValueHandling.Ignore)]
        public bool IsPath { get; set; }

        [JsonProperty("default_value", NullValueHandling = NullValueHandling.Ignore)]
        public string? DefaultValue { get; set; }

        [JsonProperty("semantic_role", NullValueHandling = NullValueHandling.Ignore)]
        public string? SemanticRole { get; set; }

        [JsonProperty("is_field", NullValueHandling = NullValueHandling.Ignore)]
        public bool IsField { get; set; }

        [JsonProperty("is_where_clause", NullValueHandling = NullValueHandling.Ignore)]
        public bool IsWhereClause { get; set; }

        [JsonProperty("multi_value", NullValueHandling = NullValueHandling.Ignore)]
        public bool MultiValue { get; set; }
    }
}
