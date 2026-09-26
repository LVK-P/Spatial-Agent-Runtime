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
        public static string? FuzzySubstringMatch(string input, List<string> validValues)
        {
            var upper = input.ToUpperInvariant();
            // Prefer exact substring match
            var exact = validValues.FirstOrDefault(v =>
                v.ToUpperInvariant() == upper);
            if (exact != null) return exact;

            // Then containment match
            var contains = validValues.FirstOrDefault(v =>
                v.ToUpperInvariant().Contains(upper) ||
                upper.Contains(v.ToUpperInvariant()));
            return contains;
        }

        /// <summary>
        /// Validate that a numeric parameter value falls within [MinValue, MaxValue].
        /// </summary>
        public static (bool valid, string? error) ValidateNumericRange(
            string name, object? val, double? minValue, double? maxValue)
        {
            if (!double.TryParse(val?.ToString(), out var num))
                return (true, null); // non-numeric → defer to type validator

            if (minValue.HasValue && num < minValue.Value)
                return (false, $"参数 {name} 值 {num} 小于最小值 {minValue}");
            if (maxValue.HasValue && num > maxValue.Value)
                return (false, $"参数 {name} 值 {num} 大于最大值 {maxValue}");

            return (true, null);
        }

        /// <summary>
        /// Validate that a parameter value is a numeric CLR type or parseable as long/double.
        /// </summary>
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
