using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using SpatialAgent.Adapters.ArcGIS;
using SpatialAgent.Runtime.Contracts;
using SpatialAgent.Core;

namespace SpatialAgent.Tools.Core
{
    public class SchemaGPToolAdapter : ITool, IToolDataflowDescriptor, IWorkspaceAwareTool,
        IToolSafetyDescriptor, IToolGovernanceDescriptor, IToolEffectDescriptor,
        IToolIdentityDescriptor, IToolCapabilityDescriptor
    {
        private readonly GPToolRunner _runner = new();
        private readonly ToolDefinition _def;
        private readonly ToolSchemaRegistry _registry;
        private IWorkspaceService? _workspace;

        public string Name => _def.Name;
        public string FunctionName => _def.FunctionName ?? _def.Name;
        public string DisplayName => _def.DisplayName ?? _def.Name;
        public string ExecutionName => _def.ExecutionName ?? _def.Name;
        public string Description => _def.Description;
        public string Category => _def.Category;
        /// <summary>
        /// ★ 安全门一致性（2026-07-24）：Annotation 直接从 ToolDefinition.RiskLevel 派生。
        /// 动态工具风险来自本地 manifest，并可由本地审计目录覆盖。
        /// 删除了 IsIncrementTool() 关键词启发式——它曾使 27 个 Modify 工具的 Annotation
        /// 错标为 Increment（如 management_AddField 被 "addfield" 关键词误判）。
        /// </summary>
        /// <remarks>English：Security Gate Consistency (2026-07-24): Annotation is derived directly from ToolDefinition.RiskLevel. Dynamic tool risks originate from local manifests and can be covered by local audit directories. Removed the IsIncrementTool() keyword heuristic—it had caused 27 Modify tools to mislabel Annotations as Increment (for example, management_AddField was misjudged by the "addfield" keyword).</remarks>
        public ToolAnnotation Annotation =>
            ToolRiskMapper.ToAnnotation(_def.RiskLevel ?? ToolRiskLevel.Modify);
        public IReadOnlyDictionary<string, ParameterProperty> Parameters => _def.Parameters.Properties;
        public IReadOnlyList<string> RequiredParameters => _def.Parameters.Required;
        public IReadOnlyList<string> InputParameters => ResolveInputParameters();
        public IReadOnlyList<string> OutputParameters => ResolveOutputParameters();
        public string? PrimaryOutputParameter =>
            !string.IsNullOrWhiteSpace(_def.PrimaryOutput)
                ? _def.PrimaryOutput
                : OutputParameters.FirstOrDefault();
        public VerificationRule? Verification => _def.Verification;
        public ToolSafetyProfile SafetyProfile => _def.SafetyProfile ?? BuildSafetyProfile();
        public ToolGovernanceMetadata Governance => _def.Governance ?? new ToolGovernanceMetadata();
        public EffectProfile EffectProfile =>
            _def.EffectProfile ?? DefaultInvocationEffectResolver.DeriveProfile(this);
        public IReadOnlyList<ToolCapabilityClaim> CapabilityClaims =>
            _def.CapabilityClaims is { } claims
                ? claims
                : Array.Empty<ToolCapabilityClaim>();

        /// <summary>
        /// Default parameter values declared by the tool schema.
        /// Used by Scheduler.ResolveArgs to fill parameters the LLM omitted.
        /// Only declared for high-priority tools with known safe defaults.
        /// </summary>
        /// <remarks>中文：工具模式声明的默认参数值。Scheduler.ResolveArgs 用于填充 LLM 遗漏的参数。仅为具有已知安全默认的高优先级工具声明。</remarks>
        public IReadOnlyDictionary<string, string>? DefaultParameters => GetDefaultParameters();

        private IReadOnlyDictionary<string, string>? GetDefaultParameters()
        {
            string n = Name.ToLowerInvariant();
            var defaults = _def.Parameters.Properties
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Value.DefaultValue)
                             && (_def.OutputParams?.Contains(kv.Key, StringComparer.OrdinalIgnoreCase) != true))
                .ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value.DefaultValue!,
                    StringComparer.OrdinalIgnoreCase);

            // cc_calculate_area: default field_name="TQMJ", unit="平方米", digit="2"
            // 中文：cc_calculate_area： 默认 field_name=“TQMJ”，unit=“平方米”，数字=“2”
            if (n.Contains("calculate_area"))
            {
                defaults["field_name"] = "TQMJ";
                defaults["unit"] = "平方米";
                defaults["digit"] = "2";
            }

            // cc_calculate_length: default field_name="DXCD", unit="米", digit="2"
            // 中文：cc_calculate_length：默认field_name=“DXCD”，单元=“米”，数字=“2”
            if (n.Contains("calculate_length"))
            {
                defaults["field_name"] = "DXCD";
                defaults["unit"] = "米";
                defaults["digit"] = "2";
            }

            // cc_map_field_values: no defaults (mapping_source must be LLM-provided)
            // Other tools: no defaults declared
            // 中文：cc_map_field_values：无默认设置（mapping_source必须由LLM提供） 其他工具：无声明默认设置
            return defaults.Count == 0 ? null : defaults;
        }

        private ToolSafetyProfile BuildSafetyProfile()
        {
            ToolRiskLevel declaredRisk = _def.RiskLevel
                ?? (_def.Destructive ? ToolRiskLevel.Destructive : ToolRiskLevel.Modify);

            ToolSafetyCapability capabilities = ToolSafetyCapability.ReadsData;

            if (_def.OutputParams != null && _def.OutputParams.Count > 0)
                capabilities |= ToolSafetyCapability.CreatesOutput;

            if (_def.Destructive || declaredRisk == ToolRiskLevel.Destructive)
                capabilities |= ToolSafetyCapability.DeletesData;

            if (declaredRisk == ToolRiskLevel.Modify && (_def.OutputParams == null || _def.OutputParams.Count == 0))
                capabilities |= ToolSafetyCapability.MutatesExistingData | ToolSafetyCapability.RequiresReliableScope;

            if (Name.Contains("AddField", StringComparison.OrdinalIgnoreCase) ||
                Name.Contains("AlterField", StringComparison.OrdinalIgnoreCase) ||
                Name.Contains("DeleteField", StringComparison.OrdinalIgnoreCase))
            {
                capabilities |= ToolSafetyCapability.ChangesSchema;
            }

            return new ToolSafetyProfile
            {
                DeclaredRisk = declaredRisk,
                MaxInternalRisk = declaredRisk,
                Capabilities = capabilities
            };
        }

        public SchemaGPToolAdapter(string name, ToolDefinition def, ToolSchemaRegistry registry, IWorkspaceService? workspace = null)
        { _def = def; _registry = registry; _workspace = workspace; }

        /// <summary>Phase-2 retrofit: workspace becomes available after project init.</summary>
        /// <remarks>中文：第二阶段改造：项目启动后工作区开放。</remarks>
        public void AttachWorkspace(IWorkspaceService workspace) => _workspace = workspace;

        public async System.Threading.Tasks.Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken ct = default)
        {
            var r = new ToolResult { ToolName = Name };
            try
            {
                // ★ Prefer ToolDefinition.ParameterOrder (explicit order from manifest)
                // Falls back to GPToolContract lookup for legacy tools
                // 中文：Prefer ToolDefinition.ParameterOrder（显式命令来自manifest） 退回到GPToolContract查找遗留工具
                var posArgs = ConvertToPositionalArgs(Name, call.Arguments);

                // ★ Resolve output paths: if output is a bare name, use in_memory/
                // 中文：解析输出路径：如果输出是裸名称，使用 in_memory/
                var contract = GPToolContract.Get(Name);
                NormalizePrimaryOutputPath(posArgs, contract);

                // ★ Use ExecutionName if set (dynamic tools), otherwise convert from schema name
                // 中文：如果设置了 ExecutionName（动态工具），否则从 schema name 转换
                string gpName = _def.ExecutionName ?? GPToolContract.ToDotName(Name);
                string raw = await _runner.ExecuteAsync(gpName, posArgs, ct);
                var parsed = JsonConvert.DeserializeObject<dynamic>(raw);
                bool ok = parsed?.success == true; r.Success = ok; r.Output = raw;
                if (!ok) { r.Error = parsed?.summary?.ToString() ?? parsed?.error?.ToString() ?? "失败"; return r; }
                r.VerificationDetail = contract != null
                    ? string.Join(", ", contract.DefaultVerifications.Select(v => v.Type))
                    : "";
                return r;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return new ToolResult { ToolName = Name, Success = false, Error = ex.Message }; }
        }

        /// <summary>Convert named args to positional using explicit ParameterOrder or GPToolContract fallback.</summary>
        /// <remarks>中文：使用显式ParameterOrder或GPToolContract的备用将命名arg转换为位置变量。</remarks>
        private List<string> ConvertToPositionalArgs(string toolName, Dictionary<string, object?> namedArgs)
        {
            // 1. Use ToolDefinition.ParameterOrder (explicit, from manifest)
            // 中文：1. 使用 ToolDefinition.ParameterOrder（显式，来自 manifest）
            if (_def.ParameterOrder != null && _def.ParameterOrder.Count > 0)
            {
                var pos = new List<string>();
                foreach (string key in _def.ParameterOrder)
                {
                    pos.Add(TryGetArg(namedArgs, key, out object? val) ? val?.ToString() ?? "" : "");
                }
                TrimTrailingEmptyOptionalArgs(pos);
                return pos;
            }

            // 2. Fallback: ToolSchemaRegistry/GPToolContract lookup
            // 中文：2. 备用：ToolSchemaRegistry/GPToolContract 查询
            return _registry.ConvertToPositionalArgs(toolName, namedArgs);
        }

        private void NormalizePrimaryOutputPath(List<string> posArgs, GPToolContract? contract)
        {
            int outputIndex = -1;

            string? outputKey = !string.IsNullOrWhiteSpace(_def.PrimaryOutput)
                ? _def.PrimaryOutput
                : _def.OutputParams?.FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(outputKey) && _def.ParameterOrder != null)
                outputIndex = _def.ParameterOrder.FindIndex(p => p.Equals(outputKey, StringComparison.OrdinalIgnoreCase));

            if (outputIndex < 0 && contract != null)
                outputIndex = contract.OutputIndex;

            if (outputIndex < 0 || outputIndex >= posArgs.Count)
                return;

            posArgs[outputIndex] = NormalizeOutputPath(posArgs[outputIndex]);
        }

        private IReadOnlyList<string> ResolveInputParameters()
        {
            if (_def.InputParams != null && _def.InputParams.Count > 0)
                return _def.InputParams.AsReadOnly();

            var contract = GPToolContract.Get(Name);
            if (contract != null &&
                contract.InputIndex >= 0 &&
                contract.InputIndex < contract.ParameterOrder.Length)
            {
                return new[] { contract.ParameterOrder[contract.InputIndex] };
            }

            return _def.Parameters.Properties.Keys
                .Where(k => !ParameterNameClassifier.IsOutputParameter(k))
                .Where(ParameterNameClassifier.IsLikelyInputParameter)
                .ToList()
                .AsReadOnly();
        }

        private IReadOnlyList<string> ResolveOutputParameters()
        {
            if (_def.OutputParams != null && _def.OutputParams.Count > 0)
                return _def.OutputParams.AsReadOnly();

            if (!string.IsNullOrWhiteSpace(_def.PrimaryOutput))
                return new[] { _def.PrimaryOutput };

            var contract = GPToolContract.Get(Name);
            if (contract != null &&
                contract.OutputIndex >= 0 &&
                contract.OutputIndex < contract.ParameterOrder.Length)
            {
                return new[] { contract.ParameterOrder[contract.OutputIndex] };
            }

            return _def.Parameters.Properties.Keys
                .Where(ParameterNameClassifier.IsOutputParameter)
                .ToList()
                .AsReadOnly();
        }

        private string NormalizeOutputPath(string outputVal)
        {
            if (string.IsNullOrWhiteSpace(outputVal))
                return outputVal;

            // Already a qualified path — leave as-is.
            // 中文：这已经是一条合格的路径——保持现状。
            if (outputVal.Contains('\\') || outputVal.Contains('/')
                || outputVal.Contains(".gdb", StringComparison.OrdinalIgnoreCase)
                || outputVal.StartsWith("in_memory"))
                return outputVal;

            // Route through workspace if available.
            // 中文：如果有，就通过工作区路由。
            if (_workspace != null)
                return _workspace.ResolveOutputPath(outputVal, _def.Name);

            return "in_memory/" + outputVal;
        }

        private void TrimTrailingEmptyOptionalArgs(List<string> pos)
        {
            if (_def.ParameterOrder == null)
                return;

            while (pos.Count > 0)
            {
                string key = _def.ParameterOrder[pos.Count - 1];
                bool required = _def.Parameters.Required.Any(r => r.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (required || !string.IsNullOrWhiteSpace(pos[^1]))
                    break;

                pos.RemoveAt(pos.Count - 1);
            }
        }

        private static bool TryGetArg(Dictionary<string, object?> args, string key, out object? value)
        {
            if (args.TryGetValue(key, out value))
                return true;

            foreach (var kv in args)
            {
                if (kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    value = kv.Value;
                    return true;
                }
            }

            value = null;
            return false;
        }
    }
}
