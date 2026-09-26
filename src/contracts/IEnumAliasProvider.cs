// ================================================================
// IEnumAliasProvider.cs — 枚举值别名解析接口
//
// 将 GIS 工具特定的枚举值知识从 Runtime 核心解耦。
// Runtime 的 ParameterValidator 依赖此接口进行模糊匹配，
// 具体别名数据由 Tools 层从本地 tools_manifest.json + tools_aliases.json 注入。
//
// 架构审查发现：将 EnumAliases 硬编码在 ParameterValidator 中会导致
// GP 领域知识泄漏到 Runtime 层。此接口用于守住分层边界。
// ================================================================
namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Resolves LLM-produced enum values to their canonical GP tool names.
    /// Implementations provide case-insensitive matching, common alias tables,
    /// and type-system conversion (e.g. "string" → "TEXT" for field_type).
    /// </summary>
    public interface IEnumAliasProvider
    {
        /// <summary>
        /// Resolve a potentially-incorrect enum value to the canonical form.
        /// Returns null if no match found (caller should report as error).
        /// </summary>
        /// <param name="paramName">Parameter name (e.g. "field_type", "selection_type").</param>
        /// <param name="inputValue">LLM-provided value (may be wrong case, alias, or chinese).</param>
        /// <returns>Canonical enum value, or null if unresolvable.</returns>
        string? ResolveAlias(string paramName, string inputValue);

        /// <summary>
        /// Whether this parameter is a "type selector" — its values belong to a
        /// type system (GP field types, GP selection types) rather than free-form
        /// enums. Type selectors benefit from an additional conversion step.
        /// </summary>
        bool IsTypeSelector(string paramName);

        /// <summary>
        /// Convert a general type name to the GP-specific equivalent.
        /// E.g. "string" → "TEXT", "integer" → "LONG", "double" → "DOUBLE".
        /// Returns null if no conversion available.
        /// </summary>
        string? ConvertType(string paramName, string typeNameFromLLM);
    }
}
