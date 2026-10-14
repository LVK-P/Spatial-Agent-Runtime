using System;
using System.Collections.Generic;
using System.Linq;

// ToolNameResolver.cs — Three-form tool name normalization.
// The tool identity model uses three name formats:
//   management_AddField   ← schema name (LLM Function Calling, registration)
//   management.AddField   ← dot name (GPToolRunner execution)
//   AddField              ← short name (fuzzy matching, human-readable)
// All modules that match tool names (SafetyGate, PostConditionValidationRule,
// GPToolContract, ToolRegistry) normalize through this resolver so no module
// maintains its own string-matching logic.
// 中文：ToolNameResolver.cs — 在三种工具名称格式之间归一化。工具身份使用：management_AddField（模式名，用于 LLM 函数调用和注册）、management.AddField（点号名，用于 GPToolRunner 执行）、AddField（短名称，用于模糊匹配和人类阅读）。所有需要匹配工具名的模块（SafetyGate、PostConditionValidationRule、GPToolContract、ToolRegistry）都通过此解析器归一化，避免各自维护字符串匹配逻辑。
namespace SpatialAgent.Core
{
    public class ToolNameResolver
    {
        private readonly Dictionary<string, string> _schemaToDot = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _dotToSchema = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _shortToSchema = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _schemaToToolbox = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Register a tool name in all three forms.
        /// </summary>
        /// <param name="schemaName">e.g. "management_AddField"</param>
        /// <remarks>中文：在这三种表格中注册工具名称。例如“management_AddField”</remarks>
        public void Register(string schemaName)
        {
            if (string.IsNullOrEmpty(schemaName)) return;

            string normalized = NormalizeSchemaName(schemaName);
            string dotName = SchemaToDot(normalized);
            string shortName = ExtractShortName(normalized);
            string toolbox = ExtractToolbox(normalized);

            _schemaToDot[normalized] = dotName;
            _dotToSchema[dotName] = normalized;
            if (!_shortToSchema.ContainsKey(shortName))
                _shortToSchema[shortName] = normalized;
            _schemaToToolbox[normalized] = toolbox;
        }

        /// <summary>Register all three forms explicitly.</summary>
        /// <remarks>中文：明确注册所有三种表格。</remarks>
        public void RegisterAll(params string[] schemaNames)
        {
            foreach (var name in schemaNames)
                Register(name);
        }

        /// <summary>Get the dot name (GPToolRunner format).</summary>
        /// <remarks>中文：获取点号名称（GPToolRunner 格式）。</remarks>
        public string ToDotName(string name)
        {
            string schema = Resolve(name);
            return _schemaToDot.TryGetValue(schema, out var dot) ? dot : SchemaToDot(schema);
        }

        /// <summary>Get the schema name (underscore format).</summary>
        /// <remarks>中文：获取模式名称（下划线格式）。</remarks>
        public string ToSchemaName(string name)
        {
            return Resolve(name);
        }

        /// <summary>Get the short name (human-readable).</summary>
        /// <remarks>中文：获取短名称（便于人类阅读）。</remarks>
        public string ToShortName(string name)
        {
            string schema = Resolve(name);
            return ExtractShortName(schema);
        }

        /// <summary>Get the toolbox alias.</summary>
        /// <remarks>中文：获取工具箱别名。</remarks>
        public string GetToolbox(string name)
        {
            string schema = Resolve(name);
            return _schemaToToolbox.TryGetValue(schema, out var tb) ? tb : "";
        }

        /// <summary>
        /// Resolve ANY form (schema, dot, short) to the canonical schema name.
        /// </summary>
        /// <remarks>中文：将任何形式（模式、点、短）解析为规范模式名称。</remarks>
        public string Resolve(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";

            // 1. Exact schema name match
            // 中文：1. 模式名称的精确匹配
            if (_schemaToDot.ContainsKey(name))
                return name;

            // 2. Exact dot name match
            // 中文：2. 点名的精确匹配
            if (_dotToSchema.TryGetValue(name, out var schema))
                return schema;

            // 3. Short name match
            // 中文：3. 短名称匹配
            if (_shortToSchema.TryGetValue(ExtractShortName(name), out schema))
                return schema;

            // 4. Normalize and retry (handles mixed formats)
            // 中文：4. 规范化并重试（处理混合格式）
            string normalized = NormalizeSchemaName(name);
            if (_schemaToDot.ContainsKey(normalized))
                return normalized;

            string dotForm = SchemaToDot(name.Contains('.') ? name : normalized);
            if (_dotToSchema.TryGetValue(dotForm, out schema))
                return schema;

            // 5. Last-ditch: check if any registered name contains the query
            // Deterministic: sort by key length (shorter=better match) then alphabetically
            // 中文：5. 最后尝试：检查注册名称是否包含查询。为保证确定性，先按键长度（越短越优）再按字母顺序排序
            string queryLower = name.Replace(".", "_").ToLower();
            foreach (var key in _schemaToDot.Keys.OrderBy(k => k.Length).ThenBy(k => k))
            {
                if (key.ToLower().Contains(queryLower) || queryLower.Contains(key.ToLower()))
                    return key;
            }

            return name; // 无法解析时原样返回。 / Give up and return as-is.
        }

        /// <summary>Check if a tool name is registered.</summary>
        /// <remarks>中文：检查工具名称是否已注册。</remarks>
        public bool IsRegistered(string name)
        {
            return _schemaToDot.ContainsKey(Resolve(name));
        }

        // ── Static helpers ──
        // 中文：静态辅助器

        /// <summary>management.AddField → management_AddField</summary>
        /// <remarks>中文：管理层。AddField → management_AddField</remarks>
        public static string NormalizeSchemaName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            return name.Replace('.', '_');
        }

        /// <summary>management_AddField → management.AddField</summary>
        /// <remarks>中文：management_AddField →管理层。AddField</remarks>
        public static string SchemaToDot(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            int idx = name.IndexOf('_');
            if (idx < 0) return name; // 没有下划线即没有工具箱前缀。 / No underscore means no toolbox prefix.
            string toolbox = name[..idx];
            string tool = name[(idx + 1)..];
            return $"{toolbox}.{tool}";
        }

        /// <summary>management_AddField → AddField</summary>
        /// <remarks>中文：management_AddField → AddField</remarks>
        public static string ExtractShortName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            // Handle dot format
            // 中文：处理点号格式
            string normalized = name.Contains('.') ? NormalizeSchemaName(name) : name;
            int idx = normalized.IndexOf('_');
            return idx >= 0 && idx < normalized.Length - 1
                ? normalized[(idx + 1)..]
                : normalized;
        }

        /// <summary>management_AddField → management</summary>
        /// <remarks>中文：management_AddField →管理</remarks>
        public static string ExtractToolbox(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            string normalized = name.Contains('.') ? NormalizeSchemaName(name) : name;
            int idx = normalized.IndexOf('_');
            return idx > 0 ? normalized[..idx] : "";
        }
    }
}
