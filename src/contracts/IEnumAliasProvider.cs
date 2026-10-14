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
// English：================================================================ IEnumAliasProvider.cs — Enumeration Value Alias Parsing Interface Decouples GIS tool-specific enumeration knowledge from the Runtime core. Runtime's ParameterValidator relies on this interface for fuzzy matching, and the specific aliased data is injected by the Tools layer from the local tools_manifest.json + tools_aliases.json. The architecture review found that hard-coding EnumAliases in ParameterValidator causes GP domain knowledge to leak to the runtime layer. This interface is used to maintain layered boundaries. ================================================================
namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Resolves LLM-produced enum values to their canonical GP tool names.
    /// Implementations provide case-insensitive matching, common alias tables,
    /// and type-system conversion (e.g. "string" → "TEXT" for field_type).
    /// </summary>
    /// <remarks>中文：把 LLM 生成的枚举值解析为规范的 GP 工具值。实现应提供不区分大小写的匹配、常用别名表和类型系统转换（例如 field_type 的 "string" → "TEXT"）。</remarks>
    public interface IEnumAliasProvider
    {
        /// <summary>
        /// Resolve a potentially-incorrect enum value to the canonical form.
        /// Returns null if no match found (caller should report as error).
        /// </summary>
        /// <param name="paramName">Parameter name (e.g. "field_type", "selection_type").</param>
        /// <param name="inputValue">LLM-provided value (may be wrong case, alias, or chinese).</param>
        /// <returns>Canonical enum value, or null if unresolvable.</returns>
        /// <remarks>中文：把可能不正确的枚举值解析为规范形式。找不到匹配时返回 null，由调用方报告错误。参数名示例为 "field_type"、"selection_type"；输入值可能存在大小写错误、使用别名或为中文。</remarks>
        string? ResolveAlias(string paramName, string inputValue);

        /// <summary>
        /// Whether this parameter is a "type selector" — its values belong to a
        /// type system (GP field types, GP selection types) rather than free-form
        /// enums. Type selectors benefit from an additional conversion step.
        /// </summary>
        /// <remarks>中文：该参数是否为“类型选择器”，即其值属于类型系统（GP 字段类型、GP 选择类型），而非自由形式枚举。类型选择器可使用额外的转换步骤。</remarks>
        bool IsTypeSelector(string paramName);

        /// <summary>
        /// Convert a general type name to the GP-specific equivalent.
        /// E.g. "string" → "TEXT", "integer" → "LONG", "double" → "DOUBLE".
        /// Returns null if no conversion available.
        /// </summary>
        /// <remarks>中文：把通用类型名转换为 GP 专用值，例如 "string" → "TEXT"、"integer" → "LONG"、"double" → "DOUBLE"；没有可用转换时返回 null。</remarks>
        string? ConvertType(string paramName, string typeNameFromLLM);
    }
}
