using System.Threading;
using SpatialAgent.Adapters.ArcGIS;
using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Tools.Core
{
    /// <summary>
    /// Get current map info (name, CRS, extent).
    /// </summary>
    public class GetMapInfoTool : ITool
    {
        private readonly MapReader _reader = new();
        public string Name => "agent_get_map_info";
        public string Description => "获取当前活动地图的基本信息：名称、坐标系、范围。";
        public string Category => "map";
        public ToolAnnotation Annotation => ToolAnnotation.ReadOnly;
        public IReadOnlyDictionary<string, ParameterProperty> Parameters => new Dictionary<string, ParameterProperty>();
        public VerificationRule? Verification => null;
        public IReadOnlyDictionary<string, string>? DefaultParameters => null;
        public IReadOnlyList<string> RequiredParameters => Array.Empty<string>();

        public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken ct = default)
        {
            try
            {
                string json = await _reader.GetMapInfoAsync();
                return JsonToolResultFactory.FromJson(Name, json);
            }
            catch (Exception ex) { return new ToolResult { ToolName = Name, Success = false, Error = ex.Message }; }
        }
    }

    /// <summary>
    /// List all layers in the current map.
    /// </summary>
    public class ListLayersTool : ITool
    {
        private readonly MapReader _reader = new();
        public string Name => "agent_list_layers";
        public string Description => "列出当前地图中所有图层的名称、类型和数据源。";
        public string Category => "map";
        public ToolAnnotation Annotation => ToolAnnotation.ReadOnly;
        public IReadOnlyDictionary<string, ParameterProperty> Parameters => new Dictionary<string, ParameterProperty>();
        public VerificationRule? Verification => null;
        public IReadOnlyDictionary<string, string>? DefaultParameters => null;
        public IReadOnlyList<string> RequiredParameters => Array.Empty<string>();

        public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken ct = default)
        {
            try
            {
                string json = await _reader.ListLayersAsync();
                return JsonToolResultFactory.FromJson(Name, json);
            }
            catch (Exception ex) { return new ToolResult { ToolName = Name, Success = false, Error = ex.Message }; }
        }
    }

    /// <summary>
    /// Describe a specific layer's fields, geometry type, feature count.
    /// </summary>
    public class DescribeLayerTool : ITool
    {
        private readonly MapReader _reader = new();
        public string Name => "agent_describe_layer";
        public string Description => "获取指定图层的详细信息：字段名、字段类型、几何类型、记录数。";
        public string Category => "map";
        public ToolAnnotation Annotation => ToolAnnotation.ReadOnly;
        public IReadOnlyDictionary<string, ParameterProperty> Parameters => new Dictionary<string, ParameterProperty>
        {
            ["layer_name"] = new() { Type = "string", Description = "要查询的图层名称" }
        };
        public VerificationRule? Verification => null;
        public IReadOnlyDictionary<string, string>? DefaultParameters => null;
        public IReadOnlyList<string> RequiredParameters => new[] { "layer_name" };

        public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken ct = default)
        {
            try
            {
                string name = call.Arguments.GetValueOrDefault("layer_name")?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(name))
                    return new ToolResult { ToolName = Name, Success = false, Error = "请提供 layer_name 参数" };
                string json = await _reader.DescribeLayerAsync(name);
                return JsonToolResultFactory.FromJson(Name, json);
            }
            catch (Exception ex) { return new ToolResult { ToolName = Name, Success = false, Error = ex.Message }; }
        }
    }

    /// <summary>
    /// Get current feature selection.
    /// </summary>
    public class GetSelectionTool : ITool
    {
        private readonly MapReader _reader = new();
        public string Name => "agent_get_selection";
        public string Description => "获取当前用户选中的要素信息。";
        public string Category => "map";
        public ToolAnnotation Annotation => ToolAnnotation.ReadOnly;
        public IReadOnlyDictionary<string, ParameterProperty> Parameters => new Dictionary<string, ParameterProperty>();
        public VerificationRule? Verification => null;
        public IReadOnlyDictionary<string, string>? DefaultParameters => null;
        public IReadOnlyList<string> RequiredParameters => Array.Empty<string>();

        public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken ct = default)
        {
            try
            {
                string json = await _reader.GetSelectionAsync();
                return JsonToolResultFactory.FromJson(Name, json);
            }
            catch (Exception ex) { return new ToolResult { ToolName = Name, Success = false, Error = ex.Message }; }
        }
    }

    /// <summary>
    /// 小样本读行工具（cc_read_rows）。保守调用策略（2026-07-19 用户裁定）：
    /// 仅限 ≤10 行且 ≤3 字段的小样本核对——属性表读取遵循"合并同类项"，
    /// 类别性问题走 Statistics/唯一值统计，不逐行看表。
    /// </summary>
    public class ReadRowsTool : ITool
    {
        public const int MaxRows = 10;
        public const int MaxFields = 3;

        private readonly MapReader _reader = new();
        public string Name => "cc_read_rows";
        public string Description =>
            "读取要素图层或独立表的少量行值，用于小样本核对（如逐条比对两个字段是否对应）。" +
            $"匹配行数尽量不超过 {MaxRows} 行、字段不超过 {MaxFields} 个；超限时强烈建议转用 " +
            "analysis_Statistics 按字段分类合并同类项，或 cc_map_field_values 一步完成字段映射。" +
            "是否读行取决于字段的唯一值数量（distinct_count）：唯一值 ≤10 可逐类抽样，>10 不宜逐行读——" +
            "用 agent_describe_layer 先看 distinct_count 判断。字段含义不明时先反问用户，不要瞎猜。" +
            "★ Statistics 等工具生成的统计输出是独立表（standalone table），不是地图图层——" +
            "agent_describe_layer 找不到它，但 cc_read_rows 的 layer_name 可以直接填统计输出表名。";
        public string Category => "map";
        public ToolAnnotation Annotation => ToolAnnotation.ReadOnly;
        public VerificationRule? Verification => null;
        public IReadOnlyDictionary<string, string>? DefaultParameters => null;

        public IReadOnlyDictionary<string, ParameterProperty> Parameters => new Dictionary<string, ParameterProperty>
        {
            ["layer_name"] = new() { Type = "string", Description = "图层名称" },
            ["fields"] = new() { Type = "string", Description = $"要读取的字段名，逗号/顿号分隔，最多 {MaxFields} 个" },
            ["where_clause"] = new() { Type = "string", Description = "可选 SQL 过滤条件，用于把范围收窄到小样本" }
        };
        public IReadOnlyList<string> RequiredParameters => new[] { "layer_name", "fields" };

        /// <summary>参数门槛校验（纯逻辑，可单测）。返回 null 即通过。</summary>
        public static string? ValidateRequest(string layerName, string fieldsRaw, out List<string> fields)
        {
            fields = (fieldsRaw ?? "")
                .Split(new[] { ',', '，', '、', ';', '；' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(f => f.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (string.IsNullOrWhiteSpace(layerName))
                return "参数 layer_name 不能为空";
            if (fields.Count == 0)
                return "参数 fields 不能为空";
            if (fields.Count > MaxFields)
                return $"字段数 {fields.Count} 超过小样本门槛（最多 {MaxFields} 个）。" +
                       "类别性问题请用 analysis_Statistics 合并同类项，而不是逐行读表。";
            return null;
        }

        public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken ct = default)
        {
            var r = new ToolResult { ToolName = Name };
            try
            {
                string layerName = call.Arguments.GetValueOrDefault("layer_name")?.ToString() ?? "";
                string fieldsRaw = call.Arguments.GetValueOrDefault("fields")?.ToString() ?? "";
                string? where = call.Arguments.GetValueOrDefault("where_clause")?.ToString();

                string? invalid = ValidateRequest(layerName, fieldsRaw, out var fields);
                if (invalid != null)
                {
                    r.Success = false;
                    r.Error = invalid;
                    return r;
                }

                string json = await _reader.ReadRowsAsync(layerName, fields, where, MaxRows);
                return JsonToolResultFactory.FromJson(Name, json);
            }
            catch (Exception ex)
            {
                return new ToolResult { ToolName = Name, Success = false, Error = ex.Message };
            }
        }
    }
}
