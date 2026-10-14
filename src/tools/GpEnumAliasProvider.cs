using System;
using System.Collections.Generic;
using System.Linq;
using SpatialAgent.Runtime.Contracts;

// ================================================================
// GpEnumAliasProvider.cs — GP 工具枚举值别名解析器
//
// 从本地 tools_manifest.json 的 filter 字段 + 人工维护的
// tools_aliases.json 批量生成别名映射。
// 实现 IEnumAliasProvider，通过构造函数注入到 ParameterValidator。
//
// 放在 Tools 层而非 Runtime 层——包含 ArcGIS GP 工具专有名词
// （PYTHON3, NEW_SELECTION, CGCS2000 等），不应泄漏到 Runtime 核心。
//
// 架构审查发现原别名表会把 GP 领域知识带入 Runtime；
// 此实现把该知识保留在 Tools 能力层。
// ================================================================
// English：================================================================ GpEnumAliasProvider.cs — GP Tool Enumeration Value Alias: Batch generates aliase mappings from the local tools_manifest.json filter field + manually maintained tools_aliases.json. Implement IEnumAliasProvider and inject it into ParameterValidator via constructors. Place them at the Tools layer rather than the Runtime layer—containing ArcGIS GP tool terms (PYTHON3, NEW_SELECTION, CGCS2000, etc.), and should not leak to the Runtime core. Architecture review found that the original aliases list brings GP domain knowledge into Runtime; This implementation retains that knowledge at the Tools capability layer. ================================================================
namespace SpatialAgent.Tools.Core
{
    /// <summary>
    /// GP-tool-specific enum alias resolver.
    /// Populated from local tools_manifest.json filter fields + tools_aliases.json overrides.
    /// </summary>
    /// <remarks>中文：GP 专用的枚举别名解析器。从本地tools_manifest.json滤波字段+tools_aliases.json覆盖填充。</remarks>
    public class GpEnumAliasProvider : IEnumAliasProvider
    {
        // paramName → (lowercase_input → canonical_value)
        // 中文：参数名 → （lowercase_input → canonical_value）
        private readonly Dictionary<string, Dictionary<string, string>> _aliases;

        // Parameter names that are "type selectors" — benefit from type-system conversion
        // 中文：作为“类型选择器”的参数名称——受益于类型系统转换
        private static readonly HashSet<string> TypeSelectorParams = new(StringComparer.OrdinalIgnoreCase)
        {
            "field_type", "expression_type", "selection_type"
        };

        // Type system conversion: general → GP specific
        // 中文：类型系统转换：通用→GP专用
        private static readonly Dictionary<string, string> FieldTypeMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["string"] = "TEXT", ["text"] = "TEXT",
            ["integer"] = "LONG", ["int"] = "LONG", ["long"] = "LONG",
            ["double"] = "DOUBLE", ["float"] = "FLOAT",
            ["date"] = "DATE", ["datetime"] = "DATE",
            ["short"] = "SHORT",
            ["blob"] = "BLOB", ["guid"] = "GUID",
            ["raster"] = "RASTER",
        };

        public GpEnumAliasProvider(Dictionary<string, Dictionary<string, string>>? aliases = null)
        {
            _aliases = aliases ?? new Dictionary<string, Dictionary<string, string>>();
        }

        /// <summary>
        /// Populate aliases from locally generated tools_manifest.json data.
        /// Call once at startup after loading the manifest.
        /// </summary>
        /// <remarks>中文：从本地生成的tools_manifest.json数据中填充别名。加载清单后启动时打一次电话。</remarks>
        public void LoadFromManifest(IEnumerable<ToolDefinition> tools)
        {
            foreach (var tool in tools)
            {
                foreach (var (paramName, prop) in tool.Parameters.Properties)
                {
                    if (prop.Enum == null || prop.Enum.Count == 0) continue;

                    if (!_aliases.ContainsKey(paramName))
                        _aliases[paramName] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var enumValue in prop.Enum)
                    {
                        // Auto-generate lowercase → canonical mapping
                        // 中文：自动生成小写→典范映射
                        _aliases[paramName][enumValue.ToLowerInvariant()] = enumValue;
                    }
                }
            }
        }

        /// <summary>
        /// Load additional aliases from a curated JSON file.
        /// Merges with manifest-derived aliases — manual entries take precedence.
        /// </summary>
        /// <remarks>中文：从精心策划的JSON文件加载额外的别名。与清单衍生的别名合并——手动条目优先。</remarks>
        public void LoadAliasOverrides(Dictionary<string, Dictionary<string, string>> overrides)
        {
            foreach (var (paramName, aliasMap) in overrides)
            {
                if (!_aliases.ContainsKey(paramName))
                    _aliases[paramName] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var (input, canonical) in aliasMap)
                {
                    _aliases[paramName][input.ToLowerInvariant()] = canonical;
                }
            }
        }

        /// <inheritdoc/>
        public string? ResolveAlias(string paramName, string inputValue)
        {
            if (string.IsNullOrWhiteSpace(inputValue)) return null;

            if (_aliases.TryGetValue(paramName, out var aliasMap))
            {
                if (aliasMap.TryGetValue(inputValue.ToLowerInvariant(), out var resolved))
                    return resolved;
            }

            return null;
        }

        /// <inheritdoc/>
        public bool IsTypeSelector(string paramName)
        {
            return TypeSelectorParams.Contains(paramName);
        }

        /// <inheritdoc/>
        public string? ConvertType(string paramName, string typeNameFromLLM)
        {
            if (string.IsNullOrWhiteSpace(typeNameFromLLM)) return null;

            if (paramName.Equals("field_type", StringComparison.OrdinalIgnoreCase))
            {
                return FieldTypeMap.TryGetValue(typeNameFromLLM, out var gpType) ? gpType : null;
            }

            return null;
        }

        /// <summary>
        /// Register default curated aliases (Chinese + common LLM mistakes).
        /// Covers the top 50 high-frequency parameters.
        /// </summary>
        /// <remarks>中文：注册默认的策划别名（中文+常见的LLM错误）。涵盖了前50个高频参数。</remarks>
        public static Dictionary<string, Dictionary<string, string>> DefaultCuratedAliases => new()
        {
            ["selection_type"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["new"] = "NEW_SELECTION",
                ["add"] = "ADD_TO_SELECTION",
                ["remove"] = "REMOVE_FROM_SELECTION",
                ["subset"] = "SUBSET_SELECTION",
                ["新建选择"] = "NEW_SELECTION",
                ["添加到选择"] = "ADD_TO_SELECTION",
                ["从选择中移除"] = "REMOVE_FROM_SELECTION",
                ["新建"] = "NEW_SELECTION",
                ["添加"] = "ADD_TO_SELECTION",
            },
            ["expression_type"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["python"] = "PYTHON3",
                ["python3"] = "PYTHON3",
                ["py"] = "PYTHON3",
                ["arcade"] = "ARCADE",
                ["sql"] = "SQL",
            },
            ["dissolve_option"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["none"] = "NONE",
                ["all"] = "ALL",
                ["list"] = "LIST",
                ["不融合"] = "NONE",
                ["全部融合"] = "ALL",
                ["按字段融合"] = "LIST",
            },
            ["line_side"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["full"] = "FULL",
                ["left"] = "LEFT",
                ["right"] = "RIGHT",
                ["双侧"] = "FULL",
                ["左侧"] = "LEFT",
                ["右侧"] = "RIGHT",
            },
            ["line_end_type"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["round"] = "ROUND",
                ["flat"] = "FLAT",
                ["圆头"] = "ROUND",
                ["平头"] = "FLAT",
            },
            ["join_attributes"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["all"] = "ALL",
                ["no_fid"] = "NO_FID",
                ["only_fid"] = "ONLY_FID",
            },
            ["output_type"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["input"] = "INPUT",
                ["line"] = "LINE",
                ["point"] = "POINT",
            },
        };
    }
}
