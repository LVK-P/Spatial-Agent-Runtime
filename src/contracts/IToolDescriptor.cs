using System.Collections.Generic;
using System.Threading;

namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Tool annotation — behavioral metadata consumed by Planner and Safety layer.
    /// </summary>
    public enum ToolAnnotation
    {
        ReadOnly,
        /// <summary>Creates new data without overwriting existing field values (Buffer, Clip, Statistics).</summary>
        Increment,
        /// <summary>Overwrites existing field values (CalculateField, cc_calculate_area on re-run).</summary>
        Modify,
        Destructive,
        Idempotent
    }

    [Flags]
    public enum ToolSafetyCapability
    {
        None = 0,
        ReadsData = 1 << 0,
        CreatesOutput = 1 << 1,
        MutatesExistingData = 1 << 2,
        DeletesData = 1 << 3,
        ChangesSchema = 1 << 4,
        Composite = 1 << 5,
        RequiresReliableScope = 1 << 6
    }

    public sealed class ToolSafetyProfile
    {
        public ToolRiskLevel DeclaredRisk { get; init; } = ToolRiskLevel.Modify;
        public ToolRiskLevel MaxInternalRisk { get; init; } = ToolRiskLevel.Modify;
        public ToolSafetyCapability Capabilities { get; init; } = ToolSafetyCapability.None;

        public bool NeedsBackup => DeclaredRisk == ToolRiskLevel.Destructive
            || Capabilities.HasFlag(ToolSafetyCapability.DeletesData);

        public bool RequiresReliableScope =>
            Capabilities.HasFlag(ToolSafetyCapability.RequiresReliableScope);
    }

    public enum ToolModifySubtype
    {
        Unknown = 0,
        QuantityGateCandidate,
        SchemaOrSystemModify,
        SpecializedModify,
        ManualReviewModify
    }

    public sealed class ToolGovernanceMetadata
    {
        public ToolModifySubtype ModifySubtype { get; init; } = ToolModifySubtype.Unknown;
        public string BucketReason { get; init; } = "";
    }

    /// <summary>
    /// Runtime-only safety metadata. This is consumed by runtime policy and safety
    /// gates, not by the prompt-layer tool schema.
    /// </summary>
    public interface IToolSafetyDescriptor
    {
        ToolSafetyProfile SafetyProfile { get; }
    }

    public interface IToolGovernanceDescriptor
    {
        ToolGovernanceMetadata Governance { get; }
    }

    public interface IToolEffectDescriptor
    {
        EffectProfile EffectProfile { get; }
    }

    /// <summary>
    /// A stable logical capability declared by a tool. Applicability metadata is
    /// intentionally independent from a user request so capability discovery can
    /// scale through manifests and domain packs instead of tool-name conditionals.
    /// </summary>
    public sealed class ToolCapabilityClaim
    {
        public string CapabilityId { get; init; } = "";
        public IReadOnlyList<string> ApplicableGeometryTypes { get; init; } =
            Array.Empty<string>();
        public bool RequiresPostconditionEvidence { get; init; }
        public string Evidence { get; init; } = "tool-declaration";
        public double Confidence { get; init; } = 1;
    }

    /// <summary>
    /// Optional semantic capability metadata. Safety metadata remains in
    /// IToolSafetyDescriptor; a capability claim can never downgrade risk.
    /// </summary>
    public interface IToolCapabilityDescriptor
    {
        IReadOnlyList<ToolCapabilityClaim> CapabilityClaims { get; }
    }

    public static class ToolRiskMapper
    {
        public static ToolRiskLevel FromAnnotation(ToolAnnotation annotation) => annotation switch
        {
            ToolAnnotation.ReadOnly => ToolRiskLevel.ReadOnly,
            ToolAnnotation.Idempotent => ToolRiskLevel.ReadOnly,
            ToolAnnotation.Increment => ToolRiskLevel.Increment,
            ToolAnnotation.Modify => ToolRiskLevel.Modify,
            ToolAnnotation.Destructive => ToolRiskLevel.Destructive,
            _ => ToolRiskLevel.Modify
        };

        public static ToolAnnotation ToAnnotation(ToolRiskLevel riskLevel) => riskLevel switch
        {
            ToolRiskLevel.ReadOnly => ToolAnnotation.ReadOnly,
            ToolRiskLevel.Increment => ToolAnnotation.Increment,
            ToolRiskLevel.Modify => ToolAnnotation.Modify,
            ToolRiskLevel.Destructive => ToolAnnotation.Destructive,
            _ => ToolAnnotation.Modify
        };

        public static bool TryParse(string? value, out ToolRiskLevel riskLevel)
        {
            riskLevel = ToolRiskLevel.Modify;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            switch (value.Trim().ToLowerInvariant())
            {
                case "readonly":
                case "read_only":
                case "read-only":
                case "read":
                    riskLevel = ToolRiskLevel.ReadOnly;
                    return true;
                case "modify":
                case "write":
                case "writes":
                    riskLevel = ToolRiskLevel.Modify;
                    return true;
                case "increment":
                case "create":
                case "creates":
                    riskLevel = ToolRiskLevel.Increment;
                    return true;
                case "destructive":
                case "delete":
                case "danger":
                    riskLevel = ToolRiskLevel.Destructive;
                    return true;
                default:
                    return false;
            }
        }

        public static ToolRiskLevel FromSerialized(
            string? value,
            bool destructiveFallback,
            ToolRiskLevel defaultRisk = ToolRiskLevel.Modify)
        {
            if (TryParse(value, out var parsed))
                return parsed;

            return destructiveFallback ? ToolRiskLevel.Destructive : defaultRisk;
        }

        public static string ToSerializedName(ToolRiskLevel riskLevel) => riskLevel.ToString();
    }

    public static class ToolGovernanceMapper
    {
        public static ToolModifySubtype ParseModifySubtype(string? value) => value?.Trim() switch
        {
            "QuantityGateCandidate" => ToolModifySubtype.QuantityGateCandidate,
            "SchemaOrSystemModify" => ToolModifySubtype.SchemaOrSystemModify,
            "SpecializedModify" => ToolModifySubtype.SpecializedModify,
            "ManualReviewModify" => ToolModifySubtype.ManualReviewModify,
            _ => ToolModifySubtype.Unknown
        };
    }

    /// <summary>
    /// Describes a tool to the LLM for Function Calling.
    /// Pure data object — no execution logic.
    /// </summary>
    public interface IToolDescriptor
    {
        string Name { get; }
        string Description { get; }
        string Category { get; }
        ToolAnnotation Annotation { get; }
        IReadOnlyDictionary<string, ParameterProperty> Parameters { get; }
        IReadOnlyList<string> RequiredParameters { get; }
        /// <summary>Verification rule for post-condition checking. Null = use GPToolContract or skip.</summary>
        VerificationRule? Verification { get; }

        /// <summary>
        /// Default parameter values filled by the tool schema when the LLM omits them.
        /// Keys are parameter names (e.g. "field_name", "unit"), values are the defaults.
        /// Used by Scheduler.ResolveArgs to distinguish LLM-provided vs tool-default args.
        /// Null = no defaults declared for this tool.
        /// </summary>
        IReadOnlyDictionary<string, string>? DefaultParameters { get; }
    }

    /// <summary>
    /// Optional data-flow metadata for tools whose manifests distinguish inputs and outputs.
    /// Scheduler uses this before falling back to name-based heuristics.
    /// </summary>
    public interface IToolDataflowDescriptor
    {
        IReadOnlyList<string> InputParameters { get; }
        IReadOnlyList<string> OutputParameters { get; }
        string? PrimaryOutputParameter { get; }
    }

    /// <summary>
    /// Optional three-name identity supplied by dynamically discovered ArcGIS
    /// tools. Plans persist all identities so UI/audit records do not drift
    /// when the model used an alias.
    /// </summary>
    public interface IToolIdentityDescriptor
    {
        string FunctionName { get; }
        string DisplayName { get; }
        string ExecutionName { get; }
    }

    /// <summary>
    /// Executes a tool. Called by Scheduler at runtime.
    /// Separate from Descriptor so LLM never sees execution logic.
    /// </summary>
    public interface IToolExecutor
    {
        /// <summary>
        /// Execute a tool call with cancellation support.
        /// Default ct=default preserves source compatibility with existing implementations.
        /// Note: binary-incompatible change — all IToolExecutor implementations must be recompiled.
        /// </summary>
        Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken ct = default);
    }

    /// <summary>
    /// Combined: a tool that can both describe and execute itself.
    /// Implementations register with ToolRegistry.
    /// </summary>
    public interface ITool : IToolDescriptor, IToolExecutor { }

    /// <summary>
    /// LLM-generated tool call.
    /// </summary>
    public class ToolCall
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public Dictionary<string, object?> Arguments { get; set; } = new();
    }

    /// <summary>
    /// Tool execution result.
    /// </summary>
    public class ToolResult
    {
        public string ToolName { get; set; } = "";
        public bool Success { get; set; }
        public string? Output { get; set; }
        public string? Error { get; set; }
        public int Attempts { get; set; } = 1;
        public string VerificationDetail { get; set; } = "";
        public List<string> AutofixApplied { get; set; } = new();
        /// <summary>Typed output for structured Observation. Tools set this directly to skip JSON parsing.</summary>
        public ObservationOutput? StructuredOutput { get; set; }
    }
}
