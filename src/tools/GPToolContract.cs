using System.Collections.Generic;

namespace SpatialAgent.Tools.Core
{
    public class GPToolContract
    {
        public string FullName { get; set; } = "";
        public string DotName => FullName.Replace('_', '.');
        public string[] ParameterOrder { get; set; } = System.Array.Empty<string>();
        public int OutputIndex { get; set; } = -1;
        public int InputIndex { get; set; } = 0;
        public List<SpatialAgent.Runtime.Contracts.VerificationRule> DefaultVerifications { get; set; } = new();

        public static GPToolContract Buffer => new()
        {
            FullName = "analysis.Buffer",
            ParameterOrder = new[] { "in_features", "out_feature_class", "buffer_distance_or_field", "line_side", "line_end_type", "dissolve_option", "dissolve_field" },
            OutputIndex = 1,
            InputIndex = 0,
            DefaultVerifications = new()
            {
                new() { Type = "verify_exists", Target = "", Expected = "", FixHint = "检查输出是否生成" }
            }
        };
        public static GPToolContract Clip => new()
        {
            FullName = "analysis.Clip",
            ParameterOrder = new[] { "in_features", "clip_features", "out_feature_class" },
            OutputIndex = 2,
            InputIndex = 0,
            DefaultVerifications = new()
            {
                new() { Type = "verify_exists", Target = "", Expected = "", FixHint = "检查输出是否生成" }
            }
        };
        public static GPToolContract Intersect => new()
        {
            FullName = "analysis.Intersect",
            ParameterOrder = new[] { "in_features", "out_feature_class", "join_attributes", "output_type" },
            OutputIndex = 1,
            InputIndex = 0,
            DefaultVerifications = new()
            {
                new() { Type = "verify_exists", Target = "", Expected = "", FixHint = "检查输出是否生成" }
            }
        };
        public static GPToolContract Erase => new()
        {
            FullName = "analysis.Erase",
            ParameterOrder = new[] { "in_features", "erase_features", "out_feature_class" },
            OutputIndex = 2,
            InputIndex = 0,
            DefaultVerifications = new()
            {
                new() { Type = "verify_exists", Target = "", Expected = "", FixHint = "检查输出是否生成" }
            }
        };
        public static GPToolContract Select => new()
        {
            FullName = "analysis.Select",
            ParameterOrder = new[] { "in_features", "out_feature_class", "where_clause" },
            OutputIndex = 1,
            InputIndex = 0,
            DefaultVerifications = new()
            {
                new() { Type = "verify_exists", Target = "", Expected = "", FixHint = "检查输出是否生成" }
            }
        };
        public static GPToolContract Statistics => new()
        {
            FullName = "analysis.Statistics",
            ParameterOrder = new[] { "in_table", "out_table", "statistics_fields", "case_field" },
            OutputIndex = 1,
            InputIndex = 0,
            DefaultVerifications = new()
            {
                new() { Type = "verify_exists", Target = "", Expected = "", FixHint = "检查输出是否生成" }
            }
        };
        public static GPToolContract MultipleRingBuffer => new()
        {
            FullName = "analysis.MultipleRingBuffer",
            ParameterOrder = new[] { "Input_Features", "Output_Feature_class", "Distances", "Buffer_Unit" },
            OutputIndex = 1,
            InputIndex = 0,
            DefaultVerifications = new()
            {
                new() { Type = "verify_exists", Target = "", Expected = "", FixHint = "检查输出是否生成" }
            }
        };
        public static GPToolContract AddField => new()
        {
            FullName = "management.AddField",
            ParameterOrder = new[] { "in_table", "field_name", "field_type", "field_length" },
            OutputIndex = -1,
            InputIndex = 0,
            DefaultVerifications = new()
            {
                new() { Type = "verify_field", Target = "", Expected = "", FixHint = "检查字段是否存在" }
            }
        };
        public static GPToolContract CalculateField => new()
        {
            FullName = "management.CalculateField",
            ParameterOrder = new[] { "in_table", "field_name", "expression", "expression_type" },
            OutputIndex = -1,
            InputIndex = 0,
            DefaultVerifications = new()
            {
                new() { Type = "verify_field_populated", Target = "", Expected = "", FixHint = "检查字段存在且计算后有值；若全空说明表达式返回了空值" }
            }
        };
        public static GPToolContract SelectLayerByAttribute => new()
        {
            FullName = "management.SelectLayerByAttribute",
            ParameterOrder = new[] { "in_layer_or_view", "selection_type", "where_clause" },
            OutputIndex = -1,
            InputIndex = 0,
            DefaultVerifications = new()
        };
        public static GPToolContract DeleteField => new()
        {
            FullName = "management.DeleteField",
            ParameterOrder = new[] { "in_table", "drop_field" },
            OutputIndex = -1,
            InputIndex = 0,
            DefaultVerifications = new()
            {
                new() { Type = "verify_field", Target = "", Expected = "", Invert = true, FixHint = "检查字段是否已删除" }
            }
        };
        public static GPToolContract Dissolve => new()
        {
            FullName = "management.Dissolve",
            ParameterOrder = new[] { "in_features", "out_feature_class", "dissolve_field", "statistics_fields" },
            OutputIndex = 1,
            InputIndex = 0,
            DefaultVerifications = new()
            {
                new() { Type = "verify_exists", Target = "", Expected = "", FixHint = "检查输出是否生成" }
            }
        };
        public static GPToolContract Merge => new()
        {
            FullName = "management.Merge",
            ParameterOrder = new[] { "inputs", "output" },
            OutputIndex = 1,
            InputIndex = 0,
            DefaultVerifications = new()
            {
                new() { Type = "verify_exists", Target = "", Expected = "", FixHint = "检查输出是否生成" }
            }
        };
        public static GPToolContract Project => new()
        {
            FullName = "management.Project",
            ParameterOrder = new[] { "in_dataset", "out_dataset", "out_coor_system" },
            OutputIndex = 1,
            InputIndex = 0,
            DefaultVerifications = new()
            {
                new() { Type = "verify_spatial_ref", Target = "", Expected = "", FixHint = "检查坐标系是否正确" }
            }
        };
        public static GPToolContract CopyFeatures => new()
        {
            FullName = "management.CopyFeatures",
            ParameterOrder = new[] { "in_features", "out_feature_class" },
            OutputIndex = 1,
            InputIndex = 0,
            DefaultVerifications = new()
            {
                new() { Type = "verify_exists", Target = "", Expected = "", FixHint = "检查输出是否生成" }
            }
        };
        public static GPToolContract DeleteFeatures => new()
        {
            FullName = "management.DeleteFeatures",
            ParameterOrder = new[] { "in_features" },
            OutputIndex = -1,
            InputIndex = 0,
            DefaultVerifications = new()
            {
                new() { Type = "verify_count_zero", Target = "", Expected = "0", FixHint = "检查输出是否已清空" }
            }
        };

        private static readonly Dictionary<string, GPToolContract> _contracts = new()
        {
            ["analysis_Buffer"] = Buffer,
            ["analysis_Clip"] = Clip,
            ["analysis_Intersect"] = Intersect,
            ["analysis_Erase"] = Erase,
            ["analysis_Select"] = Select,
            ["analysis_Statistics"] = Statistics,
            ["analysis_MultipleRingBuffer"] = MultipleRingBuffer,
            ["management_AddField"] = AddField,
            ["management_CalculateField"] = CalculateField,
            ["management_SelectLayerByAttribute"] = SelectLayerByAttribute,
            ["management_DeleteField"] = DeleteField,
            ["management_Dissolve"] = Dissolve,
            ["management_Merge"] = Merge,
            ["management_Project"] = Project,
            ["management_CopyFeatures"] = CopyFeatures,
            ["management_DeleteFeatures"] = DeleteFeatures,
        };

        public static string ToDotName(string toolName)
        {
            if (toolName.Contains('.')) return toolName;
            var contract = Get(toolName);
            return contract?.DotName ?? toolName.Replace('_', '.');
        }

        public static string? ExtractOutput(string toolName, Dictionary<string, object?> namedArgs)
        {
            var contract = Get(toolName);
            if (contract == null || contract.OutputIndex < 0) return null;
            if (contract.OutputIndex >= contract.ParameterOrder.Length) return null;
            string outputKey = contract.ParameterOrder[contract.OutputIndex];
            return namedArgs.TryGetValue(outputKey, out var val) ? val?.ToString() : null;
        }

        public static GPToolContract? Get(string toolName)
        {
            if (_contracts.TryGetValue(toolName, out var contract)) return contract;
            var underscoreName = toolName.Replace('.', '_');
            _contracts.TryGetValue(underscoreName, out contract);
            return contract;
        }
    }
}
