using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SpatialAgent.Adapters.ArcGIS;
using SpatialAgent.Runtime.Contracts;
using SpatialAgent.Core;

// ================================================================
// CalculateAreaTool.cs — Single-call composite tool for polygon area.
//
// Auto-creates the target field if missing, calculates geodesic or
// projected area with unit conversion, and verifies the result.
// ================================================================
namespace SpatialAgent.Tools.Core.HighLevel
{
    public class CalculateAreaTool : ITool, IToolSafetyDescriptor,
        IToolCapabilityDescriptor
    {
        private readonly GPToolRunner _runner = new();
        private readonly MapReader _mapReader = new();

        public string Name => "cc_calculate_area";
        public string Description =>
            "计算面图层的面积并写入指定字段。自动检查字段是否存在（不存在则创建DOUBLE字段），支持椭球面积和投影面积。" +
            "单位可选平方米、公顷、平方公里、亩。一次调用完成全部操作。" +
            "椭球面积表达式: !shape.geodesicArea!，投影面积表达式: !shape.area!。";

        public string Category => "analysis";
        public ToolAnnotation Annotation => ToolAnnotation.Modify;
        public ToolSafetyProfile SafetyProfile => new()
        {
            DeclaredRisk = ToolRiskLevel.Modify,
            MaxInternalRisk = ToolRiskLevel.Modify,
            Capabilities = ToolSafetyCapability.ReadsData
                | ToolSafetyCapability.MutatesExistingData
                | ToolSafetyCapability.ChangesSchema
                | ToolSafetyCapability.Composite
                | ToolSafetyCapability.RequiresReliableScope
        };
        public IReadOnlyList<ToolCapabilityClaim> CapabilityClaims => new[]
        {
            new ToolCapabilityClaim
            {
                CapabilityId = GisCapabilityIds.MeasureArea,
                ApplicableGeometryTypes = new[] { "polygon" },
                RequiresPostconditionEvidence = true
            }
        };

        public IReadOnlyDictionary<string, ParameterProperty> Parameters => new Dictionary<string, ParameterProperty>
        {
            ["layer_name"] = new()
            {
                Type = "string",
                Description = "面图层名称（当前地图中存在的要素图层）"
            },
            ["field_name"] = new()
            {
                Type = "string",
                Description = "存储面积的字段名。默认 TQMJ（椭球面积）。如字段已存在则直接计算，不存在则自动创建。"
            },
            ["area_type"] = new()
            {
                Type = "string",
                Description = "面积类型",
                Enum = new List<string> { "椭球面积", "投影面积" }
            },
            ["unit"] = new()
            {
                Type = "string",
                Description = "面积单位",
                Enum = new List<string> { "平方米", "公顷", "平方公里", "亩" }
            },
            ["digit"] = new()
            {
                Type = "integer",
                Description = "保留小数位数，默认2"
            }
        };

        public VerificationRule? Verification => new()
        {
            Type = "verify_field_populated",
            // field_name 省略时实际写入 TQMJ——让验证器知道默认字段，
            // 而不是靠参数顺序猜出"椭球面积"这种假字段名
            Params = "field_default=TQMJ",
            FixHint = "检查字段存在且计算后有值"
        };
        public IReadOnlyDictionary<string, string>? DefaultParameters => new Dictionary<string, string>
        {
            ["field_name"] = "TQMJ",
            ["area_type"] = "椭球面积",
            ["unit"] = "平方米",
            ["digit"] = "2"
        };
        public IReadOnlyList<string> RequiredParameters => new[] { "layer_name" };

        public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken ct = default)
        {
            var r = new ToolResult { ToolName = Name };
            try
            {
                string layerName = call.Arguments.GetValueOrDefault("layer_name")?.ToString() ?? "";
                string fieldName = call.Arguments.GetValueOrDefault("field_name")?.ToString() ?? "TQMJ";
                string areaType = call.Arguments.GetValueOrDefault("area_type")?.ToString() ?? "椭球面积";
                string unit = call.Arguments.GetValueOrDefault("unit")?.ToString() ?? "平方米";
                int digit = call.Arguments.TryGetValue("digit", out var d) && int.TryParse(d?.ToString(), out var dv) ? dv : 2;

                if (string.IsNullOrWhiteSpace(layerName))
                {
                    r.Success = false;
                    r.Error = "参数 layer_name 不能为空";
                    return r;
                }

                // Step 1: Check existing fields via MapReader (handles threading internally)
                var fields = new List<string>();
                try
                {
                    var layerJson = await _mapReader.DescribeLayerAsync(layerName);
                    var desc = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(layerJson);
                    if (desc?.fields != null)
                    {
                        foreach (var f in desc.fields)
                        {
                            string? fn = f?.name?.ToString() ?? f?.Name?.ToString();
                            if (!string.IsNullOrEmpty(fn)) fields.Add(fn);
                        }
                    }
                }
                catch (Exception ex) { AgentDiagnostics.Warn(nameof(CalculateAreaTool), "DescribeLayer failed, assuming field doesn't exist", ex); }

                // Step 2: Create field if not exists (GPToolRunner handles threading)
                if (!fields.Any(f => f.Equals(fieldName, StringComparison.OrdinalIgnoreCase)))
                {
                    string addResult = await _runner.ExecuteAsync("management.AddField",
                        new List<string> { layerName, fieldName, "DOUBLE", "#", "#", "#", fieldName }, ct);
                    var parsed = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(addResult);
                    if (parsed?.success != true)
                    {
                        r.Success = false;
                        r.Error = $"创建字段 {fieldName} 失败: {parsed?.summary?.ToString() ?? addResult}";
                        return r;
                    }
                }

                // Step 3: Build expression and calculate (GPToolRunner handles threading)
                double unitFactor = unit switch
                {
                    "平方米" => 1,
                    "公顷" => 10000,
                    "平方公里" => 1000000,
                    "亩" => 666.66667,
                    _ => 1
                };

                string shapeExpr = areaType switch
                {
                    "椭球面积" => "geodesicArea",
                    "投影面积" => "area",
                    _ => "geodesicArea"
                };

                string expression = $"round(!shape.{shapeExpr}!/{unitFactor},{digit})";
                string calcResult = await _runner.ExecuteAsync("management.CalculateField",
                    new List<string> { layerName, fieldName, expression, "PYTHON3" }, ct);

                var calcParsed = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(calcResult);
                bool success = calcParsed?.success == true;

                r.Success = success;
                r.Output = Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success,
                    layer = layerName,
                    field = fieldName,
                    area_type = areaType,
                    unit,
                    expression,
                    summary = calcParsed?.summary?.ToString() ?? (success ? "计算完成" : "计算失败")
                });

                if (!success)
                    r.Error = calcParsed?.summary?.ToString() ?? calcParsed?.error?.ToString() ?? "计算失败";

                // Step 4: Verify via MapReader
                if (success)
                {
                    try
                    {
                        var verifyJson = await _mapReader.DescribeLayerAsync(layerName);
                        var vdesc = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(verifyJson);
                        long? count = (long?)vdesc?.featureCount;
                        r.VerificationDetail = count > 0
                            ? $"图层 {layerName} 共 {count} 个要素，字段 {fieldName} 已计算"
                            : $"图层 {layerName} 要素数为 0";
                    }
                    catch
                    {
                        r.VerificationDetail = "无法验证字段值";
                    }
                }

                return r;
            }
            catch (Exception ex)
            {
                return new ToolResult { ToolName = Name, Success = false, Error = ex.Message };
            }
        }
    }
}
