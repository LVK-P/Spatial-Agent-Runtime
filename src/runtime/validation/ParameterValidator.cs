using System;
using System.Collections.Generic;
using System.Linq;
using SpatialAgent.Runtime.Contracts;
using SpatialAgent.Core;

// ================================================================
// ParameterValidator.cs — 工具参数预执行校验器
//
// 在 Scheduler 执行每个 ToolCall 前校验参数：
// ① 必填参数检查 ② 类型校验 ③ 枚举值模糊匹配（通过 IEnumAliasProvider）
// ④ 空间参考参数校验 ⑤ 数值范围校验（Phase 2）
//
// 纯逻辑，零 ArcGIS 依赖 — 可在 Runtime.Tests 中直接测试。
//
// 来源：跨实现架构审查后的参数验证边界。
// ================================================================
namespace SpatialAgent.Runtime.Validation
{
    /// <summary>
    /// Pre-execution parameter validator.
    /// Validates LLM-generated tool calls against ToolDefinition schemas.
    /// Key insight: GIS enum values are case-sensitive; LLM often produces
    /// lowercase or aliased variants. IEnumAliasProvider corrects these
    /// before they hit the expensive GP execution layer.
    /// </summary>
    public class ParameterValidator
    {
        private readonly Dictionary<string, ToolDefinition> _schemas;
        private readonly IEnumAliasProvider? _aliasProvider;

        public ParameterValidator(
            IEnumerable<ToolDefinition> schemas,
            IEnumAliasProvider? aliasProvider = null)
        {
            _schemas = schemas.ToDictionary(
                s => s.Name, StringComparer.OrdinalIgnoreCase);
            _aliasProvider = aliasProvider;
        }

        public ParameterValidator(
            IEnumerable<IToolDescriptor> descriptors,
            IEnumAliasProvider? aliasProvider = null)
            : this(descriptors.Select(ToDefinition), aliasProvider)
        {
        }

        /// <summary>Validate a tool call. Returns corrected args or errors.</summary>
        public ParameterValidationResult Validate(ToolCall call)
        {
            var result = new ParameterValidationResult
            {
                Action = ValidationAction.Proceed,
                CorrectedArgs = new Dictionary<string, object?>(call.Arguments)
            };

            if (!_schemas.TryGetValue(call.Name, out var schema))
            {
                result.Action = ValidationAction.Rejected;
                result.Errors.Add($"未知工具: {call.Name}");
                return result;
            }

            // ① Required parameter check
            foreach (var req in schema.Parameters.Required)
            {
                if (!result.CorrectedArgs.ContainsKey(req) ||
                    result.CorrectedArgs[req] == null)
                {
                    result.Errors.Add($"缺少必填参数: {req}");
                }
            }

            // ② Type + range validation (per-parameter)
            foreach (var (name, prop) in schema.Parameters.Properties)
            {
                if (!result.CorrectedArgs.TryGetValue(name, out var val) || val == null)
                    continue;

                // Numeric range (Phase 2 — only active when data is available)
                if (prop.MinValue.HasValue || prop.MaxValue.HasValue)
                    ValidateNumericRange(name, val, prop, result);

                // Numeric type check for GP numeric types
                if (IsNumericDataType(prop.DataType))
                    ValidateNumericType(name, val, result);
            }

            // ③ Enum validation with alias resolution
            foreach (var (name, prop) in schema.Parameters.Properties)
            {
                if (prop.Enum == null || prop.Enum.Count == 0) continue;
                if (!result.CorrectedArgs.TryGetValue(name, out var val) || val == null) continue;

                string strVal = val.ToString() ?? "";

                // ③a: Exact match (case-insensitive)
                var exact = prop.Enum.FirstOrDefault(e =>
                    e.Equals(strVal, StringComparison.OrdinalIgnoreCase));
                if (exact != null)
                {
                    if (exact != strVal)
                    {
                        result.CorrectedArgs[name] = exact;
                        result.Corrections.Add(
                            $"{name}: '{strVal}' → '{exact}' (大小写纠正)");
                        result.Action = ValidationAction.Corrected;
                    }
                    continue;
                }

                // ③b: Type selector conversion (field_type, selection_type, etc.)
                if (_aliasProvider != null && _aliasProvider.IsTypeSelector(name))
                {
                    var converted = _aliasProvider.ConvertType(name, strVal);
                    if (converted != null)
                    {
                        result.CorrectedArgs[name] = converted;
                        result.Corrections.Add(
                            $"{name}: '{strVal}' → '{converted}' (类型转换纠正)");
                        result.Action = ValidationAction.Corrected;
                        continue;
                    }
                }

                // ③c: Alias table lookup (IEnumAliasProvider)
                if (_aliasProvider != null)
                {
                    var resolved = _aliasProvider.ResolveAlias(name, strVal);
                    if (resolved != null)
                    {
                        result.CorrectedArgs[name] = resolved;
                        result.Corrections.Add(
                            $"{name}: '{strVal}' → '{resolved}' (别名纠正)");
                        result.Action = ValidationAction.Corrected;
                        continue;
                    }
                }

                // ③d: Substring fallback (last resort fuzzy match)
                var substring = FuzzySubstringMatch(strVal, prop.Enum);
                if (substring != null)
                {
                    result.CorrectedArgs[name] = substring;
                    result.Corrections.Add(
                        $"{name}: '{strVal}' → '{substring}' (模糊匹配纠正)");
                    result.Action = ValidationAction.Corrected;
                    continue;
                }

                // Unresolvable
                result.Errors.Add(
                    $"参数 {name} 枚举值无效: '{strVal}'，" +
                    $"有效值: [{string.Join(", ", prop.Enum.Take(10))}]" +
                    (prop.Enum.Count > 10 ? "..." : ""));
            }

            // ④ Spatial reference parameter validation
            foreach (var (name, prop) in schema.Parameters.Properties)
            {
                if (!prop.IsSpatialRef) continue;
                if (!result.CorrectedArgs.TryGetValue(name, out var val) || val == null) continue;

                string strVal = val.ToString() ?? "";
                // WKID should be an integer; longer strings look like WKT/prj paths
                if (!int.TryParse(strVal, out _) && strVal.Length > 10)
                {
                    result.Warnings.Add(
                        $"空间参考参数 {name} 值不像 WKID (应为整数): '{strVal}'");
                }
            }

            if (result.Errors.Count > 0)
                result.Action = ValidationAction.Rejected;

            return result;
        }

        private static bool IsNumericDataType(string? dataType)
        {
            if (string.IsNullOrWhiteSpace(dataType))
                return false;

            string normalized = dataType.Replace(" ", "", StringComparison.Ordinal)
                .ToLowerInvariant();
            return normalized is "gplong" or "gpdouble" or "gpshort"
                or "gpfloat" or "long" or "double" or "short" or "float"
                or "integer" or "smallinteger";
        }

        private static ToolDefinition ToDefinition(IToolDescriptor descriptor) => new()
        {
            Name = descriptor.Name,
            Description = descriptor.Description,
            Category = descriptor.Category,
            RiskLevel = ToolRiskMapper.FromAnnotation(descriptor.Annotation),
            Verification = descriptor.Verification,
            Parameters = new ToolParameters
            {
                Properties = descriptor.Parameters.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value,
                    StringComparer.OrdinalIgnoreCase),
                Required = descriptor.RequiredParameters.ToList()
            }
        };

        // ★ ADR 0056 / Phase 3：纯参数校验规则迁入 Core
        private static string? FuzzySubstringMatch(string input, List<string> validValues)
            => CoreParameterRules.FuzzySubstringMatch(input, validValues);

        private static void ValidateNumericRange(
            string name, object? val,
            ParameterProperty prop, ParameterValidationResult result)
        {
            var (valid, error) = CoreParameterRules.ValidateNumericRange(
                name, val, prop.MinValue, prop.MaxValue);
            if (!valid && error != null)
                result.Errors.Add(error);
        }

        private static void ValidateNumericType(
            string name, object? val, ParameterValidationResult result)
        {
            var error = CoreParameterRules.ValidateNumericType(name, val);
            if (error != null)
                result.Errors.Add(error);
        }
    }
}
