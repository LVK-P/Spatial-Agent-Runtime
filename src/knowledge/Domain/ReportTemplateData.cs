using System.Collections.Generic;

// ────────────────────────────────────────────────────────────────
// ReportTemplateData.cs — 规划报表模板定义
//
// BUILD-TIME ASSET: consumed by tools/extract_domain_data.py as generation input.
// No runtime callers. Keep for domain reference.
// Six standard report templates for spatial planning:
//   1. 用地结构表 — 按地类分类汇总面积+占比
//   2. 规划用地调整表 — 现状vs规划对比
//   3. 三大类面积汇总表 — 农用地/建设用地/未利用地
//   4. 建设用地平衡表 — 建设用地各项指标
//   5. 耕地占补平衡表 — 耕地占用与补充跟踪
//   6. 用地指标核算表 — 规划指标达标判定
//
// GP 工具链：analysis.Statistics + management.CalculateField
// ────────────────────────────────────────────────────────────────
namespace SpatialAgent.Knowledge.Data
{
    public static class ReportTemplateData
    {
        // ───────────────────────────────────────────────────────────────────
        // Report column definitions
        // ───────────────────────────────────────────────────────────────────
        public class ReportColumn
        {
            public string FieldName { get; set; } = "";       // field name
            public string Alias { get; set; } = "";           // column header
            public string Type { get; set; } = "string";      // data type
            public string? Formula { get; set; }              // CalculateField expression
            public string? Source { get; set; }               // data source
        }

        public class ReportTemplate
        {
            public string Name { get; set; } = "";
            public string Description { get; set; } = "";
            public string TriggerKeyword { get; set; } = "";  // comma-separated keywords for matching
            public List<ReportColumn> Columns { get; set; } = new();
            public string GroupByField { get; set; } = "";    // field to group statistics by
            public string AreaField { get; set; } = "JSMJ";   // area field name
            public string? FilterSql { get; set; }            // optional SQL WHERE clause
        }

        // ───────────────────────────────────────────────────────────────────
        // Template 1: Land use structure (area + proportion by category)
        // Purpose: aggregate area and proportion by land use type
        // ───────────────────────────────────────────────────────────────────
        public static readonly ReportTemplate LandUseStructure = new()
        {
            Name = "用地结构表",
            Description = "按一级类/二级类汇总各用地类型的面积、占全域面积比例、占建设用地比例",
            TriggerKeyword = "用地结构, 用地汇总, 面积占比, 地类汇总表, 用地面积统计",
            GroupByField = "YDLX",
            AreaField = "JSMJ",

            Columns = new List<ReportColumn>
            {
                new() { FieldName = "用地代码", Alias = "用地代码", Type = "string", Source = "YDLX 或 DLBM" },
                new() { FieldName = "用地名称", Alias = "用地名称", Type = "string", Source = "LandClassificationData.GetName(YDLX)" },
                new() { FieldName = "面积_公顷", Alias = "面积(公顷)", Type = "float", Formula = "!JSMJ! / 10000" },
                new() { FieldName = "占全域比例", Alias = "占全域比例(%)", Type = "float", Formula = "!SUM_JSMJ! / <全域总面积> * 100" },
                new() { FieldName = "占建设用地比例", Alias = "占建设用地比例(%)", Type = "float", Formula = "!SUM_JSMJ! / <建设用地总面积> * 100" },
                new() { FieldName = "人均面积", Alias = "人均面积(㎡/人)", Type = "float", Formula = "!JSMJ! / <规划人口>", Source = "仅计算建设用地时有效" },
            },
            FilterSql = null  // all records
        };

        // ───────────────────────────────────────────────────────────────────
        // Template 2: Land use adjustment (current vs. planned)
        // Purpose: compare base-year current vs. target-year planned land change
        // ───────────────────────────────────────────────────────────────────
        public static readonly ReportTemplate LandUseAdjustment = new()
        {
            Name = "规划用地调整表",
            Description = "对比现状用地（基期年）和规划用地（目标年），计算各地类的增减量",
            TriggerKeyword = "用地调整, 地类变化, 现状规划对比, 调入调出, 用地转换, 结构调整",
            GroupByField = "YDLX",
            AreaField = "JSMJ",

            Columns = new List<ReportColumn>
            {
                new() { FieldName = "用地代码", Alias = "用地代码", Type = "string" },
                new() { FieldName = "用地名称", Alias = "用地名称", Type = "string" },
                new() { FieldName = "现状面积_公顷", Alias = "现状面积(公顷)", Type = "float", Source = "基期年图层统计" },
                new() { FieldName = "现状占比", Alias = "现状占比(%)", Type = "float", Formula = "!现状面积! / <现状总面积> * 100" },
                new() { FieldName = "规划面积_公顷", Alias = "规划面积(公顷)", Type = "float", Source = "规划年图层统计" },
                new() { FieldName = "规划占比", Alias = "规划占比(%)", Type = "float", Formula = "!规划面积! / <规划总面积> * 100" },
                new() { FieldName = "增减量_公顷", Alias = "增减量(公顷)", Type = "float", Formula = "!规划面积! - !现状面积!" },
                new() { FieldName = "增减幅度", Alias = "增减幅度(%)", Type = "float", Formula = "(!规划面积! - !现状面积!) / !现状面积! * 100" },
            }
        };

        // ───────────────────────────────────────────────────────────────────
        // Template 3: Three-category area summary
        // Purpose: aggregate by agricultural/construction/unused categories
        // ───────────────────────────────────────────────────────────────────
        public static readonly ReportTemplate ThreeCategorySummary = new()
        {
            Name = "三大类面积汇总表",
            Description = "按《土地管理法》农用地/建设用地/未利用地三大类汇总面积",
            TriggerKeyword = "三大类, 农用地面积, 建设用地面积, 未利用地面积, 土地利用结构",
            GroupByField = "三大类",
            AreaField = "JSMJ",

            Columns = new List<ReportColumn>
            {
                new() { FieldName = "大类名称", Alias = "大类", Type = "string" },
                new() { FieldName = "面积_公顷", Alias = "面积(公顷)", Type = "float", Formula = "!JSMJ! / 10000" },
                new() { FieldName = "占比", Alias = "占比(%)", Type = "float", Formula = "!SUM_JSMJ! / <全域总面积> * 100" },
                new() { FieldName = "地块数", Alias = "图斑数", Type = "int", Source = "COUNT" },
            }
        };

        // ───────────────────────────────────────────────────────────────────
        // Template 4: Construction land balance
        // Purpose: detailed construction land breakdown with per-capita indicators
        // ───────────────────────────────────────────────────────────────────
        public static readonly ReportTemplate ConstructionLandBalance = new()
        {
            Name = "建设用地平衡表",
            Description = "详细列出居住/公服/商业/工业/交通/绿地等各类建设用地的面积和人均指标",
            TriggerKeyword = "建设用地平衡, 用地平衡表, 建设用地结构, 人均建设用地, 公服用地面积",
            GroupByField = "YDLX",
            AreaField = "JSMJ",

            Columns = new List<ReportColumn>
            {
                new() { FieldName = "用地代码", Alias = "用地代码", Type = "string" },
                new() { FieldName = "用地名称", Alias = "用地名称", Type = "string" },
                new() { FieldName = "面积_公顷", Alias = "面积(公顷)", Type = "float", Formula = "!JSMJ! / 10000" },
                new() { FieldName = "占建设用地比例", Alias = "占建设用地(%)", Type = "float", Formula = "!SUM_JSMJ! / <建设用地总面积> * 100" },
                new() { FieldName = "占全域比例", Alias = "占全域(%)", Type = "float", Formula = "!SUM_JSMJ! / <全域总面积> * 100" },
                new() { FieldName = "人均面积", Alias = "人均面积(㎡/人)", Type = "float", Formula = "!JSMJ! / <规划人口>" },
                new() { FieldName = "国家标准下限", Alias = "国标下限", Type = "float", Source = "GB 50137 城市用地分类与规划建设用地标准" },
                new() { FieldName = "达标判断", Alias = "是否达标", Type = "string", Formula = "IF 人均面积 >= 国标下限 THEN '达标' ELSE '不达标'" },
            },
            FilterSql = "YDLX IN ('07','08','09','10','11','12','13','14','15','16')"
        };

        // ───────────────────────────────────────────────────────────────────
        // Template 5: Farmland requisition-compensation balance
        // Purpose: track farmland occupation and replenishment
        // ───────────────────────────────────────────────────────────────────
        public static readonly ReportTemplate FarmlandBalance = new()
        {
            Name = "耕地占补平衡表",
            Description = "跟踪规划期内耕地占用（转建设用地）和补充（开发整理复垦）的面积变化",
            TriggerKeyword = "占补平衡, 耕地占用, 耕地补充, 耕地保有量, 耕地变化",
            GroupByField = "变更类型",
            AreaField = "JSMJ",

            Columns = new List<ReportColumn>
            {
                new() { FieldName = "变更类型", Alias = "变更类型", Type = "string" },
                new() { FieldName = "涉及耕地面积_公顷", Alias = "涉及耕地面积(公顷)", Type = "float" },
                new() { FieldName = "补充耕地面积_公顷", Alias = "补充耕地面积(公顷)", Type = "float" },
                new() { FieldName = "净增减_公顷", Alias = "净增减(公顷)", Type = "float", Formula = "!补充面积! - !占用面积!" },
                new() { FieldName = "占补平衡判断", Alias = "占补是否平衡", Type = "string", Formula = "IF 补充 >= 占用 THEN '平衡' ELSE '缺口'" },
            }
        };

        // ───────────────────────────────────────────────────────────────────
        // Template 6: Land use indicator audit
        // Purpose: verify planning indicators against targets
        // ───────────────────────────────────────────────────────────────────
        public static readonly ReportTemplate IndicatorAudit = new()
        {
            Name = "用地指标核算表",
            Description = "核算耕地保有量、林地保有量、建设用地总规模、人均城镇建设用地等关键指标",
            TriggerKeyword = "指标核算, 规划指标, 约束性指标, 指标达标, 用地考核, 耕地保有量, 林地保有量",
            GroupByField = "指标名称",
            AreaField = "JSMJ",

            Columns = new List<ReportColumn>
            {
                new() { FieldName = "指标名称", Alias = "指标名称", Type = "string" },
                new() { FieldName = "规划目标值", Alias = "规划目标值", Type = "float", Source = "上级下达的约束性指标" },
                new() { FieldName = "现状值", Alias = "现状值", Type = "float", Source = "当前数据统计" },
                new() { FieldName = "差值", Alias = "差值", Type = "float", Formula = "!现状值! - !规划目标值!" },
                new() { FieldName = "达标判断", Alias = "是否达标", Type = "string", Formula = "IF 差值 >= 0 THEN '达标' ELSE '未达标'" },
                new() { FieldName = "指标属性", Alias = "指标属性", Type = "string", Source = "约束性/预期性" },
            }
        };

        // ───────────────────────────────────────────────────────────────────
        // Template registry
        // ───────────────────────────────────────────────────────────────────
        public static readonly List<ReportTemplate> AllTemplates = new()
        {
            LandUseStructure,
            LandUseAdjustment,
            ThreeCategorySummary,
            ConstructionLandBalance,
            FarmlandBalance,
            IndicatorAudit
        };

        /// <summary>根据关键词匹配报表模板</summary>
        public static ReportTemplate? MatchTemplate(string userInput)
        {
            foreach (var template in AllTemplates)
            {
                foreach (var kw in template.TriggerKeyword.Split(','))
                {
                    if (userInput.Contains(kw.Trim()))
                        return template;
                }
            }
            return null;
        }

        public static int TemplateCount => AllTemplates.Count;
    }
}
