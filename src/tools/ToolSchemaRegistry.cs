using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Tools.Core
{
    public class ToolSchemaRegistry
    {
        private readonly Dictionary<string, ToolDefinition> _schemas = new();

        public void Register(ToolDefinition schema)
        {
            _schemas[schema.Name] = schema;
        }

        public void RegisterAllDefaults()
        {
            Register(Buffer());
            Register(Clip());
            Register(Intersect());
            Register(Erase());
            Register(Select());
            Register(Statistics());
            Register(MultipleRingBuffer());
            Register(AddField());
            Register(CalculateField());
            Register(SelectLayerByAttribute());
            Register(DeleteField());
            Register(Dissolve());
            Register(Merge());
            Register(Project());
            Register(CopyFeatures());
            Register(DeleteFeatures());
        }

        private static ToolDefinition Buffer() => new()
        {
            Name = "analysis_Buffer",
            Description = "创建缓冲区。支持字段驱动距离、线侧类型(FULL/LEFT/RIGHT)、端点类型(ROUND/FLAT)、融合选项。",
            Category = "gp",
            Destructive = false,
            RiskLevel = ToolRiskLevel.Increment,
            ParameterOrder = new() { "in_features", "out_feature_class", "buffer_distance_or_field", "line_side", "line_end_type", "dissolve_option", "dissolve_field" },
            InputParams = new() { "in_features" },
            OutputParams = new() { "out_feature_class" },
            PrimaryOutput = "out_feature_class",
            Verification = new VerificationRule { Type = "verify_exists", FixHint = "检查输出是否生成" },
            Parameters = new ToolParameters
            {
                Type = "object",
                Properties = new Dictionary<string, ParameterProperty>
                {
                    ["in_features"] = new() { Type = "string", Description = "输入图层名称" },
                    ["out_feature_class"] = new() { Type = "string", Description = "输出要素类名称（简单名）" },
                    ["buffer_distance_or_field"] = new() { Type = "string", Description = "缓冲距离。固定值如'50 Meters'，或字段名如'buf_dist'。" },
                    ["line_side"] = new() { Type = "string", Description = "线缓冲侧。FULL=双侧，LEFT=左侧，RIGHT=右侧。", Enum = new() { "FULL", "LEFT", "RIGHT" } },
                    ["line_end_type"] = new() { Type = "string", Description = "线端点。ROUND=圆头，FLAT=平头直角。", Enum = new() { "ROUND", "FLAT" } },
                    ["dissolve_option"] = new() { Type = "string", Description = "融合选项。NONE=不融合，ALL=全部融合（交汇圆角），LIST=按字段融合。", Enum = new() { "NONE", "ALL", "LIST" } },
                    ["dissolve_field"] = new() { Type = "string", Description = "融合字段名（dissolve_option=LIST时需要）" },
                },
                Required = new() { "in_features", "out_feature_class", "buffer_distance_or_field" }
            }
        };

        private static ToolDefinition Clip() => new()
        {
            Name = "analysis_Clip",
            Description = "裁剪。用裁剪图层边界提取输入图层的要素。",
            Category = "gp",
            Destructive = false,
            RiskLevel = ToolRiskLevel.Increment,
            ParameterOrder = new() { "in_features", "clip_features", "out_feature_class" },
            InputParams = new() { "in_features", "clip_features" },
            OutputParams = new() { "out_feature_class" },
            PrimaryOutput = "out_feature_class",
            Verification = new VerificationRule { Type = "verify_exists", FixHint = "检查输出是否生成" },
            Parameters = new ToolParameters
            {
                Type = "object",
                Properties = new Dictionary<string, ParameterProperty>
                {
                    ["in_features"] = new() { Type = "string", Description = "输入图层名称（被裁剪方）" },
                    ["clip_features"] = new() { Type = "string", Description = "裁剪边界图层名称" },
                    ["out_feature_class"] = new() { Type = "string", Description = "输出要素类名称（简单名）" },
                },
                Required = new() { "in_features", "clip_features", "out_feature_class" }
            }
        };

        private static ToolDefinition Intersect() => new()
        {
            Name = "analysis_Intersect",
            Description = "相交分析。计算两个或多个图层的几何交集。",
            Category = "gp",
            Destructive = false,
            RiskLevel = ToolRiskLevel.Increment,
            ParameterOrder = new() { "in_features", "out_feature_class", "join_attributes", "output_type" },
            InputParams = new() { "in_features" },
            OutputParams = new() { "out_feature_class" },
            PrimaryOutput = "out_feature_class",
            Verification = new VerificationRule { Type = "verify_exists", FixHint = "检查输出是否生成" },
            Parameters = new ToolParameters
            {
                Type = "object",
                Properties = new Dictionary<string, ParameterProperty>
                {
                    ["in_features"] = new() { Type = "string", Description = "输入图层（多个用分号分隔）" },
                    ["out_feature_class"] = new() { Type = "string", Description = "输出要素类名称（简单名）" },
                    ["join_attributes"] = new() { Type = "string", Description = "连接属性方式", Enum = new() { "ALL", "NO_FID", "ONLY_FID" } },
                    ["output_type"] = new() { Type = "string", Description = "输出类型", Enum = new() { "INPUT", "LINE", "POINT" } },
                },
                Required = new() { "in_features", "out_feature_class" }
            }
        };

        private static ToolDefinition Erase() => new()
        {
            Name = "analysis_Erase",
            Description = "擦除。用擦除图层去除输入图层的重叠部分。",
            Category = "gp",
            Destructive = false,
            RiskLevel = ToolRiskLevel.Increment,
            ParameterOrder = new() { "in_features", "erase_features", "out_feature_class" },
            InputParams = new() { "in_features", "erase_features" },
            OutputParams = new() { "out_feature_class" },
            PrimaryOutput = "out_feature_class",
            Verification = new VerificationRule { Type = "verify_exists", FixHint = "检查输出是否生成" },
            Parameters = new ToolParameters
            {
                Type = "object",
                Properties = new Dictionary<string, ParameterProperty>
                {
                    ["in_features"] = new() { Type = "string", Description = "输入图层名称（被擦除方）" },
                    ["erase_features"] = new() { Type = "string", Description = "擦除边界图层名称" },
                    ["out_feature_class"] = new() { Type = "string", Description = "输出要素类名称（简单名）" },
                },
                Required = new() { "in_features", "erase_features", "out_feature_class" }
            }
        };

        private static ToolDefinition Select() => new()
        {
            Name = "analysis_Select",
            Description = "按属性或空间条件提取要素到新图层。",
            Category = "gp",
            Destructive = false,
            RiskLevel = ToolRiskLevel.Increment,
            ParameterOrder = new() { "in_features", "out_feature_class", "where_clause" },
            InputParams = new() { "in_features" },
            OutputParams = new() { "out_feature_class" },
            PrimaryOutput = "out_feature_class",
            Verification = new VerificationRule { Type = "verify_exists", FixHint = "检查输出是否生成" },
            Parameters = new ToolParameters
            {
                Type = "object",
                Properties = new Dictionary<string, ParameterProperty>
                {
                    ["in_features"] = new() { Type = "string", Description = "输入图层名称" },
                    ["out_feature_class"] = new() { Type = "string", Description = "输出要素类名称（简单名）" },
                    ["where_clause"] = new() { Type = "string", Description = "SQL WHERE 条件。如 DLBM = '0601'" },
                },
                Required = new() { "in_features", "out_feature_class" }
            }
        };

        private static ToolDefinition Statistics() => new()
        {
            Name = "analysis_Statistics",
            Description = "汇总统计。按字段分组计算总和/均值/计数等。",
            Category = "gp",
            Destructive = false,
            RiskLevel = ToolRiskLevel.Increment,
            ParameterOrder = new() { "in_table", "out_table", "statistics_fields", "case_field" },
            InputParams = new() { "in_table" },
            OutputParams = new() { "out_table" },
            PrimaryOutput = "out_table",
            Verification = new VerificationRule { Type = "verify_exists", FixHint = "检查输出是否生成" },
            Parameters = new ToolParameters
            {
                Type = "object",
                Properties = new Dictionary<string, ParameterProperty>
                {
                    ["in_table"] = new() { Type = "string", Description = "输入图层名称" },
                    ["out_table"] = new() { Type = "string", Description = "输出统计表名称（简单名）" },
                    ["statistics_fields"] = new() { Type = "string", Description = "统计字段和方式。格式：'字段名 统计类型'。如'TQMJ SUM'。" },
                    ["case_field"] = new() { Type = "string", Description = "分组字段名（可选）" },
                },
                Required = new() { "in_table", "out_table", "statistics_fields" }
            }
        };

        private static ToolDefinition MultipleRingBuffer() => new()
        {
            Name = "analysis_MultipleRingBuffer",
            Description = "多环缓冲区。一次生成多个距离的缓冲区环。",
            Category = "gp",
            Destructive = false,
            RiskLevel = ToolRiskLevel.Increment,
            ParameterOrder = new() { "Input_Features", "Output_Feature_class", "Distances", "Buffer_Unit" },
            InputParams = new() { "Input_Features" },
            OutputParams = new() { "Output_Feature_class" },
            PrimaryOutput = "Output_Feature_class",
            Verification = new VerificationRule { Type = "verify_exists", FixHint = "检查输出是否生成" },
            Parameters = new ToolParameters
            {
                Type = "object",
                Properties = new Dictionary<string, ParameterProperty>
                {
                    ["Input_Features"] = new() { Type = "string", Description = "输入图层名称" },
                    ["Output_Feature_class"] = new() { Type = "string", Description = "输出要素类名称（简单名）" },
                    ["Distances"] = new() { Type = "string", Description = "缓冲距离列表，空格分隔。如 '50 100 150'。" },
                    ["Buffer_Unit"] = new() { Type = "string", Description = "距离单位", Enum = new() { "Meters", "Kilometers", "Feet", "Miles" } },
                },
                Required = new() { "Input_Features", "Output_Feature_class", "Distances" }
            }
        };

        private static ToolDefinition AddField() => new()
        {
            Name = "management_AddField",
            Description = "添加新字段。是 CalculateField 的前置步骤。",
            Category = "gp",
            Destructive = false,
            RiskLevel = ToolRiskLevel.Modify,
            Verification = new VerificationRule { Type = "verify_field", FixHint = "检查字段是否存在" },
            Parameters = new ToolParameters
            {
                Type = "object",
                Properties = new Dictionary<string, ParameterProperty>
                {
                    ["in_table"] = new() { Type = "string", Description = "输入图层名称" },
                    ["field_name"] = new() { Type = "string", Description = "新字段名。中文或英文，无特殊字符。" },
                    ["field_type"] = new() { Type = "string", Description = "字段类型", Enum = new() { "SHORT", "LONG", "FLOAT", "DOUBLE", "TEXT", "DATE" } },
                    ["field_length"] = new() { Type = "integer", Description = "字段长度（TEXT类型需要）" },
                },
                Required = new() { "in_table", "field_name", "field_type" }
            }
        };

        private static ToolDefinition CalculateField() => new()
        {
            Name = "management_CalculateField",
            Description = "计算字段值。必须先确保目标字段存在。",
            Category = "gp",
            Destructive = false,
            RiskLevel = ToolRiskLevel.Modify,
            // 值级验证：字段存在≠计算成功——表达式逐行返回 None 时 GP 照样报成功，
            // 只有抽样字段值才能抓住"整列被写空"（2026-07-18 现场事故）
            // English：Value-level verification: Field exists ≠ calculation successful—when an expression returns None line by line, GP still reports success as usual; only the sampled field value can catch "entire column is empty" (2026-07-18 On-site Incident)
            Verification = new VerificationRule { Type = "verify_field_populated", FixHint = "检查字段存在且计算后有值；若全空说明表达式返回了空值" },
            Parameters = new ToolParameters
            {
                Type = "object",
                Properties = new Dictionary<string, ParameterProperty>
                {
                    ["in_table"] = new() { Type = "string", Description = "输入图层名称" },
                    ["field_name"] = new() { Type = "string", Description = "要计算的字段名（必须已存在）" },
                    ["expression"] = new() { Type = "string", Description = "计算表达式。PYTHON3格式。如'!shape.geodesicArea!'。" },
                    ["expression_type"] = new() { Type = "string", Description = "表达式类型", Enum = new() { "PYTHON3", "ARCADE", "SQL" } },
                },
                Required = new() { "in_table", "field_name", "expression" }
            }
        };

        private static ToolDefinition SelectLayerByAttribute() => new()
        {
            Name = "management_SelectLayerByAttribute",
            Description = "按属性条件选中要素（修改选择集，不产生新图层）。",
            Category = "gp",
            Destructive = false,
            RiskLevel = ToolRiskLevel.ReadOnly,
            Parameters = new ToolParameters
            {
                Type = "object",
                Properties = new Dictionary<string, ParameterProperty>
                {
                    ["in_layer_or_view"] = new() { Type = "string", Description = "输入图层名称" },
                    ["selection_type"] = new() { Type = "string", Description = "选择类型", Enum = new() { "NEW_SELECTION", "ADD_TO_SELECTION", "REMOVE_FROM_SELECTION", "SUBSET_SELECTION" } },
                    ["where_clause"] = new() { Type = "string", Description = "SQL WHERE 条件。如 level = 1" },
                },
                Required = new() { "in_layer_or_view", "where_clause" }
            }
        };

        private static ToolDefinition DeleteField() => new()
        {
            Name = "management_DeleteField",
            Description = "删除字段。不可逆。",
            Category = "gp",
            Destructive = true,
            RiskLevel = ToolRiskLevel.Destructive,
            Verification = new VerificationRule { Type = "verify_field", Invert = true, FixHint = "检查字段是否存在" },
            Parameters = new ToolParameters
            {
                Type = "object",
                Properties = new Dictionary<string, ParameterProperty>
                {
                    ["in_table"] = new() { Type = "string", Description = "输入图层名称" },
                    ["drop_field"] = new() { Type = "string", Description = "要删除的字段名" },
                },
                Required = new() { "in_table", "drop_field" }
            }
        };

        private static ToolDefinition Dissolve() => new()
        {
            Name = "management_Dissolve",
            Description = "融合。按指定字段将相邻同值要素合并。",
            Category = "gp",
            Destructive = false,
            RiskLevel = ToolRiskLevel.Increment,
            ParameterOrder = new() { "in_features", "out_feature_class", "dissolve_field", "statistics_fields" },
            InputParams = new() { "in_features" },
            OutputParams = new() { "out_feature_class" },
            PrimaryOutput = "out_feature_class",
            Verification = new VerificationRule { Type = "verify_exists", FixHint = "检查输出是否生成" },
            Parameters = new ToolParameters
            {
                Type = "object",
                Properties = new Dictionary<string, ParameterProperty>
                {
                    ["in_features"] = new() { Type = "string", Description = "输入图层名称" },
                    ["out_feature_class"] = new() { Type = "string", Description = "输出要素类名称（简单名）" },
                    ["dissolve_field"] = new() { Type = "string", Description = "融合依据字段名" },
                    ["statistics_fields"] = new() { Type = "string", Description = "统计字段（可选）。格式：'字段名 统计类型'" },
                },
                Required = new() { "in_features", "out_feature_class", "dissolve_field" }
            }
        };

        private static ToolDefinition Merge() => new()
        {
            Name = "management_Merge",
            Description = "合并。将多个同类型图层合并为一个。",
            Category = "gp",
            Destructive = false,
            RiskLevel = ToolRiskLevel.Increment,
            ParameterOrder = new() { "inputs", "output" },
            InputParams = new() { "inputs" },
            OutputParams = new() { "output" },
            PrimaryOutput = "output",
            Verification = new VerificationRule { Type = "verify_exists", FixHint = "检查输出是否生成" },
            Parameters = new ToolParameters
            {
                Type = "object",
                Properties = new Dictionary<string, ParameterProperty>
                {
                    ["inputs"] = new() { Type = "string", Description = "输入图层（多个用分号分隔）" },
                    ["output"] = new() { Type = "string", Description = "输出要素类名称（简单名）" },
                },
                Required = new() { "inputs", "output" }
            }
        };

        private static ToolDefinition Project() => new()
        {
            Name = "management_Project",
            Description = "投影转换。将图层坐标系转换为目标坐标系。",
            Category = "gp",
            Destructive = false,
            RiskLevel = ToolRiskLevel.Increment,
            ParameterOrder = new() { "in_dataset", "out_dataset", "out_coor_system" },
            InputParams = new() { "in_dataset" },
            OutputParams = new() { "out_dataset" },
            PrimaryOutput = "out_dataset",
            Verification = new VerificationRule { Type = "verify_spatial_ref", FixHint = "检查坐标系是否正确" },
            Parameters = new ToolParameters
            {
                Type = "object",
                Properties = new Dictionary<string, ParameterProperty>
                {
                    ["in_dataset"] = new() { Type = "string", Description = "输入图层名称", SemanticRole = "input_dataset" },
                    ["out_dataset"] = new() { Type = "string", Description = "输出要素类名称（简单名）", SemanticRole = "output_dataset" },
                    ["out_coor_system"] = new() { Type = "string", Description = "目标坐标系 WKID。常用：4490=CGCS2000地理，4527=CGCS2000 3度带37", SemanticRole = "spatial_reference", IsSpatialRef = true },
                },
                Required = new() { "in_dataset", "out_dataset", "out_coor_system" }
            }
        };

        private static ToolDefinition CopyFeatures() => new()
        {
            Name = "management_CopyFeatures",
            Description = "复制要素到新图层。",
            Category = "gp",
            Destructive = false,
            RiskLevel = ToolRiskLevel.Increment,
            ParameterOrder = new() { "in_features", "out_feature_class" },
            InputParams = new() { "in_features" },
            OutputParams = new() { "out_feature_class" },
            PrimaryOutput = "out_feature_class",
            Verification = new VerificationRule { Type = "verify_exists", FixHint = "检查输出是否生成" },
            Parameters = new ToolParameters
            {
                Type = "object",
                Properties = new Dictionary<string, ParameterProperty>
                {
                    ["in_features"] = new() { Type = "string", Description = "输入图层名称" },
                    ["out_feature_class"] = new() { Type = "string", Description = "输出要素类名称（简单名）" },
                },
                Required = new() { "in_features", "out_feature_class" }
            }
        };

        private static ToolDefinition DeleteFeatures() => new()
        {
            Name = "management_DeleteFeatures",
            Description = "删除图层中所有要素（保留图层结构）。不可逆。",
            Category = "gp",
            Destructive = true,
            RiskLevel = ToolRiskLevel.Destructive,
            Verification = new VerificationRule { Type = "verify_count_zero", FixHint = "检查输出是否已清空" },
            Parameters = new ToolParameters
            {
                Type = "object",
                Properties = new Dictionary<string, ParameterProperty>
                {
                    ["in_features"] = new() { Type = "string", Description = "输入图层名称" },
                },
                Required = new() { "in_features" }
            }
        };

        public ToolDefinition? GetSchema(string toolName) =>
            _schemas.TryGetValue(toolName, out var s) ? s : null;

        public List<ToolDefinition> GetAllSchemas() => _schemas.Values.ToList();

        public bool HasSchema(string toolName) => _schemas.ContainsKey(toolName);

        public List<string> ConvertToPositionalArgs(string toolName, Dictionary<string, object?> namedArgs)
        {
            var contract = SpatialAgent.Tools.Core.GPToolContract.Get(toolName);
            string[] order = contract?.ParameterOrder
                ?? namedArgs.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();

            var positional = new List<string>();
            foreach (string key in order)
            {
                if (namedArgs.TryGetValue(key, out object? val))
                    positional.Add(val?.ToString() ?? "");
                else
                    positional.Add("");
            }
            return positional;
        }
    }
}
