using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SpatialAgent.Adapters.ArcGIS;
using SpatialAgent.Runtime.Contracts;
using SpatialAgent.Core;

// ================================================================
// CalculateLengthTool.cs — Single-call composite tool for line length.
//
// Auto-creates the target field if missing, calculates geodesic or
// projected length with unit conversion, and verifies the result.
// ================================================================
// 中文：================================================================ CalculateLengthTool.cs — 单次调用的线长合成工具。如果缺少目标场，自动创建目标场，计算测地线或投影长度并进行单位换算，并验证结果。================================================================
namespace SpatialAgent.Tools.Core.HighLevel
{
    public class CalculateLengthTool : ITool, IToolSafetyDescriptor,
        IToolCapabilityDescriptor
    {
        private readonly GPToolRunner _runner = new();
        private readonly MapReader _mapReader = new();

        public string Name => "cc_calculate_length";
        public string Description =>
            "计算线图层的长度并写入指定字段。自动检查字段是否存在（不存在则创建DOUBLE字段），支持地线长度（椭球测地线）和投影长度。" +
            "单位可选米、千米。一次调用完成全部操作。" +
            "地线长度表达式: !shape.geodesicLength!，投影长度表达式: !shape.length!。";

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
                CapabilityId = GisCapabilityIds.MeasureLength,
                ApplicableGeometryTypes = new[] { "line", "polyline" },
                RequiresPostconditionEvidence = true
            }
        };

        public IReadOnlyDictionary<string, ParameterProperty> Parameters => new Dictionary<string, ParameterProperty>
        {
            ["layer_name"] = new()
            {
                Type = "string",
                Description = "线图层名称（当前地图中存在的要素图层）"
            },
            ["field_name"] = new()
            {
                Type = "string",
                Description = "存储长度的字段名。默认 DXCD（地线长度）。如字段已存在则直接计算，不存在则自动创建。"
            },
            ["length_type"] = new()
            {
                Type = "string",
                Description = "长度类型",
                Enum = new List<string> { "地线长度", "投影长度" }
            },
            ["unit"] = new()
            {
                Type = "string",
                Description = "长度单位",
                Enum = new List<string> { "米", "千米" }
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
            // field_name 省略时实际写入 DXCD——让验证器知道默认字段
            // English：field_name Actually write DXCD when omitted—let the validator know the default field
            Params = "field_default=DXCD",
            FixHint = "检查字段存在且计算后有值"
        };
        public IReadOnlyDictionary<string, string>? DefaultParameters => new Dictionary<string, string>
        {
            ["field_name"] = "DXCD",
            ["length_type"] = "地线长度",
            ["unit"] = "米",
            ["digit"] = "2"
        };
        public IReadOnlyList<string> RequiredParameters => new[] { "layer_name" };

        public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken ct = default)
        {
            var r = new ToolResult { ToolName = Name };
            try
            {
                string layerName = call.Arguments.GetValueOrDefault("layer_name")?.ToString() ?? "";
                string fieldName = call.Arguments.GetValueOrDefault("field_name")?.ToString() ?? "DXCD";
                string lengthType = call.Arguments.GetValueOrDefault("length_type")?.ToString() ?? "地线长度";
                string unit = call.Arguments.GetValueOrDefault("unit")?.ToString() ?? "米";
                int digit = call.Arguments.TryGetValue("digit", out var d) && int.TryParse(d?.ToString(), out var dv) ? dv : 2;

                if (string.IsNullOrWhiteSpace(layerName))
                {
                    r.Success = false;
                    r.Error = "参数 layer_name 不能为空";
                    return r;
                }

                // Step 1: Check existing fields via MapReader (handles threading internally)
                // 中文：步骤1：通过MapReader检查现有字段（内部处理线程）
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
                catch (Exception ex) { AgentDiagnostics.Warn(nameof(CalculateLengthTool), "DescribeLayer failed, assuming field doesn't exist", ex); }

                // Step 2: Create field if not exists (GPToolRunner handles threading)
                // 中文：步骤2：如果不存在，创建字段（GPToolRunner 负责线程处理）
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
                // 中文：步骤3：构建表达式并计算（GPToolRunner 负责线程处理）
                double unitFactor = unit switch
                {
                    "米" => 1,
                    "千米" => 1000,
                    _ => 1
                };

                string shapeExpr = lengthType switch
                {
                    "地线长度" => "geodesicLength",
                    "投影长度" => "length",
                    _ => "geodesicLength"
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
                    length_type = lengthType,
                    unit,
                    expression,
                    summary = calcParsed?.summary?.ToString() ?? (success ? "计算完成" : "计算失败")
                });

                if (!success)
                    r.Error = calcParsed?.summary?.ToString() ?? calcParsed?.error?.ToString() ?? "计算失败";

                // Step 4: Verify via MapReader
                // 中文：步骤4：通过MapReader进行验证
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
