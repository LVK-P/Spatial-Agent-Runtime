using System.IO;
using System.Security.Cryptography;
using System.Text;
using ArcGIS.Core.Data;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using Newtonsoft.Json;
using SpatialAgent.Core;

namespace SpatialAgent.Adapters.ArcGIS
{
    /// <summary>
    /// Reads ArcGIS Pro map state: layers, fields, selections, extent.
    /// Pure adapter — returns JSON strings consumed by Tools layer.
    /// </summary>
    /// <remarks>中文：读取ArcGIS Pro地图状态：图层、字段、选择、范围。纯适配器 — 返回工具层消耗的 JSON 字符串。</remarks>
    public class MapReader : SpatialAgent.Runtime.Contracts.IMapReader
    {
        public async Task<SpatialAgent.Runtime.Contracts.MapContextSnapshot> GetMapContextAsync()
        {
            return await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null)
                    return new SpatialAgent.Runtime.Contracts.MapContextSnapshot();

                var layers = new List<SpatialAgent.Runtime.Contracts.LayerInfo>();
                var storageRevisions = new Dictionary<string, long>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (var layer in map.GetLayersAsFlattenedList())
                {
                    if (layer is RasterLayer)
                    {
                        layers.Add(new SpatialAgent.Runtime.Contracts.LayerInfo
                        {
                            Name = layer.Name,
                            MapMemberId = StableId("map", layer.URI ?? layer.Name),
                            GeometryType = "Raster"
                        });
                        continue;
                    }
                    if (layer is not FeatureLayer featureLayer)
                        continue;

                    try
                    {
                        var featureClass = featureLayer.GetFeatureClass();
                        string dataSource = FileSystemDataSourcePath.Normalize(
                            featureClass?.GetDatastore()?.GetPath());
                        if (dataSource.Contains("agentbackup.gdb", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var definition = featureClass?.GetDefinition();
                        if (featureClass == null || definition == null)
                            continue;

                        layers.Add(new SpatialAgent.Runtime.Contracts.LayerInfo
                        {
                            Name = layer.Name,
                            MapMemberId = StableId("map", layer.URI ?? layer.Name),
                            DatasetId = StableId(
                                "dataset",
                                $"{dataSource}|{featureClass.GetName()}"),
                            DatasetName = featureClass.GetName() ?? "",
                            GeometryType = definition.GetShapeType().ToString(),
                            FeatureCount = featureClass.GetCount(),
                            Fields = definition.GetFields().Select(field => field.Name).ToList(),
                            SchemaFingerprint = BuildSchemaFingerprint(
                                featureClass.GetName() ?? "",
                                definition.GetFields().Select(field =>
                                    $"{field.Name}:{field.FieldType}")),
                            StateFingerprint = BuildStateFingerprint(
                                dataSource,
                                featureClass.GetName() ?? "",
                                definition.GetFields().Select(field =>
                                    $"{field.Name}:{field.FieldType}"),
                                featureClass.GetCount(),
                                GetStorageRevision(
                                    dataSource,
                                    storageRevisions)),
                            StateFingerprintReliable =
                                IsFileBacked(dataSource)
                        });
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[MapReader] Structured map snapshot failed for '{layer.Name}': " +
                            $"{ex.GetType().Name}: {ex.Message}");
                        // Preserve target identity even when a detailed probe fails.
                        // 中文：即使详细探针失败，也要保持目标身份。
                        layers.Add(new SpatialAgent.Runtime.Contracts.LayerInfo
                        {
                            Name = layer.Name,
                            MapMemberId = StableId("map", layer.URI ?? layer.Name)
                        });
                    }
                }

                foreach (var standalone in map.StandaloneTables)
                {
                    try
                    {
                        var table = standalone.GetTable();
                        string dataSource = FileSystemDataSourcePath.Normalize(
                            table?.GetDatastore()?.GetPath());
                        if (dataSource.Contains("agentbackup.gdb", StringComparison.OrdinalIgnoreCase))
                            continue;
                        var definition = table?.GetDefinition();
                        if (table == null || definition == null)
                            continue;
                        layers.Add(new SpatialAgent.Runtime.Contracts.LayerInfo
                        {
                            Name = standalone.Name,
                            MapMemberId = StableId(
                                "map",
                                standalone.URI ?? standalone.Name),
                            DatasetId = StableId(
                                "dataset",
                                $"{dataSource}|{table.GetName()}"),
                            DatasetName = table.GetName() ?? "",
                            GeometryType = "Table",
                            FeatureCount = table.GetCount(),
                            Fields = definition.GetFields().Select(field => field.Name).ToList(),
                            SchemaFingerprint = BuildSchemaFingerprint(
                                table.GetName() ?? "",
                                definition.GetFields().Select(field =>
                                    $"{field.Name}:{field.FieldType}")),
                            StateFingerprint = BuildStateFingerprint(
                                dataSource,
                                table.GetName() ?? "",
                                definition.GetFields().Select(field =>
                                    $"{field.Name}:{field.FieldType}"),
                                table.GetCount(),
                                GetStorageRevision(
                                    dataSource,
                                    storageRevisions)),
                            StateFingerprintReliable =
                                IsFileBacked(dataSource)
                        });
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[MapReader] Structured table snapshot failed for '{standalone.Name}': " +
                            $"{ex.GetType().Name}: {ex.Message}");
                        layers.Add(new SpatialAgent.Runtime.Contracts.LayerInfo
                        {
                            Name = standalone.Name,
                            MapMemberId = StableId(
                                "map",
                                standalone.URI ?? standalone.Name),
                            GeometryType = "Table"
                        });
                    }
                }

                return new SpatialAgent.Runtime.Contracts.MapContextSnapshot
                {
                    IsAvailable = true,
                    MapName = map.Name ?? "",
                    SpatialReferenceName = map.SpatialReference?.Name ?? "",
                    Wkid = map.SpatialReference?.Wkid ?? 0,
                    Layers = layers
                };
            });
        }

        public async Task<string> GetContentRevisionAsync()
        {
            // Pending ArcGIS edit transactions do not necessarily update the
            // File GDB timestamps yet. Treat that state as unversionable so a
            // mutating plan fails closed until the user saves or discards it.
            // 中文：待处理的 ArcGIS 编辑事务不一定会更新文件 GDB 的时间戳。将该状态视为不可变更，并让写入计划失败关闭，直到用户保存或放弃编辑。
            if (Project.Current?.HasEdits == true)
                return "unreliable:pending-edits";
            var snapshot = await GetMapContextAsync();
            if (snapshot.Layers.Any(layer =>
                    !string.IsNullOrWhiteSpace(layer.DatasetId)
                    && !layer.StateFingerprintReliable))
                return "unreliable:non-file-backed-store";
            string material = string.Join(
                "\n",
                snapshot.Layers
                    .OrderBy(layer => layer.MapMemberId, StringComparer.Ordinal)
                    .ThenBy(layer => layer.DatasetId, StringComparer.Ordinal)
                    .Select(layer =>
                        $"{layer.MapMemberId}|{layer.DatasetId}|{layer.StateFingerprint}"));
            return StableId("content", material);
        }

        private static string BuildStateFingerprint(
            string dataSource,
            string datasetName,
            IEnumerable<string> schema,
            long rowCount,
            long storageRevision)
        {
            return StableId(
                "state",
                $"{datasetName}|{rowCount}|{storageRevision}|{string.Join("|", schema)}");
        }

        private static string BuildSchemaFingerprint(
            string datasetName,
            IEnumerable<string> schema) =>
            StableId("schema", $"{datasetName}|{string.Join("|", schema)}");

        private static long GetStorageRevision(
            string dataSource,
            IDictionary<string, long> cache)
        {
            if (cache.TryGetValue(dataSource ?? "", out long cached))
                return cached;

            long revision = 0;
            try
            {
                if (Directory.Exists(dataSource))
                {
                    revision = Directory
                        .EnumerateFiles(dataSource, "*", SearchOption.TopDirectoryOnly)
                        .Where(path => !IsVolatileStorageFile(path))
                        .Select(path => File.GetLastWriteTimeUtc(path).Ticks)
                        .DefaultIfEmpty(
                            Directory.GetLastWriteTimeUtc(dataSource).Ticks)
                        .Max();
                }
                else if (File.Exists(dataSource))
                {
                    revision = File.GetLastWriteTimeUtc(dataSource).Ticks;
                }
            }
            catch (Exception ex)
            {
                AgentDiagnostics.Warn("MapReader.GetStorageRevision",
                    $"Failed to read storage revision for '{dataSource}' — revision set to 0.", ex);
                revision = 0;
            }

            cache[dataSource ?? ""] = revision;
            return revision;
        }

        private static bool IsFileBacked(string dataSource) =>
            !string.IsNullOrWhiteSpace(dataSource)
            && (Directory.Exists(dataSource) || File.Exists(dataSource));

        private static bool IsVolatileStorageFile(string path)
        {
            string name = Path.GetFileName(path);
            return name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".sr.lock", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
        }

        private static string StableId(string kind, string material)
        {
            byte[] hash = SHA256.HashData(
                Encoding.UTF8.GetBytes(material ?? ""));
            return $"{kind}:{Convert.ToHexString(hash)[..24].ToLowerInvariant()}";
        }

        internal static string BuildDatasetId(FeatureClass? featureClass)
        {
            if (featureClass == null)
                return "";
            string dataSource = FileSystemDataSourcePath.Normalize(
                featureClass.GetDatastore()?.GetPath());
            return StableId(
                "dataset",
                $"{dataSource}|{featureClass.GetName()}");
        }

        /// <summary>
        /// Finds a Table by name in both feature layers and standalone tables.
        /// Returns (null, false) when no match is found in either collection.
        /// </summary>
        /// <remarks>中文：在特征图层和独立表中，按名称查找表。当任一集合中都未找到匹配时返回（空、假）。</remarks>
        private static (Table? table, bool isStandalone) FindTable(string name, Map map)
        {
            // 1. Try feature layers first (most common case).
            // 中文：1. 先尝试特征层（最常见的情况）。
            foreach (var layer in map.GetLayersAsFlattenedList())
            {
                if (!layer.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                if (layer is FeatureLayer fl)
                {
                    var fc = fl.GetFeatureClass();
                    if (fc != null) return (fc, false);
                }
            }

            // 2. Try standalone tables (e.g. Statistics output, GP result tables).
            // 中文：2. 尝试独立表格（例如统计输出、GP结果表）。
            foreach (var table in map.StandaloneTables)
            {
                if (!table.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                var t = table.GetTable();
                if (t != null) return (t, true);
            }

            return (null, false);
        }
        public async Task<string> GetMapInfoAsync()
        {
            return await QueuedTask.Run(() =>
            {
                var view = MapView.Active;
                var map = view?.Map;
                if (map == null) return JsonConvert.SerializeObject(new { error = "无活动地图" });

                var extent = view!.Extent;
                return JsonConvert.SerializeObject(new
                {
                    name = map.Name,
                    spatialReference = map.SpatialReference?.Name ?? "未知",
                    wkid = map.SpatialReference?.Wkid.ToString() ?? "",
                    extent = extent != null ? new { xmin = extent.XMin, ymin = extent.YMin, xmax = extent.XMax, ymax = extent.YMax } : null
                });
            });
        }

        public async Task<string> ListLayersAsync()
        {
            return await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return JsonConvert.SerializeObject(new { error = "无活动地图" });

                var all = map.GetLayersAsFlattenedList().Select(l => new
                {
                    name = l.Name,
                    type = l.GetType().Name,
                    datasource = (l as FeatureLayer)?.GetFeatureClass()?.GetDatastore()?.GetPath()?.ToString() ?? ""
                }).ToList();

                // Include standalone tables (e.g. Statistics output, GP result tables).
                // 中文：包括独立表格（例如统计数据输出、GP结果表）。
                var tables = map.StandaloneTables.Select(t => new
                {
                    name = t.Name,
                    type = "StandaloneTable",
                    datasource = t.GetTable()?.GetDatastore()?.GetPath()?.ToString() ?? ""
                }).ToList();

                all.AddRange(tables);

                // ★ 排除 agentbackup.gdb 内的数据——备份不应暴露给 LLM，
                // 否则会导致"备份→加入地图→LLM 看到→再次备份备份"的套娃循环。
                // English：Exclude data from agentbackup.gdb—backups should not be exposed to the LLM, otherwise it will cause a nesting loop where the "backup→ added to the map→ LLM sees →back up backups again".
                var layers = all.Where(it =>
                    !(it.datasource ?? "").Contains("agentbackup.gdb", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                return JsonConvert.SerializeObject(new { count = layers.Count, layers });
            });
        }

        /// <summary>
        /// 小样本读行（cc_read_rows 后端）。保守调用策略（2026-07-19 用户裁定）：
        /// 匹配行数超过 maxRows 时拒绝返回行数据——部分可见比不可见更误导，
        /// 类别性问题应走 Statistics/唯一值统计合并同类项。
        /// </summary>
        /// <remarks>English：Fewsample line reading (cc_read_rows backend). Conservative Call Policy (User Rule on 2026-07-19): Reject row data when the number of matching rows exceeds maxRows—partially visible is more misleading than invisible. Categorical issues should be handled by Statistics/unique value statistics merging similar items.</remarks>
        public async Task<string> ReadRowsAsync(
            string layerName,
            IReadOnlyList<string> fields,
            string? whereClause,
            int maxRows,
            bool selectedOnly = false)
        {
            return await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return JsonConvert.SerializeObject(new { error = "无活动地图" });

                var (table, isStandalone) = FindTable(layerName, map);
                if (table == null)
                    return JsonConvert.SerializeObject(new { error = $"找不到图层或表「{layerName}」" });

                var def = table.GetDefinition();
                var known = def.GetFields().Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var missing = fields.Where(f => !known.Contains(f)).ToList();
                if (missing.Count > 0)
                    return JsonConvert.SerializeObject(new { error = $"图层/表「{layerName}」中无字段：{string.Join("、", missing)}" });

                var filter = new QueryFilter
                {
                    WhereClause = whereClause ?? "",
                    SubFields = string.Join(",", fields)
                };

                var rows = new List<Dictionary<string, object?>>();
                long matched = 0;
                using (var cursor = table.Search(filter, false))
                {
                    while (cursor.MoveNext())
                    {
                        matched++;
                        if (matched > maxRows)
                            break; // 超过门槛：只需知道“超了”，不再读取。 / Over the threshold: knowing that is enough, so stop reading.

                        var row = cursor.Current;
                        var record = new Dictionary<string, object?>();
                        foreach (string f in fields)
                        {
                            object? v = row[f];
                            record[f] = v is DBNull ? null : v;
                        }
                        rows.Add(record);
                    }
                }

                if (matched > maxRows)
                    return JsonConvert.SerializeObject(new
                    {
                        error = $"匹配行数超过 {maxRows} 行（小样本核对门槛），已拒绝读取。" +
                                "类别性问题请用 analysis_Statistics 按字段合并同类项，或加 where_clause 收窄范围。",
                        matched_over = maxRows
                    });

                return JsonConvert.SerializeObject(new
                {
                    layer = layerName,
                    standalone = isStandalone,
                    where = whereClause ?? "",
                    row_count = rows.Count,
                    rows
                });
            });
        }

        /// <summary>
        /// 字段唯一值扫描（合并同类项）：全表只保留 distinct 集合，超过
        /// maxDistinct 即中止——类别字段的唯一值数量在标准数量级内，
        /// 超限说明该字段不具类别意义（cc_map_field_values 前置）。
        /// </summary>
        /// <remarks>English：Field Unique Value Scan (Merging Items of the Same Category): The entire table only retains the distinct set; exceeding maxDistinct terminates the count — the unique value of a category field is within the standard order of magnitude; exceeding the limit means the field does not have category meaning (cc_map_field_values prefix).</remarks>
        public async Task<string> GetDistinctValuesAsync(string layerName, string fieldName, int maxDistinct)
        {
            return await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return JsonConvert.SerializeObject(new { error = "无活动地图" });

                var (table, isStandalone) = FindTable(layerName, map);
                if (table == null)
                    return JsonConvert.SerializeObject(new { error = $"找不到图层或表「{layerName}」" });

                if (table.GetDefinition().FindField(fieldName) < 0)
                    return JsonConvert.SerializeObject(new { error = $"图层/表「{layerName}」中无字段「{fieldName}」" });

                var distinct = new HashSet<string>(StringComparer.Ordinal);
                long scanned = 0, nulls = 0;
                bool exceeded = false;

                var filter = new QueryFilter { SubFields = fieldName };
                using (var cursor = table.Search(filter, true)) // 回收游标：只收集值。 / Recycling cursor: values only.
                {
                    while (cursor.MoveNext())
                    {
                        scanned++;
                        object? v = cursor.Current[fieldName];
                        if (v == null || v is DBNull) { nulls++; continue; }

                        distinct.Add(v.ToString() ?? "");
                        if (distinct.Count > maxDistinct)
                        {
                            exceeded = true;
                            break;
                        }
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    layer = layerName,
                    standalone = isStandalone,
                    field = fieldName,
                    distinct_count = distinct.Count,
                    exceeded,
                    scanned,
                    null_count = nulls,
                    values = exceeded ? new List<string>() : distinct.OrderBy(s => s, StringComparer.Ordinal).ToList()
                });
            });
        }

        public async Task<string> DescribeLayerAsync(string layerName)
        {            return await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return JsonConvert.SerializeObject(new { error = "无活动地图" });

                var (table, isStandalone) = FindTable(layerName, map);
                if (table == null)
                    return JsonConvert.SerializeObject(new { error = $"找不到图层或表「{layerName}」" });

                var def = table.GetDefinition();
                var allFields = def.GetFields().ToList();

                // Build field info with value distribution
                // 中文：构建场地信息与价值分布
                var fields = new List<object>();
                foreach (var f in allFields)
                {
                    if (f.FieldType == FieldType.Geometry || f.FieldType == FieldType.Blob || f.FieldType == FieldType.Raster)
                    {
                        fields.Add(new { name = f.Name, alias = f.AliasName, type = f.FieldType.ToString(), length = f.Length });
                        continue;
                    }

                    // Sample values for categorical/numeric fields
                    // 中文：范畴/数值字段的样本值
                    object? valueInfo = null;
                    int nullCount = 0;
                    try
                    {
                        using var cursor = table.Search(null, false); // 非回收游标。 / Non-recycling cursor.
                        var valueCounts = new Dictionary<string, int>();
                        double? minVal = null, maxVal = null;
                        int sampled = 0;
                        const int MAX_SAMPLE = 1000;

                        while (cursor.MoveNext() && sampled < MAX_SAMPLE)
                        {
                            var row = cursor.Current;
                            var val = row[f.Name];
                            if (val == null || val is DBNull) { nullCount++; sampled++; continue; }

                            string key = val.ToString() ?? "";
                            if (valueCounts.ContainsKey(key))
                                valueCounts[key]++;
                            else
                                valueCounts[key] = 1;

                            if (double.TryParse(key, out double numVal))
                            {
                                minVal = minVal.HasValue ? Math.Min(minVal.Value, numVal) : numVal;
                                maxVal = maxVal.HasValue ? Math.Max(maxVal.Value, numVal) : numVal;
                            }
                            sampled++;
                        }

                        // Only include distribution if reasonable cardinality
                        // 中文：只有当基数合理时才包含分布
                        if (valueCounts.Count <= 20 && valueCounts.Count > 0)
                        {
                            var sorted = valueCounts.OrderByDescending(kv => kv.Value).ToList();
                            valueInfo = new
                            {
                                distinct_count = valueCounts.Count,
                                values = sorted.Select(kv => new { value = kv.Key, count = kv.Value }),
                                min = minVal,
                                max = maxVal
                            };
                        }
                        else if (minVal.HasValue)
                        {
                            valueInfo = new { min = minVal, max = maxVal, distinct_count = valueCounts.Count, note = "值种类过多，仅显示范围" };
                        }
                    }
                    catch (Exception ex)
                    {
                        // 采样是 best-effort 增强——失败不中断描述流程。
                        // 但必须留诊断信号：LLM 拿不到此字段的值分布时，
                        // 排查人员需能从调试输出中定位。
                        // English：Sampling is a best-effort enhancement—failure does not interrupt the description process. However, diagnostic signals must be retained: if the LLM cannot obtain the value distribution of this field, the investigator must be able to locate it from the debugging output.
                        System.Diagnostics.Debug.WriteLine(
                            $"[MapReader] Field sampling failed for '{f.Name}' " +
                            $"in layer/table '{layerName}': {ex.GetType().Name}: {ex.Message}");
                    }

                    fields.Add(new
                    {
                        name = f.Name,
                        alias = f.AliasName,
                        type = f.FieldType.ToString(),
                        length = f.Length,
                        null_count = nullCount,
                        sample = valueInfo
                    });
                }

                string? geometryType = isStandalone
                    ? null
                    : (table is FeatureClass fc ? fc.GetDefinition().GetShapeType().ToString() : null);

                return JsonConvert.SerializeObject(new
                {
                    name = layerName,
                    standalone = isStandalone,
                    geometryType,
                    featureCount = table.GetCount(),
                    fields
                });
            });
        }

        public async Task<string> GetAllLayerSummariesAsync()
        {
            return await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return "";

                var summaries = new List<string>();
                foreach (var layer in map.GetLayersAsFlattenedList())
                {
                    if (layer is not FeatureLayer fl) continue;
                    // 排除 agentbackup.gdb 内的数据
                    string ds = fl.GetFeatureClass()?.GetDatastore()?.GetPath()?.ToString() ?? "";
                    if (ds.Contains("agentbackup.gdb", StringComparison.OrdinalIgnoreCase)) continue;

                    try
                    {
                        var fc = fl.GetFeatureClass();
                        if (fc == null) continue;
                        var fcDef = fc.GetDefinition();
                        var fieldNames = fcDef.GetFields().Select(f => f.Name).ToList();
                        summaries.Add($"· {layer.Name}：{fcDef.GetShapeType()}，{fc.GetCount()} 个要素，字段：{string.Join("、", fieldNames)}");
                    }
                    catch (Exception ex)
                    {
                        // 单层读取失败不应拖垮整个摘要（其他层仍可用）。
                        // 但必须在摘要中保留一行标记——否则 LLM 以为该层不存在，
                        // 后续工具调用会反复报"找不到图层"而 LLM 不理解为什么。
                        // English：A single-layer read failure should not drag down the entire summary (other layers remain available). However, a line of marker must be kept in the summary—otherwise, the LLM will assume the layer does not exist, and subsequent tool calls will repeatedly report "Layer not found" without the LLM understanding why.
                        System.Diagnostics.Debug.WriteLine(
                            $"[MapReader] Failed to summarize layer '{layer.Name}': " +
                            $"{ex.GetType().Name}: {ex.Message}");
                        summaries.Add($"· {layer.Name}：读取失败（{ex.GetType().Name}）");
                    }
                }

                // Include standalone tables in summary.
                // 中文：总结中包含独立表格。
                foreach (var table in map.StandaloneTables)
                {
                    // 排除 agentbackup.gdb 内的数据
                    string ds = table.GetTable()?.GetDatastore()?.GetPath()?.ToString() ?? "";
                    if (ds.Contains("agentbackup.gdb", StringComparison.OrdinalIgnoreCase)) continue;

                    try
                    {
                        var t = table.GetTable();
                        if (t == null) continue;
                        var def = t.GetDefinition();
                        var fieldNames = def.GetFields().Select(f => f.Name).ToList();
                        summaries.Add($"· [独立表] {table.Name}：{t.GetCount()} 行，字段：{string.Join("、", fieldNames)}");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[MapReader] Failed to summarize standalone table '{table.Name}': " +
                            $"{ex.GetType().Name}: {ex.Message}");
                        summaries.Add($"· [独立表] {table.Name}：读取失败（{ex.GetType().Name}）");
                    }
                }

                return string.Join("\n", summaries);
            });
        }

        public async Task<string> GetSelectionAsync()
        {
            return await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return JsonConvert.SerializeObject(new { error = "无活动地图" });

                var selections = new List<object>();
                foreach (var layer in map.GetLayersAsFlattenedList())
                {
                    if (layer is not FeatureLayer fl) continue;
                    var sel = fl.GetSelection();
                    if (sel.GetCount() > 0)
                        selections.Add(new
                        {
                            layer = layer.Name,
                            count = sel.GetCount(),
                            oid_digest = ComputeObjectIdDigest(sel.GetObjectIDs())
                        });
                }
                return JsonConvert.SerializeObject(new { total = selections.Sum(s => (long)((dynamic)s).count), selections });
            });
        }

        private static string ComputeObjectIdDigest(IEnumerable<long> objectIds)
        {
            using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
                System.Security.Cryptography.HashAlgorithmName.SHA256);
            byte[] buffer = new byte[sizeof(long)];
            foreach (long objectId in objectIds.OrderBy(id => id))
            {
                System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(buffer, objectId);
                hash.AppendData(buffer);
            }
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }

        public async Task<string> GetProjectExtentAsync()
        {
            return await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return JsonConvert.SerializeObject(new { error = "无活动地图" });

                double xmin = double.MaxValue, ymin = double.MaxValue, xmax = double.MinValue, ymax = double.MinValue;
                string wkid = "";
                var layers = new List<object>();

                foreach (var layer in map.GetLayersAsFlattenedList())
                {
                    if (layer is not FeatureLayer fl) continue;
                    try
                    {
                        var env = fl.QueryExtent();
                        if (env == null || env.IsEmpty) continue;
                        xmin = Math.Min(xmin, env.XMin); ymin = Math.Min(ymin, env.YMin);
                        xmax = Math.Max(xmax, env.XMax); ymax = Math.Max(ymax, env.YMax);
                        if (string.IsNullOrEmpty(wkid)) wkid = env.SpatialReference?.Wkid.ToString() ?? "";
                        layers.Add(new { name = layer.Name, xmin = Math.Round(env.XMin, 4), ymin = Math.Round(env.YMin, 4), xmax = Math.Round(env.XMax, 4), ymax = Math.Round(env.YMax, 4) });
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[MapReader] Extent query failed for layer '{layer.Name}': " +
                            $"{ex.GetType().Name}: {ex.Message}");
                    }
                }

                if (xmin == double.MaxValue)
                    return JsonConvert.SerializeObject(new { message = "未找到矢量图层" });

                double width = xmax - xmin, height = ymax - ymin;
                bool isGeographic = wkid is "4326" or "4490" or "4610";
                double approxSqKm = isGeographic
                    ? Math.Abs(width * height * 111.32 * 111.32 * Math.Cos((ymin + ymax) / 2 * Math.PI / 180))
                    : Math.Abs(width * height / 1_000_000);

                return JsonConvert.SerializeObject(new
                {
                    total_extent = new { xmin = Math.Round(xmin, 4), ymin = Math.Round(ymin, 4), xmax = Math.Round(xmax, 4), ymax = Math.Round(ymax, 4), wkid, approximate_sqkm = Math.Round(approxSqKm, 2), is_geographic = isGeographic },
                    per_layer = layers
                }, Formatting.Indented);
            });
        }

        public async Task<SpatialAgent.Runtime.Contracts.SpatialRefContext> GetSpatialRefContextAsync()
        {
            return await QueuedTask.Run(() =>
            {
                var ctx = new SpatialAgent.Runtime.Contracts.SpatialRefContext();
                var map = MapView.Active?.Map;
                if (map == null) return ctx;

                // Map spatial reference
                // 中文：地图空间参考
                var mapSr = map.SpatialReference;
                if (mapSr != null)
                {
                    ctx.MapWkid = mapSr.Wkid;
                    ctx.MapSpatialRefName = mapSr.Name ?? "";
                    ctx.MapSpatialRefLabel = FormatSpatialRefLabel(mapSr.Wkid, mapSr.Name);
                    ctx.DefaultOutputWkid = mapSr.Wkid;
                }

                // Per-layer spatial references
                // 中文：每层空间引用
                foreach (var layer in map.GetLayersAsFlattenedList())
                {
                    if (layer is not FeatureLayer fl) continue;
                    try
                    {
                        // Use GetDefinition().GetSpatialReference() per pitfall #8
                        // 中文：使用GetDefinition（）。GetSpatialReference（） per pitfall #8
                        var fc = fl.GetFeatureClass();
                        var def = fc?.GetDefinition();
                        var layerSr = def?.GetSpatialReference();
                        if (layerSr == null) continue;

                        var layerRef = new SpatialAgent.Runtime.Contracts.LayerSpatialRef
                        {
                            LayerName = layer.Name,
                            Wkid = layerSr.Wkid,
                            SpatialRefLabel = FormatSpatialRefLabel(layerSr.Wkid, layerSr.Name),
                            MatchesMap = layerSr.Wkid == ctx.MapWkid
                        };
                        ctx.Layers.Add(layerRef);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[MapReader] SpatialRef query failed for layer '{layer.Name}': " +
                            $"{ex.GetType().Name}: {ex.Message}");
                    }
                }

                if (ctx.DefaultOutputWkid == 0 && ctx.Layers.Count > 0)
                    ctx.DefaultOutputWkid = ctx.Layers[0].Wkid;

                return ctx;
            });
        }

        /// <summary>
        /// 将图层/表名解析为完整的要素类/表路径，供 CopyFeatures_management 等 GP 工具使用。
        /// GetDatastore().GetPath() 返回的是 URI 格式（file:///...），
        /// 需经 new Uri().LocalPath 转换为 Windows 路径。
        /// </summary>
        /// <remarks>English：Parse layer/table names into complete feature classes/table paths for use by GP tools like CopyFeatures_management. GetDatastore(). GetPath() returns a URI format (file:///... ), requires new Uri(). LocalPath to Windows Path.</remarks>
        public async Task<string?> ResolveLayerPathAsync(string layerName)
        {
            return await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return null;

                // Feature layers
                // 中文：特征层
                foreach (var layer in map.GetLayersAsFlattenedList())
                {
                    if (!layer.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (layer is FeatureLayer fl)
                    {
                        var fc = fl.GetFeatureClass();
                        var gdbPath = fc?.GetDatastore()?.GetPath()?.ToString();
                        if (string.IsNullOrWhiteSpace(gdbPath)) return null;
                        // Convert URI to local path: "file:///G:/..." → "G:\..."
                        // 中文：将URI转换为本地路径：“file:///G:/......”→ “G：\......”
                        string localPath = new Uri(gdbPath).LocalPath;
                        return Path.Combine(localPath, fc!.GetName());
                    }
                }

                // Standalone tables
                // 中文：独立表格
                foreach (var table in map.StandaloneTables)
                {
                    if (!table.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase)) continue;
                    var t = table.GetTable();
                    var gdbPath = t?.GetDatastore()?.GetPath()?.ToString();
                    if (string.IsNullOrWhiteSpace(gdbPath)) return null;
                    string localPath = new Uri(gdbPath).LocalPath;
                    return Path.Combine(localPath, t!.GetName());
                }

                return null;
            });
        }

        private static string FormatSpatialRefLabel(int wkid, string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return $"WKID {wkid}";
            return $"{name} (WKID {wkid})";
        }
    }
}
