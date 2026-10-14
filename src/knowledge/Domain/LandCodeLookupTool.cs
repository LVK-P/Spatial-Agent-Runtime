using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SpatialAgent.Knowledge.Data;
using SpatialAgent.Runtime.Contracts;

// ────────────────────────────────────────────────────────────────
// LandCodeLookupTool.cs — 用地分类标准对照查询工具（cc_lookup_land_code）。
//
// 把 LandClassificationData 的官方对照表（2023 版用地用海 + 三调地类 +
// 三调→用地用海映射）暴露为模型可调用工具。此前该数据只是 build-time
// 资产，模型做 ydyhmc/ydyhbm 转换时拿不到权威映射，只能"凭记忆编造"
// 或从其他图层猜——2026-07-18 的字段清空事故正源于此。
//
// 真实性约束：查不到的项如实列入 unmatched，绝不猜测；同名多码全部
// 返回为候选，由模型结合上下文选择并向用户说明。
// ────────────────────────────────────────────────────────────────
// English：LandCodeLookupTool.cs — Land use classification standard reference query tool (cc_lookup_land_code). Exposing the official LandClassificationData comparison table (2023 edition of land use sea + three land use classes + three adjustments →land use sea mapping) as a model callable tool. Previously, this data was only a build-time asset. When the model performed the ydyhmc/ydyhbm conversion, it couldn't get authoritative mapping, so it could only 'make up from memory' or guess from other layers—the field clearing incident on 2026-07-18 was precisely due to this. Authenticity constraint: Items that cannot be found are truthfully listed as unmatched and never guessed; All multi-code codes with the same name are returned as candidates, selected by the model based on context and explained to the user.
namespace SpatialAgent.Knowledge
{
    public class LandCodeLookupTool : ITool
    {
        public string Name => "cc_lookup_land_code";

        public string Description =>
            "查询国土行业分类标准官方对照：用地用海分类（2023版，system=yh）与三调地类（system=sd），" +
            "支持代码↔名称双向查询，三调代码可返回对应的用地用海代码映射建议。" +
            "做用地用海名称/代码转换（ydyhmc/ydyhbm）或地类编码相关计算前，必须先用本工具查询官方对照，" +
            "禁止凭记忆编造代码。支持一次查询多个值（逗号、顿号或分号分隔）。查不到的项会如实列出。";

        public string Category => "knowledge";
        public ToolAnnotation Annotation => ToolAnnotation.ReadOnly;
        public VerificationRule? Verification => null;
        public IReadOnlyDictionary<string, string>? DefaultParameters => null;

        public IReadOnlyDictionary<string, ParameterProperty> Parameters => new Dictionary<string, ParameterProperty>
        {
            ["query"] = new()
            {
                Type = "string",
                Description = "要查询的名称或代码，多个值用逗号/顿号/分号分隔。如 '公路用地、0702' 或 '农村宅基地'"
            },
            ["system"] = new()
            {
                Type = "string",
                Description = "分类体系：yh=用地用海分类2023版（默认），sd=第三次国土调查地类",
                Enum = new List<string> { "yh", "sd" }
            },
            ["direction"] = new()
            {
                Type = "string",
                Description = "查询方向：auto 自动判断（默认），name_to_code 名称查代码，code_to_name 代码查名称",
                Enum = new List<string> { "auto", "name_to_code", "code_to_name" }
            }
        };

        public IReadOnlyList<string> RequiredParameters => new[] { "query" };

        private static readonly Regex CodePattern = new(@"^[0-9][0-9A-Za-z]*$", RegexOptions.Compiled);
        private static readonly char[] Separators = { ',', '，', '、', ';', '；', '\n', '\t' };
        private const int MaxCandidates = 8;

        public Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken ct = default)
        {
            var r = new ToolResult { ToolName = Name };
            try
            {
                string query = call.Arguments.GetValueOrDefault("query")?.ToString() ?? "";
                string system = (call.Arguments.GetValueOrDefault("system")?.ToString() ?? "yh")
                    .Trim().ToLowerInvariant();
                string direction = (call.Arguments.GetValueOrDefault("direction")?.ToString() ?? "auto")
                    .Trim().ToLowerInvariant();

                if (string.IsNullOrWhiteSpace(query))
                {
                    r.Success = false;
                    r.Error = "参数 query 不能为空";
                    return Task.FromResult(r);
                }

                var table = system == "sd"
                    ? LandClassificationData.SdCodeToName
                    : LandClassificationData.YhCodeToName;

                var matches = new List<object>();
                var candidates = new List<object>();
                var unmatched = new List<string>();

                var tokens = query.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(t => t.Length > 0)
                    .Distinct()
                    .ToList();

                foreach (string token in tokens)
                {
                    ct.ThrowIfCancellationRequested();

                    bool asCode = direction == "code_to_name"
                        || (direction == "auto" && CodePattern.IsMatch(token));

                    if (asCode)
                    {
                        var hit = table.FirstOrDefault(kv =>
                            kv.Key.Equals(token, StringComparison.OrdinalIgnoreCase));
                        if (hit.Key != null)
                            matches.Add(BuildMatch(system, token, hit.Key, hit.Value));
                        else
                            unmatched.Add(token);
                        continue;
                    }

                    // name → code：精确命中优先（同名多码全部返回）
                    // English：name → code: Accurate hit priority (all duplicate codes are returned)
                    var exact = table.Where(kv => kv.Value == token).ToList();
                    if (exact.Count == 1)
                    {
                        matches.Add(BuildMatch(system, token, exact[0].Key, exact[0].Value));
                        continue;
                    }
                    if (exact.Count > 1)
                    {
                        candidates.Add(new
                        {
                            query = token,
                            note = "同名对应多个代码，需按层级语境选择",
                            options = exact.Select(kv => new { code = kv.Key, name = kv.Value }).ToList()
                        });
                        continue;
                    }

                    // 模糊：名称包含查询词或查询词包含名称
                    // English：Fuzzy: Name contains query terms or query words contain names
                    var fuzzy = table
                        .Where(kv => kv.Value.Contains(token, StringComparison.Ordinal)
                                  || token.Contains(kv.Value, StringComparison.Ordinal))
                        .OrderBy(kv => kv.Key.Length)   // 上级类目（短码）优先展示
                        .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                        .Take(MaxCandidates)
                        .ToList();

                    if (fuzzy.Count > 0)
                        candidates.Add(new
                        {
                            query = token,
                            note = "无精确同名项，以下为近似候选",
                            options = fuzzy.Select(kv => new { code = kv.Key, name = kv.Value }).ToList()
                        });
                    else
                        unmatched.Add(token);
                }

                r.Success = true;
                r.Output = JsonConvert.SerializeObject(new
                {
                    success = true,
                    system = system == "sd" ? "三调地类（GB/T 21010-2017）" : "用地用海分类（2023版）",
                    matches,
                    candidates,
                    unmatched,
                    note = unmatched.Count > 0
                        ? "unmatched 中的项在该分类体系中无对应条目，不得编造代码；可尝试切换 system 或让用户确认名称。"
                        : "对照结果来自官方分类表，可直接用于字段计算。"
                });
                return Task.FromResult(r);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Task.FromResult(new ToolResult { ToolName = Name, Success = false, Error = ex.Message });
            }
        }

        private static object BuildMatch(string system, string query, string code, string name)
        {
            // 三调代码附带用地用海映射建议（一对多时全部列出，由模型按语境判断）
            // English：Third-level code includes land use and sea use mapping suggestions (all listed in one-to-many scenarios, judged by the model based on context)
            if (system == "sd" &&
                LandClassificationData.SdToYh.TryGetValue(code, out string[]? yhCodes) &&
                yhCodes is { Length: > 0 })
            {
                var suggestions = yhCodes
                    .Select(c => new
                    {
                        code = c,
                        name = LandClassificationData.YhCodeToName.TryGetValue(c, out string? n) ? n : ""
                    })
                    .ToList();
                return new { query, code, name, yh_suggestions = suggestions };
            }

            return new { query, code, name };
        }
    }
}
