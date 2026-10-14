using System;
using System.Collections.Generic;
using System.Linq;

// ────────────────────────────────────────────────────────────────
// CoreParameterRules.cs — Pure parameter validation helpers.
//
// ADR 0056 / Phase 3：从 ParameterValidator 中析出的纯规则——零 I/O、
// 零宿主依赖，可独立测试。内容：
//   1. FuzzySubstringMatch — 子串模糊匹配
//   2. ValidateNumericRange — 数值范围校验
//   3. ValidateNumericType — 数值类型校验
// ────────────────────────────────────────────────────────────────
namespace SpatialAgent.Core
{
    public static class CoreParameterRules
    {
        /// <summary>
        /// Fuzzy substring match — case-insensitive exact, then containment.
        /// Returns canonical form on match, null otherwise.
        /// </summary>
        /// <remarks>中文：模糊子串匹配——大小写不区分的精确，然后是包含。匹配时返回规范形式，否则无效。</remarks>
        public static string? FuzzySubstringMatch(string input, List<string> validValues)
        {
            var upper = input.ToUpperInvariant();
            // Prefer exact substring match
            // 中文：偏好精确的子串匹配
            var exact = validValues.FirstOrDefault(v =>
                v.ToUpperInvariant() == upper);
            if (exact != null) return exact;

            // Then containment match
            // 中文：然后是遏制匹配
            var contains = validValues.FirstOrDefault(v =>
                v.ToUpperInvariant().Contains(upper) ||
                upper.Contains(v.ToUpperInvariant()));
            return contains;
        }

        /// <summary>
        /// Validate that a numeric parameter value falls within [MinValue, MaxValue].
        /// </summary>
        /// <remarks>中文：验证某个数值参数值是否落在[最小值，最大值]范围内。</remarks>
        public static (bool valid, string? error) ValidateNumericRange(
            string name, object? val, double? minValue, double? maxValue)
        {
            if (!double.TryParse(val?.ToString(), out var num))
                return (true, null); // 非数值则交给类型验证器。 / Non-numeric values are deferred to the type validator.

            if (minValue.HasValue && num < minValue.Value)
                return (false, $"参数 {name} 值 {num} 小于最小值 {minValue}");
            if (maxValue.HasValue && num > maxValue.Value)
                return (false, $"参数 {name} 值 {num} 大于最大值 {maxValue}");

            return (true, null);
        }

        /// <summary>
        /// Validate that a parameter value is a numeric CLR type or parseable as long/double.
        /// </summary>
        /// <remarks>中文：验证参数值是否为数值 CLR 类型，或能否解析为 long/double。</remarks>
        public static string? ValidateNumericType(string name, object? val)
        {
            if (val is long or int or double or float) return null;
            if (!long.TryParse(val?.ToString(), out _) &&
                !double.TryParse(val?.ToString(), out _))
            {
                return $"参数 {name} 类型错误: 期望数值，实际为 '{val}'";
            }
            return null;
        }
    }
}
