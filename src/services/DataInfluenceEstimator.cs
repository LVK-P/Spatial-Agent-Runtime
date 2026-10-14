using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Services
{
    /// <summary>
    /// Pre-execution probe for Modify tools. It estimates overwrite risk from
    /// real ArcGIS Pro layer state instead of relying only on tool names.
    /// </summary>
    /// <remarks>中文：Modify 工具的预执行探针。它根据真实的ArcGIS Pro图层状态估算覆盖风险，而不仅仅是依赖工具名称。</remarks>
    public class DataInfluenceEstimator : IDataInfluenceEstimator
    {
        private const int ExactProbeRowLimit = 200;
        private readonly IMapReader _mapReader;
        private readonly Dictionary<string, DescribeCache> _describeCache = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, int>? _selectionCache;

        public DataInfluenceEstimator(IMapReader mapReader)
        {
            _mapReader = mapReader ?? throw new ArgumentNullException(nameof(mapReader));
        }

        public async Task<DataInfluenceEstimate> EstimateAsync(ToolCall call, DataInfluenceContext? context = null)
        {
            if (call == null)
                return Unknown("ToolCall is null");

            ToolModifySubtype subtype = context?.Governance?.ModifySubtype ?? ToolModifySubtype.Unknown;
            string? layerName = ExtractLayerName(call.Arguments);
            var fieldNames = ExtractFieldNames(call.Arguments);
            bool usesFilterScope = HasMeaningfulFilter(call.Arguments);

            if (string.IsNullOrWhiteSpace(layerName))
                return Unknown("Unable to identify target layer from call arguments", subtype, layerName: null);

            var desc = await GetDescribeAsync(layerName);
            if (desc == null || desc.FeatureCount < 0)
                return Unknown($"Describe data unavailable for layer '{layerName}'", subtype, layerName);

            int featureCount = desc.FeatureCount;
            int scopeCount = featureCount;
            bool usesSelectionScope = false;

            var selections = await GetSelectionCountsAsync();
            if (selections.TryGetValue(layerName, out int selectedCount) && selectedCount > 0)
            {
                scopeCount = Math.Min(featureCount, selectedCount);
                usesSelectionScope = true;
            }

            if (fieldNames.Count == 0)
            {
                return new DataInfluenceEstimate
                {
                    LayerName = layerName,
                    FeatureCount = featureCount,
                    ScopeObjectCount = scopeCount,
                    ExistingNonEmptyTargetCount = scopeCount,
                    UsesSelectionScope = usesSelectionScope,
                    UsesFilteredScope = usesFilterScope,
                    ModifySubtype = subtype,
                    IsReliable = false,
                    Source = EvidenceSource.Unknown,
                    ReliabilityReason = "Target field could not be resolved from arguments"
                };
            }

            var describeFields = desc.Fields ?? new List<FieldInfo>();
            var existingFields = describeFields
                .Where(f => fieldNames.Contains(f.Name, StringComparer.OrdinalIgnoreCase))
                .ToList();
            var missingFields = fieldNames
                .Where(name => !existingFields.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            string? whereClause = ExtractParam(call.Arguments, "where_clause", "sql_expression");
            var exactProbe = existingFields.Count > 0
                ? await TryProbeExactRowsAsync(
                    layerName,
                    existingFields.Select(f => f.Name).ToList(),
                    whereClause,
                    usesSelectionScope,
                    scopeCount)
                : null;

            if (exactProbe != null)
            {
                string exactReason = $"Read {exactProbe.ScopeCount} rows directly from attribute table";
                if (usesFilterScope)
                    exactReason += "; scope narrowed by where_clause";
                if (usesSelectionScope)
                    exactReason += "; scope narrowed by current selection";

                int targetFieldCount = fieldNames.Count;
                int scopeValueCount = MultiplyScope(exactProbe.ScopeCount, targetFieldCount);
                int existingNonEmptyCount = exactProbe.TotalNonEmptyCount;

                return BuildEstimate(
                    layerName,
                    featureCount,
                    fieldNames,
                    exactProbe.ScopeCount,
                    targetFieldCount,
                    scopeValueCount,
                    existingNonEmptyCount,
                    Math.Max(0, scopeValueCount - existingNonEmptyCount),
                    missingFields.Count,
                    usesSelectionScope,
                    usesFilterScope,
                    subtype,
                    EvidenceSource.AttributeTableProbe,
                    exactReason);
            }

            int estimatedExistingNonEmpty = 0;
            var reasonParts = new List<string>();

            foreach (var fieldInfo in existingFields)
            {
                int fieldEstimate = EstimateExistingNonEmptyFromDescribe(fieldInfo, featureCount, scopeCount, out string fieldReason);
                estimatedExistingNonEmpty += fieldEstimate;
                reasonParts.Add(fieldReason);
            }

            if (missingFields.Count > 0)
                reasonParts.Add($"Fields missing and treated as first-fill only: {string.Join(", ", missingFields)}");

            string reliabilityReason = reasonParts.Count > 0
                ? string.Join("; ", reasonParts)
                : "No target field statistics available";
            if (usesFilterScope)
                reliabilityReason += "; where_clause present, scope kept conservative";
            if (usesSelectionScope)
                reliabilityReason += "; scope narrowed by current selection";

            int totalFieldCount = fieldNames.Count;
            int totalScopeValueCount = MultiplyScope(scopeCount, totalFieldCount);

            return BuildEstimate(
                layerName,
                featureCount,
                fieldNames,
                scopeCount,
                totalFieldCount,
                totalScopeValueCount,
                estimatedExistingNonEmpty,
                Math.Max(0, totalScopeValueCount - estimatedExistingNonEmpty),
                missingFields.Count,
                usesSelectionScope,
                usesFilterScope,
                subtype,
                EvidenceSource.Describe,
                reliabilityReason);
        }

        public async Task<IReadOnlyList<DataInfluenceEstimate>> EstimateBatchAsync(
            IReadOnlyList<ToolCall> calls,
            IReadOnlyList<DataInfluenceContext?>? contexts = null)
        {
            var results = new List<DataInfluenceEstimate>(calls.Count);
            for (int i = 0; i < calls.Count; i++)
            {
                DataInfluenceContext? context = contexts != null && i < contexts.Count ? contexts[i] : null;
                results.Add(await EstimateAsync(calls[i], context));
            }
            return results;
        }

        public void InvalidateCache()
        {
            _describeCache.Clear();
            _selectionCache = null;
        }

        private static DataInfluenceEstimate BuildEstimate(
            string layerName,
            int featureCount,
            IReadOnlyList<string> fieldNames,
            int scopeCount,
            int targetFieldCount,
            int scopeValueCount,
            int existingNonEmptyCount,
            int newValueFillCount,
            int schemaMutationCount,
            bool usesSelectionScope,
            bool usesFilterScope,
            ToolModifySubtype subtype,
            EvidenceSource source,
            string reliabilityReason)
        {
            return new DataInfluenceEstimate
            {
                LayerName = layerName,
                FieldName = fieldNames.Count == 1 ? fieldNames[0] : string.Join(", ", fieldNames),
                FieldNames = fieldNames,
                FeatureCount = featureCount,
                ScopeObjectCount = scopeCount,
                TargetFieldCount = targetFieldCount,
                ScopeValueCount = scopeValueCount,
                ExistingNonEmptyTargetCount = existingNonEmptyCount,
                NewValueFillCount = newValueFillCount,
                SchemaMutationCount = schemaMutationCount,
                UsesSelectionScope = usesSelectionScope,
                UsesFilteredScope = usesFilterScope,
                ModifySubtype = subtype,
                Source = source,
                ReliabilityReason = reliabilityReason
            };
        }

        private async Task<ExactRowProbeResult?> TryProbeExactRowsAsync(
            string layerName,
            IReadOnlyList<string> fieldNames,
            string? whereClause,
            bool usesSelectionScope,
            int scopeCount)
        {
            int probeLimit = scopeCount > 0
                ? Math.Min(scopeCount, ExactProbeRowLimit)
                : ExactProbeRowLimit;

            if (probeLimit <= 0)
                return null;

            if (!usesSelectionScope && string.IsNullOrWhiteSpace(whereClause) && scopeCount > ExactProbeRowLimit)
                return null;

            try
            {
                string json = await _mapReader.ReadRowsAsync(
                    layerName,
                    fieldNames,
                    whereClause,
                    probeLimit,
                    selectedOnly: usesSelectionScope && string.IsNullOrWhiteSpace(whereClause));

                var obj = JObject.Parse(json);
                if (obj["error"] != null || obj["rows"] is not JArray rows)
                    return null;

                var perFieldNonEmpty = fieldNames.ToDictionary(name => name, _ => 0, StringComparer.OrdinalIgnoreCase);
                foreach (var row in rows.OfType<JObject>())
                {
                    foreach (string fieldName in fieldNames)
                    {
                        var token = row[fieldName];
                        if (token != null && token.Type != JTokenType.Null && !string.IsNullOrWhiteSpace(token.ToString()))
                            perFieldNonEmpty[fieldName]++;
                    }
                }

                return new ExactRowProbeResult
                {
                    ScopeCount = (int?)obj["row_count"] ?? rows.Count,
                    NonEmptyCountByField = perFieldNonEmpty
                };
            }
            catch
            {
                return null;
            }
        }

        private async Task<DescribeCache?> GetDescribeAsync(string layerName)
        {
            if (_describeCache.TryGetValue(layerName, out var cached))
                return cached;

            try
            {
                string json = await _mapReader.DescribeLayerAsync(layerName);
                var desc = ParseDescribe(json);
                _describeCache[layerName] = desc;
                return desc;
            }
            catch
            {
                _describeCache[layerName] = new DescribeCache { FeatureCount = -1 };
                return null;
            }
        }

        private async Task<Dictionary<string, int>> GetSelectionCountsAsync()
        {
            if (_selectionCache != null)
                return _selectionCache;

            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string json = await _mapReader.GetSelectionAsync();
                var obj = JObject.Parse(json);
                if (obj["selections"] is JArray selections)
                {
                    foreach (var selection in selections)
                    {
                        string? layer = selection["layer"]?.ToString();
                        int count = (int?)selection["count"] ?? 0;
                        if (!string.IsNullOrWhiteSpace(layer) && count > 0)
                            counts[layer] = count;
                    }
                }
            }
            catch
            {
                // Best-effort only. Quantity gate can still run without selection scope.
                // 中文：只能尽力而为。数量门仍然可以在没有选择范围的情况下运行。
            }

            _selectionCache = counts;
            return counts;
        }

        private static DescribeCache ParseDescribe(string json)
        {
            try
            {
                var obj = JObject.Parse(json);
                if (obj["error"] != null)
                    return new DescribeCache { FeatureCount = -1 };

                int featureCount = (int?)obj["featureCount"] ?? -1;
                var fields = new List<FieldInfo>();
                if (obj["fields"] is JArray fieldArray)
                {
                    foreach (var field in fieldArray)
                    {
                        string? name = field["name"]?.ToString();
                        if (string.IsNullOrWhiteSpace(name))
                            continue;

                        int nullCount = (int?)field["null_count"] ?? 0;
                        int sampledCount = InferSampledCount(field["sample"], nullCount, featureCount);
                        fields.Add(new FieldInfo
                        {
                            Name = name,
                            NullCount = nullCount,
                            SampledCount = sampledCount
                        });
                    }
                }

                return new DescribeCache
                {
                    FeatureCount = featureCount,
                    Fields = fields
                };
            }
            catch
            {
                return new DescribeCache { FeatureCount = -1 };
            }
        }

        private static int InferSampledCount(JToken? sampleToken, int nullCount, int featureCount)
        {
            int maxSample = Math.Min(featureCount, 1000);
            if (sampleToken == null || sampleToken.Type == JTokenType.Null)
                return maxSample;

            int countedValues = 0;
            if (sampleToken["values"] is JArray values)
            {
                foreach (var value in values)
                    countedValues += (int?)value["count"] ?? 0;
            }

            int sampled = countedValues + Math.Max(0, nullCount);
            if (sampled <= 0)
                return maxSample;

            return Math.Min(maxSample, sampled);
        }

        private static int EstimateExistingNonEmptyFromDescribe(
            FieldInfo fieldInfo,
            int featureCount,
            int scopeCount,
            out string reliabilityReason)
        {
            int sampledTotal = Math.Min(featureCount, Math.Max(fieldInfo.SampledCount, 0));
            if (sampledTotal <= 0)
                sampledTotal = Math.Min(featureCount, 1000);

            int sampledNulls = Math.Min(fieldInfo.NullCount, sampledTotal);
            int sampledNonNull = Math.Max(0, sampledTotal - sampledNulls);

            if (sampledNonNull == 0)
            {
                reliabilityReason = $"Sampled {sampledTotal} rows; all values in '{fieldInfo.Name}' were null";
                return 0;
            }

            int estimatedWholeLayer = (int)((long)sampledNonNull * featureCount / sampledTotal);
            estimatedWholeLayer = Math.Max(sampledNonNull, estimatedWholeLayer);
            int estimatedScope = ScaleToScope(estimatedWholeLayer, featureCount, scopeCount);
            reliabilityReason = $"Sampled {sampledTotal} rows with {sampledNonNull} non-null values in '{fieldInfo.Name}'";
            return estimatedScope;
        }

        private static int ScaleToScope(int wholeLayerCount, int featureCount, int scopeCount)
        {
            if (featureCount <= 0 || scopeCount < 0)
                return wholeLayerCount;

            if (scopeCount >= featureCount)
                return Math.Min(featureCount, wholeLayerCount);

            int scoped = (int)((long)wholeLayerCount * scopeCount / featureCount);
            scoped = Math.Min(scopeCount, scoped);

            if (wholeLayerCount > 0 && scoped == 0)
                return 1;

            return Math.Max(0, scoped);
        }

        private static int MultiplyScope(int scopeCount, int fieldCount)
        {
            if (scopeCount < 0 || fieldCount <= 0)
                return -1;

            long result = (long)scopeCount * fieldCount;
            return result > int.MaxValue ? int.MaxValue : (int)result;
        }

        private static bool HasMeaningfulFilter(Dictionary<string, object?> args)
        {
            string? where = ExtractParam(args, "where_clause", "sql_expression");
            if (string.IsNullOrWhiteSpace(where))
                return false;

            string normalized = where.Trim().Replace(" ", "");
            return !normalized.Equals("1=1", StringComparison.OrdinalIgnoreCase);
        }

        private static string? ExtractLayerName(Dictionary<string, object?> args)
        {
            return ExtractParam(args,
                "in_table", "in_dataset", "in_features",
                "in_layer_or_view", "layer_name", "input_features",
                "input_layer", "input_table", "target_features");
        }

        private static List<string> ExtractFieldNames(Dictionary<string, object?> args)
        {
            var fields = new List<string>();
            foreach (string key in new[] { "field_name", "field", "fields", "statistics_fields", "calc_field_name" })
            {
                if (!TryGetValue(args, key, out object? value) || value == null)
                    continue;

                foreach (string token in ExpandFieldTokens(value))
                {
                    string normalized = NormalizeFieldToken(token);
                    if (!string.IsNullOrWhiteSpace(normalized) &&
                        !fields.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                    {
                        fields.Add(normalized);
                    }
                }
            }

            return fields;
        }

        private static IEnumerable<string> ExpandFieldTokens(object value)
        {
            if (value is string text)
            {
                foreach (string token in text.Split(new[] { ',', ';', '，', '；' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    yield return token;
                yield break;
            }

            if (value is JArray arr)
            {
                foreach (var item in arr)
                {
                    foreach (string token in ExpandFieldTokens(item))
                        yield return token;
                }
                yield break;
            }

            if (value is IEnumerable seq)
            {
                foreach (object? item in seq)
                {
                    if (item == null)
                        continue;

                    foreach (string token in ExpandFieldTokens(item))
                        yield return token;
                }
                yield break;
            }

            yield return value.ToString() ?? "";
        }

        private static string NormalizeFieldToken(string token)
        {
            string trimmed = token.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                return "";

            if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal) && trimmed.Length > 2)
                return trimmed[1..^1].Trim();

            int whitespaceIndex = trimmed.IndexOf(' ');
            return whitespaceIndex > 0
                ? trimmed[..whitespaceIndex].Trim()
                : trimmed;
        }

        private static bool TryGetValue(Dictionary<string, object?> args, string key, out object? value)
        {
            if (args.TryGetValue(key, out value))
                return true;

            foreach (var pair in args)
            {
                if (pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    value = pair.Value;
                    return true;
                }
            }

            value = null;
            return false;
        }

        private static string? ExtractParam(Dictionary<string, object?> args, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (TryGetValue(args, key, out object? value) && value != null)
                {
                    string text = value.ToString() ?? "";
                    if (!string.IsNullOrWhiteSpace(text))
                        return text;
                }
            }

            return null;
        }

        /// <summary>
        /// 当数据源完全不可用时（层名无法提取、describe 失败），
        /// ExistingNonEmptyTargetCount 设为极大值强制触发数量门审批——
        /// "不知道会影响多少数据"是最坏情况，不应静默放行。
        /// </summary>
        /// <remarks>English：When the data source is completely unavailable (level name cannot be extracted, describe fails), setting ExistingNonEmptyTargetCount to maximum forcibly triggers quantity gate approval—"unknown how much data will be affected" is the worst-case scenario and should not be silently released.</remarks>
        private static DataInfluenceEstimate Unknown(string reason, ToolModifySubtype subtype = ToolModifySubtype.Unknown, string? layerName = null) => new()
        {
            LayerName = layerName ?? "",
            FieldName = null,
            FeatureCount = -1,
            ScopeObjectCount = -1,
            ScopeValueCount = -1,
            ExistingNonEmptyTargetCount = int.MaxValue,  // 保守处理：没有信息时假设最坏情况。 / Conservatively assume the worst when information is unavailable.
            ModifySubtype = subtype,
            IsReliable = false,
            Source = EvidenceSource.Unknown,
            ReliabilityReason = reason
        };

        private sealed class DescribeCache
        {
            public int FeatureCount { get; set; } = -1;
            public List<FieldInfo>? Fields { get; set; }
        }

        private sealed class FieldInfo
        {
            public string Name { get; set; } = "";
            public int NullCount { get; set; }
            public int SampledCount { get; set; }
        }

        private sealed class ExactRowProbeResult
        {
            public int ScopeCount { get; set; }
            public Dictionary<string, int> NonEmptyCountByField { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public int TotalNonEmptyCount => NonEmptyCountByField.Values.Sum();
        }
    }
}
