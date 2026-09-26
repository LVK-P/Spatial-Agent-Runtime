using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using SpatialAgent.Core;
using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Services
{
    /// <summary>
    /// Loads an optional, locally maintained risk catalog and exposes
    /// exact-name lookups for runtime safety classification.
    /// </summary>
    internal sealed class ToolRiskCatalog
    {
        private const string CatalogFileName = "tool_risk_catalog.json";
        private static readonly Lazy<ToolRiskCatalog> _lazy = new(LoadInternal);
        private readonly Dictionary<string, ToolRiskCatalogEntry> _entries;

        private ToolRiskCatalog(Dictionary<string, ToolRiskCatalogEntry> entries)
        {
            _entries = entries;
        }

        public static ToolRiskCatalog Instance => _lazy.Value;

        public IEnumerable<ToolRiskCatalogEntry> Entries => _entries.Values;

        public bool TryGet(string? toolName, out ToolRiskCatalogEntry entry)
        {
            if (!string.IsNullOrWhiteSpace(toolName) && _entries.TryGetValue(toolName, out entry))
                return true;

            entry = default!;
            return false;
        }

        private static ToolRiskCatalog LoadInternal()
        {
            string? path = ResourcePaths.Find(CatalogFileName);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return new ToolRiskCatalog(new Dictionary<string, ToolRiskCatalogEntry>(StringComparer.OrdinalIgnoreCase));
            }

            try
            {
                string json = File.ReadAllText(path);
                var rows = JsonConvert.DeserializeObject<List<ToolRiskCatalogRow>>(json) ?? new List<ToolRiskCatalogRow>();
                var entries = new Dictionary<string, ToolRiskCatalogEntry>(StringComparer.OrdinalIgnoreCase);

                foreach (var row in rows)
                {
                    ToolRiskLevel risk = ToolRiskMapper.FromSerialized(
                        row.Level1Risk,
                        destructiveFallback: false,
                        defaultRisk: ToolRiskLevel.Modify);

                    var entry = new ToolRiskCatalogEntry(
                        row.FunctionName ?? "",
                        row.ExecutionName ?? "",
                        row.DisplayName ?? "",
                        risk,
                        ToolGovernanceMapper.ParseModifySubtype(row.ModifySubtype),
                        row.BucketReason ?? "");

                    Register(entries, entry.FunctionName, entry);
                    Register(entries, entry.ExecutionName, entry);
                }

                return new ToolRiskCatalog(entries);
            }
            catch (Exception ex)
            {
                AgentDiagnostics.Warn("ToolRiskCatalog.Load",
                    $"Failed to parse '{CatalogFileName}'. Runtime falls back to manifest/default inference.", ex);
                return new ToolRiskCatalog(new Dictionary<string, ToolRiskCatalogEntry>(StringComparer.OrdinalIgnoreCase));
            }
        }

        private static void Register(
            Dictionary<string, ToolRiskCatalogEntry> entries,
            string? key,
            ToolRiskCatalogEntry entry)
        {
            if (string.IsNullOrWhiteSpace(key))
                return;

            entries[key] = entry;
        }

        private sealed class ToolRiskCatalogRow
        {
            [JsonProperty("function_name")] public string? FunctionName { get; set; }
            [JsonProperty("execution_name")] public string? ExecutionName { get; set; }
            [JsonProperty("display_name")] public string? DisplayName { get; set; }
            [JsonProperty("level1_risk")] public string? Level1Risk { get; set; }
            [JsonProperty("modify_subtype")] public string? ModifySubtype { get; set; }
            [JsonProperty("bucket_reason")] public string? BucketReason { get; set; }
        }
    }

    internal readonly record struct ToolRiskCatalogEntry(
        string FunctionName,
        string ExecutionName,
        string DisplayName,
        ToolRiskLevel RiskLevel,
        ToolModifySubtype ModifySubtype,
        string BucketReason);
}
