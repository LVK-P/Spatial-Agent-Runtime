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
        public void RegisterAll(params string[] schemaNames)
        {
            foreach (var name in schemaNames)
                Register(name);
        }

        /// <summary>Get the dot name (GPToolRunner format).</summary>
        public string ToDotName(string name)
        {
            string schema = Resolve(name);
            return _schemaToDot.TryGetValue(schema, out var dot) ? dot : SchemaToDot(schema);
        }

        /// <summary>Get the schema name (underscore format).</summary>
        public string ToSchemaName(string name)
        {
            return Resolve(name);
        }

        /// <summary>Get the short name (human-readable).</summary>
        public string ToShortName(string name)
        {
            string schema = Resolve(name);
            return ExtractShortName(schema);
        }

        /// <summary>Get the toolbox alias.</summary>
        public string GetToolbox(string name)
        {
            string schema = Resolve(name);
            return _schemaToToolbox.TryGetValue(schema, out var tb) ? tb : "";
        }

        /// <summary>
        /// Resolve ANY form (schema, dot, short) to the canonical schema name.
        /// </summary>
        public string Resolve(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";

            // 1. Exact schema name match
            if (_schemaToDot.ContainsKey(name))
                return name;

            // 2. Exact dot name match
            if (_dotToSchema.TryGetValue(name, out var schema))
                return schema;

            // 3. Short name match
            if (_shortToSchema.TryGetValue(ExtractShortName(name), out schema))
                return schema;

            // 4. Normalize and retry (handles mixed formats)
            string normalized = NormalizeSchemaName(name);
            if (_schemaToDot.ContainsKey(normalized))
                return normalized;

            string dotForm = SchemaToDot(name.Contains('.') ? name : normalized);
            if (_dotToSchema.TryGetValue(dotForm, out schema))
                return schema;

            // 5. Last-ditch: check if any registered name contains the query
            // Deterministic: sort by key length (shorter=better match) then alphabetically
            string queryLower = name.Replace(".", "_").ToLower();
            foreach (var key in _schemaToDot.Keys.OrderBy(k => k.Length).ThenBy(k => k))
            {
                if (key.ToLower().Contains(queryLower) || queryLower.Contains(key.ToLower()))
                    return key;
            }

            return name; // Give up, return as-is
        }

        /// <summary>Check if a tool name is registered.</summary>
        public bool IsRegistered(string name)
        {
            return _schemaToDot.ContainsKey(Resolve(name));
        }

        // ── Static helpers ──

        /// <summary>management.AddField → management_AddField</summary>
        public static string NormalizeSchemaName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            return name.Replace('.', '_');
        }

        /// <summary>management_AddField → management.AddField</summary>
        public static string SchemaToDot(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            int idx = name.IndexOf('_');
            if (idx < 0) return name; // No underscore = no toolbox prefix
            string toolbox = name[..idx];
            string tool = name[(idx + 1)..];
            return $"{toolbox}.{tool}";
        }

        /// <summary>management_AddField → AddField</summary>
        public static string ExtractShortName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            // Handle dot format
            string normalized = name.Contains('.') ? NormalizeSchemaName(name) : name;
            int idx = normalized.IndexOf('_');
            return idx >= 0 && idx < normalized.Length - 1
                ? normalized[(idx + 1)..]
                : normalized;
        }

        /// <summary>management_AddField → management</summary>
        public static string ExtractToolbox(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            string normalized = name.Contains('.') ? NormalizeSchemaName(name) : name;
            int idx = normalized.IndexOf('_');
            return idx > 0 ? normalized[..idx] : "";
        }
    }
}
