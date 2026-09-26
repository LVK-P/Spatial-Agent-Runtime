using FluentAssertions;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    /// <summary>
    /// Regression tests for the 2026-07-18 lingering-selection incident:
    /// the agent ran SelectLayerByAttribute(NEW_SELECTION) as an instrument,
    /// never cleared it, and left the whole layer selected in the map.
    /// Policy: instrumental selections are cleared at end of turn; a terminal
    /// selection (the turn's final step) is kept — it was likely the task.
    /// </summary>
    public class SelectionCleanupPolicyTests
    {
        private static (string, IReadOnlyDictionary<string, object?>?) Step(
            string tool, params (string Key, object? Value)[] args)
            => (tool, args.ToDictionary(a => a.Key, a => a.Value));

        [Fact]
        public void Instrumental_selection_is_cleared()
        {
            // 事故复现：选择 → 加字段 → 计算字段，选择集从未清除
            var steps = new[]
            {
                Step("management_SelectLayerByAttribute",
                    ("in_layer_or_view", "现状用地"), ("selection_type", "NEW_SELECTION"), ("where_clause", "YDYHBM IS NULL")),
                Step("management_AddField", ("in_table", "现状用地"), ("field_name", "YDYHMC")),
                Step("management_CalculateField", ("in_table", "现状用地"), ("field_name", "YDYHBM"))
            };

            RuntimePolicy.ResolveSelectionCleanups(steps)
                .Should().ContainSingle().Which.Should().Be("现状用地");
        }

        [Fact]
        public void Terminal_selection_kept_only_with_explicit_user_intent()
        {
            // 用户的任务就是"选中住宅用地"——终态选择保留
            var steps = new[]
            {
                Step("agent_describe_layer", ("layer_name", "现状用地")),
                Step("management_SelectLayerByAttribute",
                    ("in_layer_or_view", "现状用地"), ("selection_type", "NEW_SELECTION"), ("where_clause", "DLBM = '07'"))
            };

            RuntimePolicy.ResolveSelectionCleanups(steps, "选中所有住宅用地").Should().BeEmpty();
        }

        [Fact]
        public void Terminal_probe_selection_without_intent_is_cleared()
        {
            // 2026-07-19 实测误漏复现：模型把选择当行读取器且恰好以它收尾，
            // 用户消息（"对比字段是否有错误对应"）并无选择意图 → 必须清
            var steps = new[]
            {
                Step("agent_describe_layer", ("layer_name", "道路面")),
                Step("management_SelectLayerByAttribute",
                    ("in_layer_or_view", "道路面"), ("selection_type", "NEW_SELECTION"), ("where_clause", "YDYHBM IS NOT NULL"))
            };

            RuntimePolicy.ResolveSelectionCleanups(steps, "对比道路面的ydyhbm和ydyhmc是否有错误对应")
                .Should().ContainSingle().Which.Should().Be("道路面");
        }

        [Fact]
        public void Generic_word_select_alone_does_not_keep_terminal_selection()
        {
            // "选择"一词过泛（"选择合适的方案"），不计入选择意图
            var steps = new[]
            {
                Step("management_SelectLayerByAttribute",
                    ("in_layer_or_view", "A"), ("selection_type", "NEW_SELECTION"), ("where_clause", "1=1"))
            };

            RuntimePolicy.ResolveSelectionCleanups(steps, "选择合适的方法检查图层A")
                .Should().ContainSingle().Which.Should().Be("A");
        }

        [Fact]
        public void Selection_already_cleared_in_turn_needs_no_cleanup()
        {
            var steps = new[]
            {
                Step("management_SelectLayerByAttribute",
                    ("in_layer_or_view", "现状用地"), ("selection_type", "NEW_SELECTION"), ("where_clause", "1=1")),
                Step("management_CalculateField", ("in_table", "现状用地"), ("field_name", "F1")),
                Step("management_SelectLayerByAttribute",
                    ("in_layer_or_view", "现状用地"), ("selection_type", "CLEAR_SELECTION"), ("where_clause", ""))
            };

            RuntimePolicy.ResolveSelectionCleanups(steps).Should().BeEmpty();
        }

        [Fact]
        public void Select_by_location_uses_in_layer_parameter()
        {
            var steps = new[]
            {
                Step("management_SelectLayerByLocation",
                    ("in_layer", "道路面"), ("overlap_type", "INTERSECT"), ("select_features", "项目红线")),
                Step("analysis_Statistics", ("in_table", "道路面"), ("out_table", "s1"))
            };

            RuntimePolicy.ResolveSelectionCleanups(steps)
                .Should().ContainSingle().Which.Should().Be("道路面");
        }

        [Fact]
        public void Missing_selection_type_defaults_to_new_selection()
        {
            // GP 缺省 selection_type = NEW_SELECTION，同样要清
            var steps = new[]
            {
                Step("management_SelectLayerByAttribute",
                    ("in_layer_or_view", "现状用地"), ("where_clause", "A=1")),
                Step("management_CalculateField", ("in_table", "现状用地"), ("field_name", "F1"))
            };

            RuntimePolicy.ResolveSelectionCleanups(steps)
                .Should().ContainSingle().Which.Should().Be("现状用地");
        }

        [Fact]
        public void Multiple_layers_cleaned_in_execution_order()
        {
            var steps = new[]
            {
                Step("management_SelectLayerByAttribute",
                    ("in_layer_or_view", "图层B"), ("selection_type", "NEW_SELECTION"), ("where_clause", "1=1")),
                Step("management_SelectLayerByAttribute",
                    ("in_layer_or_view", "图层A"), ("selection_type", "NEW_SELECTION"), ("where_clause", "1=1")),
                Step("management_Merge", ("inputs", "图层A;图层B"), ("output", "合并"))
            };

            RuntimePolicy.ResolveSelectionCleanups(steps)
                .Should().ContainInOrder("图层B", "图层A");
        }

        [Fact]
        public void Non_selection_tools_produce_no_cleanup()
        {
            var steps = new[]
            {
                Step("analysis_Clip", ("in_features", "A"), ("clip_features", "B"), ("out_feature_class", "C")),
                Step("management_CalculateField", ("in_table", "C"), ("field_name", "F1"))
            };

            RuntimePolicy.ResolveSelectionCleanups(steps).Should().BeEmpty();
        }

        [Fact]
        public void Clear_call_shape_is_defined_by_policy_single_source()
        {
            // 审查项 F1：选择集工具名/参数知识单点在 RuntimePolicy
            var call = RuntimePolicy.BuildSelectionClearCall("现状用地");

            call.Name.Should().Be("management_SelectLayerByAttribute");
            call.Arguments["in_layer_or_view"].Should().Be("现状用地");
            call.Arguments["selection_type"].Should().Be("CLEAR_SELECTION");
            call.Arguments.Should().ContainKey("where_clause");
        }
    }
}
