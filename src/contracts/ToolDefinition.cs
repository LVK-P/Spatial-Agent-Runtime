using Newtonsoft.Json;

namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Full tool definition — name, description, category, parameters, verification, annotations.
    /// Used by ToolSchemaRegistry and SchemaGPToolAdapter for precise Function Calling schemas.
    /// </summary>
    /// <remarks>中文：完整工具定义——名称、描述、类别、参数、验证、注释。ToolSchemaRegistry 和 SchemaGPToolAdapter 用于精确的函数调用模式。</remarks>
    public class ToolDefinition
    {
        [JsonProperty("name")] public string Name { get; set; } = "";
        /// <summary>Safe name for LLM Function Calling (alphanumeric + underscore only).</summary>
        /// <remarks>中文：LLM 函数调用的安全名称（仅字母数字 + 下划线）。</remarks>
        [JsonProperty("function_name", NullValueHandling = NullValueHandling.Ignore)]
        public string? FunctionName { get; set; }
        /// <summary>Human-readable display name (may contain spaces and Unicode).</summary>
        /// <remarks>中文：可读显示名称（可能包含空格和Unicode）。</remarks>
        [JsonProperty("display_name", NullValueHandling = NullValueHandling.Ignore)]
        public string? DisplayName { get; set; }
        /// <summary>Dot-format command name for ArcGIS Pro GP execution (e.g. analysis.Buffer3D).</summary>
        /// <remarks>中文：ArcGIS Pro GP 执行的点格式命令名称（例如分析。Buffer3D）。</remarks>
        [JsonProperty("execution_name", NullValueHandling = NullValueHandling.Ignore)]
        public string? ExecutionName { get; set; }
        [JsonProperty("description")] public string Description { get; set; } = "";
        [JsonProperty("category")] public string Category { get; set; } = "gp";
        [JsonProperty("destructive")] public bool Destructive { get; set; }
        /// <summary>Three-tier risk level. SafetyGate.Classify() is the single source of truth at runtime.</summary>
        /// <remarks>中文：三级风险等级。SafetyGate.Classify（） 是运行时唯一的真实来源。</remarks>
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
        /// <remarks>中文：位置参数转换的显式参数顺序。如果为空，则会退回到 Parameters.Properties.Keys。</remarks>
        [JsonProperty("parameter_order", NullValueHandling = NullValueHandling.Ignore)]
        public List<string>? ParameterOrder { get; set; }
        /// <summary>Input parameters from the ArcGIS parameter contract.</summary>
        /// <remarks>中文：输入参数来自ArcGIS参数合同。</remarks>
        [JsonProperty("input_params", NullValueHandling = NullValueHandling.Ignore)]
        public List<string>? InputParams { get; set; }
        /// <summary>Output or derived output parameters from the ArcGIS parameter contract.</summary>
        /// <remarks>中文：输出或衍生的 ArcGIS 参数合同中的参数。</remarks>
        [JsonProperty("output_params", NullValueHandling = NullValueHandling.Ignore)]
        public List<string>? OutputParams { get; set; }
        /// <summary>Primary output parameter used for output path normalization and validation.</summary>
        /// <remarks>中文：主要输出参数，用于输出路径归一化和验证。</remarks>
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
        // 中文：校验元数据（可从本地 tools_manifest.json 生成） 第一阶段：data_type、is_spatial_ref、is_path 从清单填充。第二阶段：min_value、max_value、tools_constraints.json的图案（手工策展）。

        /// <summary>ArcGIS GP parameter data type: GPString, GPLong, GPDouble, GPFeatureLayer…</summary>
        /// <remarks>中文：ArcGIS GP 参数数据类型：GPString、GPLong、GPDouble、GPFeatureLayer......</remarks>
        [JsonProperty("data_type", NullValueHandling = NullValueHandling.Ignore)]
        public string? DataType { get; set; }

        /// <summary>Minimum allowed numeric value (Phase 2 — needs tools_constraints.json).</summary>
        /// <remarks>中文：最低允许的数值（第二阶段——需要tools_constraints.json）。</remarks>
        [JsonProperty("min_value", NullValueHandling = NullValueHandling.Ignore)]
        public double? MinValue { get; set; }

        /// <summary>Maximum allowed numeric value (Phase 2 — needs tools_constraints.json).</summary>
        /// <remarks>中文：最大允许数值（第二阶段——需要tools_constraints.json）。</remarks>
        [JsonProperty("max_value", NullValueHandling = NullValueHandling.Ignore)]
        public double? MaxValue { get; set; }

        /// <summary>Regex pattern for path/name validation (Phase 2).</summary>
        /// <remarks>中文：用于路径/名称验证的正则表达式模式（第二阶段）。</remarks>
        [JsonProperty("pattern", NullValueHandling = NullValueHandling.Ignore)]
        public string? Pattern { get; set; }

        /// <summary>True if this parameter accepts a spatial reference (WKID or prj path).</summary>
        /// <remarks>中文：如果该参数接受空间参考（如WKID或prj路径），则为真。</remarks>
        [JsonProperty("is_spatial_ref", NullValueHandling = NullValueHandling.Ignore)]
        public bool IsSpatialRef { get; set; }

        /// <summary>True if this parameter is a filesystem path (needs normalization).</summary>
        /// <remarks>中文：如果该参数是文件系统路径（需要规范化），则为真。</remarks>
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
