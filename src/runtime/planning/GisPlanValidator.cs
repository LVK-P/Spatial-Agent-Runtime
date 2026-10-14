using System.Collections.Generic;
using System.Linq;
using SpatialAgent.Core;
using SpatialAgent.Runtime.Contracts;

// ================================================================
// GisPlanValidator.cs — GIS 领域计划校验器
//
// 在 LLM 生成的 AgentPlan 执行前校验 GIS 特有的约束：
// ① CalculateField 依赖的字段必须先 AddField
// ② 输出路径冲突检测（同一名称被多个步骤输出）
// ③ (未来) Project 前检测跨坐标系操作
// ④ (未来) Select 前自动补充 MakeFeatureLayer
//
// 纯逻辑，零 ArcGIS 依赖 — 可在 Runtime.Tests 中直接测试。
//
// 来源：跨实现架构审查后的计划验证边界。
// ================================================================
// English：================================================================ GisPlanValidator.cs — GIS Domain Planning Checker: Validates GIS-specific constraints before the LLM-generated AgentPlan is executed: (1) Fields dependent on CalculateField must be AddField (2) Output path collision detection (output by multiple steps with the same name) (3) (Future) Detect cross-coordinate operations before Project (4) (Future) Automatically supplement MakeFeatureLayer before Select Pure Logic, zero ArcGIS dependency — can be tested directly in Runtime.Tests. Source: Across the Implementation Architecture Review Plan Validation Boundary. ================================================================
namespace SpatialAgent.Runtime.Planning
{
    /// <summary>
    /// Validates AgentPlans against GIS domain constraints.
    /// Detection-only phase — reports issues, does not yet auto-correct.
    /// </summary>
    /// <remarks>中文：验证代理计划是否符合GIS域约束。仅检测阶段——报告问题，尚未自动更正。</remarks>
    public class GisPlanValidator : IPlanValidator
    {
        private readonly IReadOnlyToolGateway? _tools;

        public GisPlanValidator(IReadOnlyToolGateway? tools = null)
        {
            _tools = tools;
        }

        public PlanValidationResult Validate(AgentPlan plan, DomainContext context)
        {
            var result = new PlanValidationResult { IsValid = true };

            // Track state across steps
            // 中文：跨步跟踪状态
            var fieldAdds = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var outputs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var stepIds = new HashSet<int>();

            for (int i = 0; i < plan.Steps.Count; i++)
            {
                var step = plan.Steps[i];
                if (!stepIds.Add(step.Id))
                {
                    AddError(result, i, step.ToolCall?.Name ?? "",
                        $"计划中存在重复步骤 ID：{step.Id}。",
                        "重新编译计划并为每个步骤分配唯一 ID");
                }

                if (step.ToolCall == null)
                {
                    AddError(result, i, "", "计划步骤缺少工具调用。", "删除空步骤或补充工具调用");
                    continue;
                }

                string toolName = step.ToolCall.Name;

                // ── Rule 1: CalculateField field must be pre-added ──
                // 中文：规则1：计算字段必须预先添加
                if (toolName.Contains("CalculateField"))
                {
                    var layer = GetArg(step, "in_table");
                    var field = GetArg(step, "field_name");

                    if (layer != null && field != null
                        && fieldAdds.TryGetValue(layer, out var addedFields)
                        && addedFields.Count > 0)
                    {
                        if (!addedFields.Contains(field))
                        {
                            result.Issues.Add(new PlanIssue
                            {
                                Severity = IssueSeverity.Warning,
                                ConstraintClass =
                                    PlanConstraintClass.DataIntegrity,
                                Enforcement =
                                    ConstraintEnforcement.Observe,
                                StepIndex = i,
                                ToolName = toolName,
                                Description =
                                    $"CalculateField 目标字段 '{field}' 未被 AddField。" +
                                    $"已 AddField 的是 '{string.Join("、", addedFields)}'。",
                                FixSuggestion =
                                    $"先执行 AddField(in_table={layer}, field_name={field})"
                            });
                        }
                    }
                    // Note: not erroring if field isn't in fieldAdds —
                    // the field might already exist in the layer schema.
                    // 中文：注意：如果字段不在 fieldAdd 中，我不会出错——该字段可能已经存在于图层模式中。
                }

                // Track field additions
                // 中文：赛道场地扩建
                if (toolName.Contains("AddField"))
                {
                    var layer = GetArg(step, "in_table");
                    var field = GetArg(step, "field_name");
                    if (layer != null && field != null)
                    {
                        if (!fieldAdds.TryGetValue(layer, out var fields))
                        {
                            fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            fieldAdds[layer] = fields;
                        }
                        fields.Add(field);
                    }
                }

                // ── Rule 2: Output path collision detection ──
                // 中文：规则2：输出路径碰撞检测
                foreach (var key in ResolveOutputParameters(step))
                {
                    var path = step.ToolCall.Arguments[key]?.ToString();
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    path = NormalizePath(path);

                    if (outputs.TryGetValue(path, out var prevIdx))
                    {
                        AddError(result, i, toolName,
                            $"输出 '{path}' 与步骤 {prevIdx + 1} 的输出路径重复，执行后将覆盖先前结果。",
                            "为输出使用不同的要素类名称");
                    }
                    outputs[path] = i;
                }
            }

            ValidateDependencies(plan, result);

            return result;
        }

        private static void ValidateDependencies(AgentPlan plan, PlanValidationResult result)
        {
            var indexById = plan.Steps
                .Select((step, index) => (step.Id, index))
                .GroupBy(item => item.Id)
                .ToDictionary(group => group.Key, group => group.First().index);

            for (int index = 0; index < plan.Steps.Count; index++)
            {
                var step = plan.Steps[index];
                foreach (int dependency in
                         (step.DependsOn ?? Enumerable.Empty<int>())
                             .Concat(step.RequiresSuccessfulSteps
                                 ?? Enumerable.Empty<int>())
                             .Distinct())
                {
                    if (!indexById.TryGetValue(dependency, out int dependencyIndex))
                    {
                        AddError(result, index, step.ToolCall?.Name ?? "",
                            $"步骤依赖不存在：{dependency}。",
                            "重新编译计划并移除悬空依赖");
                    }
                    else if (dependencyIndex >= index)
                    {
                        AddError(result, index, step.ToolCall?.Name ?? "",
                            $"步骤依赖 {dependency} 没有位于当前步骤之前。",
                            "调整步骤顺序，确保依赖先执行");
                    }
                }
            }
        }

        private static void AddError(
            PlanValidationResult result,
            int stepIndex,
            string toolName,
            string description,
            string? fixSuggestion)
        {
            result.Issues.Add(new PlanIssue
            {
                Severity = IssueSeverity.Error,
                ConstraintClass = PlanConstraintClass.DataIntegrity,
                Enforcement = ConstraintEnforcement.HardBlock,
                StepIndex = stepIndex,
                ToolName = toolName,
                Description = description,
                FixSuggestion = fixSuggestion
            });
            result.IsValid = false;
        }

        private static string NormalizePath(string path) =>
            path.Trim().Replace('/', '\\').TrimEnd('\\');

        private IEnumerable<string> ResolveOutputParameters(PlanStep step)
        {
            var call = step.ToolCall!;
            string canonical = _tools?.ResolveName(call.Name) ?? call.Name;
            var descriptor = _tools?.GetDescriptor(canonical);
            if (descriptor is IToolDataflowDescriptor flow
                && flow.OutputParameters.Count > 0)
            {
                var declared = flow.OutputParameters.ToHashSet(
                    StringComparer.OrdinalIgnoreCase);
                return call.Arguments.Keys.Where(declared.Contains);
            }

            return call.Arguments.Keys.Where(
                ParameterNameClassifier.IsOutputParameter);
        }

        private static string? GetArg(PlanStep step, string paramName)
        {
            if (step.ToolCall == null) return null;
            if (step.ToolCall.Arguments.TryGetValue(paramName, out var val))
                return val?.ToString();
            return null;
        }
    }
}
