using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SpatialAgent.Adapters.ArcGIS;
using SpatialAgent.Knowledge.Data;
using SpatialAgent.Runtime.Contracts;

// ────────────────────────────────────────────────────────────────
// MapFieldValuesTool.cs — cc_map_field_values：字段→字段映射赋值。
//
// 遵循 ArcGIS Pro"计算字段"的标准形态（2026-07-19 用户裁定）：
// expression 调用 code_block 内定义的映射函数，与规划师手工操作
// 完全同构——而非让模型逐行读属性表（百万级数据不可行）。
//
// 可信路线："合并同类项"——只扫源字段唯一值（有上限），查官方对照
// 表建映射，缺失映射保留目标字段原值，未映射/歧义项如实入清单。
// 模型的自由度收敛为填参数：忘调查表、手写表达式出错、空值兜底
// 清数据三类事故在工具内部被结构性消灭。
// ────────────────────────────────────────────────────────────────
namespace SpatialAgent.Services
{
    public class MapFieldValuesTool : ITool, IToolSafetyDescriptor
    {
        /// <summary>唯一值上限：类别字段的取值数在标准数量级内，超限即判定不具映射意义。</summary>
        public const int MaxDistinctValues = 200;

        private readonly MapReader _reader = new();
        private readonly GPToolRunner _runner = new();

        public string Name => "cc_map_field_values";

        public string Description =>
            "按映射关系为字段批量赋值（同图层内 源字段→目标字段），等价于 Pro 的计算字段+Python 代码块。" +
            "用地名称/代码转换（ydyhmc→ydyhbm 等）必须用本工具：内部自动扫描源字段唯一值、" +
            "查询官方分类对照表（2023 用地用海/三调）、生成保值映射表达式并执行。" +
            "映射缺失的行保留目标字段原值；查不到或一名多码的项会如实列出，不会编造。" +
            "也可通过 mapping 参数显式提供 JSON 映射（此时不查对照表）。";

        public string Category => "analysis";
        public ToolAnnotation Annotation => ToolAnnotation.Modify;
        public ToolSafetyProfile SafetyProfile => new()
        {
            DeclaredRisk = ToolRiskLevel.Modify,
            MaxInternalRisk = ToolRiskLevel.Modify,
            Capabilities = ToolSafetyCapability.ReadsData
                | ToolSafetyCapability.MutatesExistingData
                | ToolSafetyCapability.Composite
                | ToolSafetyCapability.RequiresReliableScope
        };

        public VerificationRule? Verification => new()
        {
            Type = "verify_field_populated",
            FixHint = "检查目标字段计算后有值；若全空说明映射未生效"
        };

        public IReadOnlyDictionary<string, string>? DefaultParameters => null;

        public IReadOnlyDictionary<string, ParameterProperty> Parameters => new Dictionary<string, ParameterProperty>
        {
            ["in_table"] = new() { Type = "string", Description = "图层名称" },
            ["source_field"] = new() { Type = "string", Description = "映射依据字段（如 YDYHMC）" },
            ["field_name"] = new() { Type = "string", Description = "要写入的目标字段（如 YDYHBM，须已存在）" },
            ["mapping_source"] = new()
            {
                Type = "string",
                Description = "官方对照方向：yh_name_to_code=用地用海名称→代码（默认），yh_code_to_name，sd_name_to_code，sd_code_to_name",
                Enum = new List<string> { "yh_name_to_code", "yh_code_to_name", "sd_name_to_code", "sd_code_to_name" }
            },
            ["mapping"] = new()
            {
                Type = "string",
                Description = "可选：显式 JSON 映射对象（如 {\"公路用地\":\"1202\"}），提供时优先于官方对照表"
            }
        };

        public IReadOnlyList<string> RequiredParameters => new[] { "in_table", "source_field", "field_name" };

        public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken ct = default)
        {
            var r = new ToolResult { ToolName = Name };
            try
            {
                string inTable = call.Arguments.GetValueOrDefault("in_table")?.ToString() ?? "";
                string sourceField = call.Arguments.GetValueOrDefault("source_field")?.ToString() ?? "";
                string targetField = call.Arguments.GetValueOrDefault("field_name")?.ToString() ?? "";
                string mappingSource = call.Arguments.GetValueOrDefault("mapping_source")?.ToString() ?? "yh_name_to_code";
                string? explicitMapping = call.Arguments.GetValueOrDefault("mapping")?.ToString();

                if (string.IsNullOrWhiteSpace(inTable) || string.IsNullOrWhiteSpace(sourceField) || string.IsNullOrWhiteSpace(targetField))
                {
                    r.Success = false;
                    r.Error = "in_table / source_field / field_name 均不能为空";
                    return r;
                }

                // 1. 合并同类项：只扫唯一值，绝不逐行搬运数据
                string distinctJson = await _reader.GetDistinctValuesAsync(inTable, sourceField, MaxDistinctValues);
                var distinctInfo = JObject.Parse(distinctJson);
                if (distinctInfo["error"] != null)
                {
                    r.Success = false;
                    r.Error = distinctInfo["error"]!.ToString();
                    return r;
                }
                if (distinctInfo["exceeded"]?.Value<bool>() == true)
                {
                    r.Success = false;
                    r.Error = $"源字段「{sourceField}」唯一值超过 {MaxDistinctValues} 个，不具类别映射意义——" +
                              "请确认字段是否选对，或先向用户澄清任务。";
                    return r;
                }

                var sourceValues = (distinctInfo["values"] as JArray)?
                    .Select(v => v.ToString())
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .ToList() ?? new List<string>();

                if (sourceValues.Count == 0)
                {
                    r.Success = false;
                    r.Error = $"源字段「{sourceField}」没有非空值，无从映射。请先确认数据。";
                    return r;
                }

                // 2. 构建映射（显式 mapping 优先；否则查官方对照表）——纯逻辑，可单测
                var build = BuildMapping(sourceValues, mappingSource, explicitMapping);
                if (build.Error != null)
                {
                    r.Success = false;
                    r.Error = build.Error;
                    return r;
                }
                if (build.Matched.Count == 0)
                {
                    r.Success = false;
                    r.Error = "没有任何源值能建立映射（全部未命中或歧义）。" +
                              $"未命中：{string.Join("、", build.Unmatched.Take(10))}；" +
                              $"歧义：{string.Join("、", build.Ambiguous.Select(a => a.Key).Take(10))}。" +
                              "请用 cc_lookup_land_code 核对或提供显式 mapping。";
                    return r;
                }

                // 3. 生成 Pro 计算字段标准形态：expression + code_block（缺失保原值）
                string codeBlock = BuildCodeBlock(build.Matched);
                string expression = $"_sa_map(!{sourceField}!, !{targetField}!)";

                string calcJson = await _runner.ExecuteAsync("management.CalculateField",
                    new List<string> { inTable, targetField, expression, "PYTHON3", codeBlock }, ct);
                var calcParsed = JsonConvert.DeserializeObject<dynamic>(calcJson);
                bool ok = calcParsed?.success == true;

                r.Success = ok;
                r.Output = JsonConvert.SerializeObject(new
                {
                    success = ok,
                    layer = inTable,
                    source_field = sourceField,
                    field = targetField,
                    mapped_pairs = build.Matched.Count,
                    unmatched = build.Unmatched,       // 如实：这些源值没有官方对应，对应行保留原值
                    ambiguous = build.Ambiguous.Select(a => new { value = a.Key, candidates = a.Value }),
                    expression,
                    summary = ok
                        ? $"已按 {build.Matched.Count} 组映射写入「{targetField}」；" +
                          $"未映射 {build.Unmatched.Count} 项、歧义 {build.Ambiguous.Count} 项的行保留原值，需向用户说明"
                        : calcParsed?.summary?.ToString() ?? "计算失败"
                });
                if (!ok)
                    r.Error = calcParsed?.summary?.ToString() ?? calcParsed?.error?.ToString() ?? "CalculateField 失败";
                return r;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return new ToolResult { ToolName = Name, Success = false, Error = ex.Message };
            }
        }

        // ── 纯逻辑（可单测）──

        public sealed class MappingBuildResult
        {
            public Dictionary<string, string> Matched { get; } = new(StringComparer.Ordinal);
            public List<string> Unmatched { get; } = new();
            public List<KeyValuePair<string, List<string>>> Ambiguous { get; } = new();
            public string? Error { get; set; }
        }

        /// <summary>
        /// 为源字段唯一值建立映射。显式 mapping 优先；否则按 mapping_source
        /// 查 LandClassificationData。一名多码不猜（入 Ambiguous），查不到不编（入 Unmatched）。
        /// </summary>
        public static MappingBuildResult BuildMapping(
            IReadOnlyList<string> sourceValues, string mappingSource, string? explicitMappingJson)
        {
            var result = new MappingBuildResult();

            Dictionary<string, string>? explicitMap = null;
            if (!string.IsNullOrWhiteSpace(explicitMappingJson))
            {
                try
                {
                    explicitMap = JsonConvert.DeserializeObject<Dictionary<string, string>>(explicitMappingJson);
                }
                catch (Exception ex)
                {
                    result.Error = $"mapping 参数不是合法 JSON 对象：{ex.Message}";
                    return result;
                }
            }

            // 官方表：code→name 正查；name→code 反转（同名多码 → 歧义列表）
            Dictionary<string, string> codeToName = mappingSource.StartsWith("sd", StringComparison.OrdinalIgnoreCase)
                ? LandClassificationData.SdCodeToName
                : LandClassificationData.YhCodeToName;
            bool nameToCode = mappingSource.EndsWith("name_to_code", StringComparison.OrdinalIgnoreCase);

            var nameToCodes = nameToCode
                ? codeToName.GroupBy(kv => kv.Value)
                    .ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).OrderBy(c => c.Length).ThenBy(c => c, StringComparer.Ordinal).ToList())
                : null;

            foreach (string value in sourceValues.Distinct(StringComparer.Ordinal))
            {
                string key = value.Trim();
                if (key.Length == 0) continue;

                if (explicitMap != null)
                {
                    if (explicitMap.TryGetValue(key, out string? target) && !string.IsNullOrWhiteSpace(target))
                        result.Matched[key] = target;
                    else
                        result.Unmatched.Add(key);
                    continue;
                }

                if (nameToCode)
                {
                    if (nameToCodes!.TryGetValue(key, out var codes))
                    {
                        if (codes.Count == 1)
                            result.Matched[key] = codes[0];
                        else
                            result.Ambiguous.Add(new(key, codes));
                    }
                    else
                    {
                        result.Unmatched.Add(key);
                    }
                }
                else
                {
                    var hit = codeToName.FirstOrDefault(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                    if (hit.Key != null)
                        result.Matched[key] = hit.Value;
                    else
                        result.Unmatched.Add(key);
                }
            }

            return result;
        }

        /// <summary>
        /// 生成 Pro 计算字段的 code_block：映射缺失时返回目标字段当前值（保值），
        /// 键值经 JSON 转义防引号/反斜杠注入。
        /// </summary>
        public static string BuildCodeBlock(IReadOnlyDictionary<string, string> matched)
        {
            var sb = new StringBuilder();
            sb.AppendLine("def _sa_map(v, cur):");
            sb.AppendLine("    m = {");
            foreach (var kv in matched.OrderBy(k => k.Key, StringComparer.Ordinal))
                sb.AppendLine($"        {ToPyString(kv.Key)}: {ToPyString(kv.Value)},");
            sb.AppendLine("    }");
            sb.AppendLine("    if v is None:");
            sb.AppendLine("        return cur");
            sb.AppendLine("    return m.get(str(v).strip(), cur)");
            return sb.ToString();
        }

        private static string ToPyString(string value) =>
            JsonConvert.ToString(value); // JSON 字符串字面量与 Python 兼容（双引号+转义）
    }
}
