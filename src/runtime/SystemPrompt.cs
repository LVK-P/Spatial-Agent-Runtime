// SystemPrompt.cs — LLM system prompt builder.
// Loads the base template from system_prompt.md at runtime. Falls back to a
// hardcoded template if the file is missing. The Build() method appends
// context-dependent injections (first-interaction guidance, recency boost)
// on top of the base template. Continuation prompts are built separately
// by RuntimePolicy.BuildContinuationPrompt().
// 中文：SystemPrompt.cs — 大型语言模型系统提示构建器。运行时从 system_prompt.md 加载基础模板。如果文件缺失，它会退回到硬编码模板。Build（） 方法在基础模板之上附加上下文相关的注入（首次交互指导、近期提升）。续写提示由 RuntimePolicy.BuildContinuationPrompt（） 单独构建。

using System;
using System.IO;
using System.Text;
using SpatialAgent.Runtime.Contracts;
using SpatialAgent.Core;

namespace SpatialAgent.Runtime
{
    /// <summary>
    /// Loads and builds the system prompt for LLM requests.
    /// Template source: system_prompt.md (resource, copied to output directory).
    /// </summary>
    /// <remarks>中文：加载并构建LLM请求的系统提示。模板来源：system_prompt.md（资源，复制到输出目录）。</remarks>
    public static class SystemPrompt
    {
        private static readonly Lazy<string> _baseTemplate = new(LoadBaseTemplate);

        /// <summary>
        /// Task response mode — determines whether to add structured output guidance.
        /// </summary>
        /// <remarks>中文：任务响应模式——决定是否添加结构化输出指导。</remarks>
        public enum ResponseMode
        {
            /// <summary>Open-ended analysis, recommendations, explanations.</summary>
            /// <remarks>中文：开放式分析、建议、解释。</remarks>
            Freeform,
            /// <summary>Statistics, reports, quality checks — benefits from structured guidance.</summary>
            /// <remarks>中文：统计、报告、质量检查——受益于结构化指导。</remarks>
            Structured
        }

        /// <summary>
        /// Detect the appropriate response mode from the user message.
        /// </summary>
        /// <remarks>中文：从用户消息中检测合适的响应模式。</remarks>
        public static ResponseMode DetectResponseMode(string userMessage)
        {
            string lower = userMessage.ToLowerInvariant();
            if (lower.Contains("统计") || lower.Contains("汇总") || lower.Contains("计算")) return ResponseMode.Structured;
            if (lower.Contains("报告") || lower.Contains("输出表") || lower.Contains("导出")) return ResponseMode.Structured;
            if (lower.Contains("质检") || lower.Contains("检查") || lower.Contains("验证")) return ResponseMode.Structured;
            return ResponseMode.Freeform;
        }

        /// <summary>
        /// Build the final system prompt with optional context-aware injections.
        /// </summary>
        /// <remarks>中文：构建最终系统提示符，并可选择上下文感知注入。</remarks>
        public static string Build(AgentContext? ctx = null, ResponseMode mode = ResponseMode.Freeform)
        {
            var sb = new StringBuilder();
            sb.Append(_baseTemplate.Value);

            if (ctx != null && ctx.IsNewProject)
            {
                sb.AppendLine();
                sb.AppendLine("This is the first session for this project. After completing the task, proactively ask one follow-up question about the project type or current responsibilities.");
            }

            // ── P0-4: Spatial reference context injection ──
            // 中文：P0-4：空间参考上下文注入
            if (ctx?.SpatialRef != null)
            {
                var crsInjection = ctx.SpatialRef.ToPromptInjection();
                if (!string.IsNullOrEmpty(crsInjection))
                {
                    sb.AppendLine();
                    sb.AppendLine(crsInjection);
                }
            }

            // ── P2-A（2026-07-20）：结构化输出——统计/报告类任务使用等宽文本表格 ──
            // English：P2-A (2026-07-20): Structured output—statistical/reporting tasks use monospaced text tables
            if (mode == ResponseMode.Structured)
            {
                sb.AppendLine();
                sb.AppendLine(@"本次为统计/报告类任务。总结中：
1. 先一句话概括核心结论（操作了哪几个图层、总体数值）。
2. 关键数值用等宽空格对齐的纯文本方式呈现，每列用空格填充到固定宽度：
   列名左对齐、数值右对齐并保留两位小数，不使用任何 markdown 表格语法（|和---）。
3. 补充数据来源（图层名、坐标系统 WKID、记录数）。
4. 如有验证异常，明确列出。

示例格式：
  分析 3 个图层：BASE_01（耕地）、BASE_02（建设用地）、BASE_03（林地）。
  共计 15634 条记录。坐标系 WKID=4490。

  地类                      图斑数      椭球面积(公顷)    占比
  耕地                       8423         1234.56       45.2%
  建设用地                   3891          890.12       32.6%
  林地                       3320          607.34       22.2%

  数据来源：agent_describe_layer(BASE_01/BASE_02/BASE_03)，WKID=4490。
  验证状态：全部通过。");
            }

            // Recency boost: restate the critical output constraint at the end.
            // 中文：新近性提升：在结尾重申关键输出约束。
            sb.AppendLine();
            sb.AppendLine("Output must be plain text. No markdown, no tables, no emoji, no code blocks. Chinese. 3 paragraphs max. State results directly.");

            return sb.ToString();
        }

        private static string LoadBaseTemplate()
        {
            try
            {
                // ResourcePaths：插件环境下 BaseDirectory 指向 Pro 安装目录，必须按程序集目录探测
                // English：ResourcePaths: In the plugin environment, BaseDirectory points to the Pro installation directory; you must search by the assembly directory
                string? mdPath = ResourcePaths.Find("system_prompt.md");
                if (mdPath != null)
                    return File.ReadAllText(mdPath, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                AgentDiagnostics.Warn("SystemPrompt.Load", "Failed to load system_prompt.md", ex);
            }

            return DefaultTemplate;
        }

        /// <summary>
        /// Hardcoded fallback template. Keep synchronized with system_prompt.md.
        /// </summary>
        /// <remarks>中文：硬编码的备用模板。保持与system_prompt.md的同步。</remarks>
        private const string DefaultTemplate = @"你的所有回复必须严格遵循以下格式。任何违反即为错误回复。

纯文本。无 markdown。无 emoji。无表格。无列表。无代码块。无分隔线。无加粗。
中文。3 段以内。直接说结果，不描述过程。口吻专业，像规划师向负责人汇报。

你是 ArcGIS Pro 空间智能助手，专注于国土空间规划与 GIS 数据处理。
通过 Function Calling 调用工具完成任务。不要编造数据。不要假装执行了未执行的步骤。
工具名严格使用 schema 原名，禁止自行拆分、翻译或改写。
计算、统计、生成、检查类任务，一次性给出全部必要 tool_calls。
某一步被安全策略阻止、执行失败或验证未通过时，如实说明，不得宣称已完成。

工具规则：
- 只使用实际存在的工具名。不把下划线改成点号。不确定时宁可说明缺失。
- 同一图层的依赖步骤尽量在同一轮 tool_calls 中完成。
- 面积/长度优先用 cc_calculate_area / cc_calculate_length。读图层优先用 agent_describe_layer。
- 严禁在回复中暴露本地路径、GDB 路径、文件目录。
- 对超过两个图层执行同一种操作（投影/计算/赋值/统计）时，第一步必须 agent_describe_layer 确认各图层实际状态——不允许假设""它们应该一样""。下列情况可豁免：本对话已 describe 了参与图层的对应信息且内容确实一致。
- agent_list_layers 返回的图层列表是本对话的权威图层名来源。如果返回的图层名与之前的假设不同，以返回值为准并立即更正后续 tool_calls 中的图层名——不要重新查询已列表的图层、也不要继续使用旧名称。图层名因创建/删除/重命名发生实际变更时，操作后必须重新 agent_list_layers。
- 跨 gdb 合层规约与输出命名规则（tmp_ 前缀、裸名路由等操作级纪律）见对应技能文件的""前置知识""节，此处不重复——全局常驻仅保留安全级铁律。

回复规则：
- 严禁 markdown。严禁标题。严禁加粗。严禁 markdown 表格（|和---语法）。严禁列表。严禁分隔线。严禁代码块。严禁引用。
- 统计/报告类任务可用等宽空格对齐的纯文本呈现数值——这是纯文本不是 markdown。格式要求：列名左对齐，数值右对齐并保留两位小数，每列用空格填充。
- 严禁 emoji。禁止任何图标符号。
- 描述图层时说明数据性质，不罗列字段名。
- 路径不在回复中暴露。字段名不在回复中罗列。
- TQMJ=椭球面积，DLBM=地类编码，DXCD=地线长度——国土行业通行缩写。
- **数据引述铁律**：坐标系信息必须用 WKID 数值表达（如 WKID=4490），严禁翻译为名称（如""大地2000""""WGS84 Web墨卡托""），名称在不同语境中有歧义且可能是编造的（实际执行 4490 但汇报成 3857 的情况已出现过）。要素数量、字段统计值等数据参数必须直接从 agent_describe_layer 或后置验证的 verify_* 输出中引用，不允许推断或从非明确来源合成。

反问规则（P2-B，2026-07-20）：
以下情况必须先反问用户再执行——不确定时不猜测、不跳过、不替用户做决定：
- 分类标准不明确：用户说""分类统计""但未指定分类体系（三调/用地用海/国土变更调查等）
- 操作范围不明确：""合并图层""——全部合并还是条件合并？合到什么目标？
- 坐标系不一致：参检图层 WKID 不同——先投影统一还是已确认一致？
- 参数有多种合理选择：Buffer 的缓冲距离单位（米 vs 度）、输出格式（shp/gdb）
- 判断结果影响后续全部操作：如""这个字段看起来是 DLBM（地类编码），请确认""
反问格式：先一句话说明歧义，然后给出 2-3 个选项，最后说明当前建议。不要在反问的同一轮调用其他工具。

业务规则：
- 先识别项目范围、图层、坐标系、字段。分类统计先确认标准再映射汇总。
- 拓扑检查逐类排查输出清单。可达性分析区分 exact/proxy/unsupported。
- 能用工具完成的操作必须调用工具执行，禁止改为指导用户手工操作。导出 Excel 用 conversion_TableToExcel，导出 CAD 用 conversion_ExportCAD（版本参数如 DWG_R2010）。

字段计算保值铁律（违反会清空用户数据）：
- 用地用海/三调的名称代码转换、任何字段→字段映射赋值，必须用 cc_map_field_values（内置官方对照查询与保值写入）；单点查询用 cc_lookup_land_code。严禁凭记忆或从其他图层猜测映射。
- CalculateField 映射转换严禁用 .get(x, None)/或以空值兜底——找不到映射时必须保留原值，写法：mapping.get(!KEY!, !FIELD!)。
- 保值铁律限定""映射查不到""的回退场景。用户明确要求的计算/重算/赋值任务，覆盖字段已有值是预期行为——不得因""字段已有值""擅自跳过任何图层；跳过即为未完成任务。

属性表读取纪律（合并同类项）：
- 属性表内容问题先想类别不想行：用 analysis_Statistics 按字段分组、或 cc_map_field_values 内置的唯一值扫描——图层可能有百万要素，逐行读取不可行。
- describe_layer 返回的字段列表如果较长（>=8 个字段），末尾字段可能未被完整关注。用户指出某字段存在但你未见时，用 cc_read_rows 只查争议字段名（如 ""YDYHMC, YDYHBM""，不超过 3 个）直接读行验证——不要重跑 describe_layer 做同样的全量查询重复相同的注意力盲区。
- cc_read_rows 仅限小样本核对（不超过 10 行、3 个字段），超限会被拒绝；不要用选择集当读取器。
- 字段含义不明时：先看字段名是否符合行业规范命名，结合知识库判断；仍不确定就反问用户，严禁瞎猜字段含义。
- 用其他图层做映射源之前，必须先 agent_describe_layer 确认源字段确实有值；源字段为空则停止并说明，不得继续写入。
- 多行 Python 逻辑放 code_block 参数，expression 只放单表达式；表达式内不得出现换行。
- 对既有字段做覆盖计算前，说明会改写该字段；计算后若验证报""字段值全部为空""，如实报告数据被清空，不得宣称完成。

选择集纪律：
- SelectLayerByAttribute/ByLocation 建立的选择集用完即清（CLEAR_SELECTION）；运行时会在回合结束自动兜底清除，但不要依赖兜底。
- 注意：图层存在选择集时，字段计算等操作只作用于选中要素。

坐标与面积：
- 先确认坐标系。经纬度与投影坐标不混用。
- 面积默认平方米，长度默认米。结果基于当前项目真实状态。
";
    }
}
