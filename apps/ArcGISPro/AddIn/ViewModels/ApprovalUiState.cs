using System;
using System.Collections.Generic;
using System.Linq;
using SpatialAgent.Runtime.Contracts;

namespace ArcGISProAIAgent.ViewModels
{
    internal sealed class ApprovalUiState
    {
        public bool HasPendingApproval { get; init; }
        public string PendingApprovalText { get; init; } = "";
        public string PendingApprovalReason { get; init; } = "";
        public string PendingRequestText { get; init; } = "";
        public ApprovalKind? PendingApprovalKind { get; init; }
        public ApprovalTicket? PendingApprovalTicket { get; init; }
        public List<string> PendingTools { get; init; } = new();

        public bool CanApprove =>
            HasPendingApproval &&
            !string.IsNullOrWhiteSpace(PendingRequestText) &&
            PendingApprovalTicket != null;
    }

    internal static class ApprovalUiStateMachine
    {
        public static ApprovalUiState Empty() => new();

        public static ApprovalUiState FromResponse(AgentResponse resp, string requestText)
        {
            var ticket = resp.PendingApproval;
            var toolLabels = ApprovalCopyBuilder.BuildToolTags(ticket, resp.PendingTools);

            return new ApprovalUiState
            {
                HasPendingApproval = resp.RequiresApproval,
                PendingApprovalText = ApprovalCopyBuilder.BuildHeadline(ticket, resp.PendingTools),
                PendingApprovalReason = ApprovalCopyBuilder.BuildReason(ticket),
                PendingRequestText = requestText,
                PendingApprovalKind = resp.ApprovalKind,
                PendingApprovalTicket = ticket,
                PendingTools = toolLabels
            };
        }

        public static ApprovalUiState FromCheckpoint(AgentRunState restored)
        {
            var ticket = restored.PendingApproval;
            var toolLabels = ApprovalCopyBuilder.BuildToolTags(ticket, restored.PendingTools);

            return new ApprovalUiState
            {
                HasPendingApproval = restored.Status == SessionStatus.WaitingForApproval,
                PendingApprovalText = ApprovalCopyBuilder.BuildHeadline(ticket, restored.PendingTools, isRestored: true),
                PendingApprovalReason = ApprovalCopyBuilder.BuildReason(ticket, isRestored: true),
                PendingRequestText = restored.UserMessage ?? "",
                PendingApprovalKind = restored.PendingApproval?.Kind,
                PendingApprovalTicket = ticket,
                PendingTools = toolLabels
            };
        }
    }

    internal static class ApprovalCopyBuilder
    {
        public static string BuildHeadline(
            ApprovalTicket? ticket,
            IEnumerable<string>? fallbackTools = null,
            bool isRestored = false)
        {
            if (ticket == null)
                return isRestored ? "待恢复确认的操作" : "需要你确认的操作";

            string action = DescribeAction(ticket.CanonicalToolName, ticket.ToolName);
            if (IsExactBatch(ticket))
                return $"{action}前，请确认这个包含 {ticket.CoveredCalls.Count} 项操作的精确批次";

            string target = BuildTargetPhrase(ticket.Target);

            if (!string.IsNullOrWhiteSpace(target))
                return $"{action}前，请确认 {target}";

            return ticket.Kind == ApprovalKind.DataInfluence
                ? $"{action}前，需要确认影响范围"
                : $"{action}前，请确认本步操作";
        }

        public static string BuildReason(ApprovalTicket? ticket, bool isRestored = false)
        {
            if (ticket == null)
            {
                return isRestored
                    ? "会话恢复到了审批节点。请确认这个步骤是否继续执行。"
                    : "这个步骤需要人工确认后才能继续执行。";
            }

            string action = DescribeAction(ticket.CanonicalToolName, ticket.ToolName);
            string target = BuildTargetPhrase(ticket.Target);
            var evidence = ticket.Evidence ?? new ApprovalEvidence();
            var lines = new List<string>();

            if (IsExactBatch(ticket))
            {
                lines.Add($"该审批精确覆盖以下 {ticket.CoveredCalls.Count} 项操作；任一调用参数变化都会使本次批准失效：");
                lines.AddRange(ticket.CoveredCalls.Select(call =>
                {
                    string callAction = DescribeAction(call.CanonicalToolName, call.ToolName);
                    string callTarget = BuildTargetPhrase(call.Target);
                    return string.IsNullOrWhiteSpace(callTarget)
                        ? $"· 步骤 {call.StepId}：{callAction}（目标尚未解析，请按调用签名核对）"
                        : $"· 步骤 {call.StepId}：{callAction} {callTarget}";
                }));
            }

            if (!IsExactBatch(ticket) && ticket.Kind == ApprovalKind.DataInfluence)
            {
                if (!evidence.IsReliable)
                {
                    lines.Add(string.IsNullOrWhiteSpace(target)
                        ? $"系统暂时无法可靠判断“{action}”会影响哪些对象，请谨慎确认后再继续。"
                        : $"系统暂时无法可靠判断“{action}”会对 {target} 的哪些对象产生影响，请谨慎确认后再继续。");
                }
                else
                {
                    lines.Add(BuildInfluenceLead(action, target));
                }
            }
            else if (!IsExactBatch(ticket))
            {
                lines.Add(string.IsNullOrWhiteSpace(target)
                    ? $"“{action}”可能删除、覆盖或重建现有数据，已先暂停，请确认后再继续。"
                    : $"“{action}”可能直接改动 {target}，且回退成本较高，已先暂停，请确认后再继续。");
            }

            string impactLine = BuildImpactLine(ticket, evidence);
            if (!string.IsNullOrWhiteSpace(impactLine))
                lines.Add(impactLine);

            string scopeLine = BuildScopeLine(ticket.Target, evidence);
            if (!string.IsNullOrWhiteSpace(scopeLine))
                lines.Add(scopeLine);

            string confidenceLine = BuildConfidenceLine(evidence);
            if (!string.IsNullOrWhiteSpace(confidenceLine))
                lines.Add(confidenceLine);

            string backupLine = BuildBackupLine(evidence);
            if (!string.IsNullOrWhiteSpace(backupLine))
                lines.Add(backupLine);

            return string.Join("\n", lines.Where(s => !string.IsNullOrWhiteSpace(s)));
        }

        public static List<string> BuildToolTags(ApprovalTicket? ticket, IEnumerable<string>? fallbackTools = null)
        {
            var tags = new List<string>();

            if (ticket != null)
            {
                if (IsExactBatch(ticket))
                {
                    foreach (var call in ticket.CoveredCalls)
                    {
                        string action = DescribeAction(call.CanonicalToolName, call.ToolName);
                        string layer = FirstNonEmpty(call.Target.Layer, call.Target.Dataset, call.Target.OutputTarget);
                        string field = call.Target.Field?.Trim() ?? "";
                        string tag = !string.IsNullOrWhiteSpace(layer) && !string.IsNullOrWhiteSpace(field)
                            ? $"{action} · {layer}.{field}"
                            : !string.IsNullOrWhiteSpace(layer)
                                ? $"{action} · {layer}"
                                : $"{action} · 步骤 {call.StepId}";
                        if (!tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                            tags.Add(tag);
                    }
                }
                else
                {
                    string action = DescribeAction(ticket.CanonicalToolName, ticket.ToolName);
                    string layer = FirstNonEmpty(ticket.Target.Layer, ticket.Target.Dataset, ticket.Target.OutputTarget);
                    string field = ticket.Target.Field?.Trim() ?? "";

                    if (!string.IsNullOrWhiteSpace(layer) && !string.IsNullOrWhiteSpace(field))
                        tags.Add($"{action} · {layer}.{field}");
                    else if (!string.IsNullOrWhiteSpace(layer))
                        tags.Add($"{action} · {layer}");
                    else
                        tags.Add(action);
                }
            }

            if (fallbackTools != null)
            {
                foreach (var raw in fallbackTools.Where(t => !string.IsNullOrWhiteSpace(t)))
                {
                    string mapped = DescribeAction(raw, raw);
                    if (!tags.Contains(mapped, StringComparer.OrdinalIgnoreCase))
                        tags.Add(mapped);
                }
            }

            if (tags.Count == 0)
                tags.Add("待确认步骤");

            return tags;
        }

        public static string DescribeAction(string? canonicalToolName, string? rawToolName)
        {
            string tool = !string.IsNullOrWhiteSpace(canonicalToolName)
                ? canonicalToolName!
                : rawToolName ?? "";

            // Extract action part after the category prefix (first underscore).
            // For cc_calculate_area, this gives "calculate_area" not just "area".
            string shortName = tool;
            int firstUnderscore = tool.IndexOf('_');
            if (firstUnderscore >= 0 && firstUnderscore < tool.Length - 1)
                shortName = tool[(firstUnderscore + 1)..];

            // Normalize snake_case → PascalCase for switch matching.
            // E.g. "calculate_area" → "CalculateArea".
            string normalized = shortName.Contains('_')
                ? string.Concat(shortName.Split('_').Select(seg =>
                    seg.Length > 0 ? char.ToUpperInvariant(seg[0]) + seg[1..].ToLowerInvariant() : ""))
                : shortName;

            return normalized switch
            {
                "CalculateField" => "更新字段值",
                "MapFieldValues" => "按映射批量赋值",
                "AddField" => "新增字段",
                "DeleteField" => "删除字段",
                "CalculateArea" => "写入面积结果",
                "CalculateLength" => "写入长度结果",
                "CalculateGeometryAttributes" => "更新几何属性",
                "EditVertices" => "修改对象形状",
                "Reshape" => "重塑对象形状",
                "SelectLayerByAttribute" => "按条件筛选对象",
                "CopyFeatures" => "复制图层数据",
                "Buffer" => "生成缓冲区",
                "Clip" => "裁剪图层",
                "Intersect" => "叠加相交",
                "Erase" => "擦除目标区域",
                "Dissolve" => "融合对象",
                "Merge" => "合并图层",
                "Project" => "转换坐标系",
                "Statistics" => "汇总统计",
                "SummaryStatistics" => "汇总统计",
                _ when string.IsNullOrWhiteSpace(shortName) => "执行工具操作",
                _ => shortName
            };
        }

        public static string BuildTargetPhrase(ApprovalTarget? target)
        {
            if (target == null)
                return "";

            string layer = FirstNonEmpty(target.Layer, target.Dataset, target.OutputTarget);
            string field = target.Field?.Trim() ?? "";
            string output = target.OutputTarget?.Trim() ?? "";

            if (!string.IsNullOrWhiteSpace(field) && (field.Contains(',') || field.Contains('，')))
            {
                var fields = field
                    .Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList();
                if (!string.IsNullOrWhiteSpace(layer) && fields.Count > 1)
                    return $"“{layer}”图层的 {fields.Count} 个字段（{string.Join("、", fields)}）";
            }

            if (!string.IsNullOrWhiteSpace(layer) && !string.IsNullOrWhiteSpace(field))
                return $"“{layer}”图层的“{field}”字段";
            if (!string.IsNullOrWhiteSpace(layer))
                return $"“{layer}”图层";
            if (!string.IsNullOrWhiteSpace(output))
                return $"输出到“{output}”";

            return "";
        }

        private static string BuildInfluenceLead(string action, string target)
        {
            string objectText = string.IsNullOrWhiteSpace(target) ? "现有成果" : target;

            if (action.Contains("面积", StringComparison.Ordinal))
                return $"这一步会重算 {objectText} 中已有的面积结果，影响范围较大，因此先暂停等待确认。";

            if (action.Contains("长度", StringComparison.Ordinal) || action.Contains("几何属性", StringComparison.Ordinal))
                return $"这一步会重算 {objectText} 中已有的几何成果，影响范围较大，因此先暂停等待确认。";

            if (action.Contains("字段值", StringComparison.Ordinal) || action.Contains("赋值", StringComparison.Ordinal))
                return $"这一步会批量改写 {objectText} 中已有的填报结果，影响范围较大，因此先暂停等待确认。";

            if (action.Contains("形状", StringComparison.Ordinal))
                return $"这一步会直接改变 {objectText} 的空间形状或边界，影响范围较大，因此先暂停等待确认。";

            return $"这一步会直接改动 {objectText}，且影响范围较大，因此先暂停等待确认。";
        }

        private static string BuildImpactLine(ApprovalTicket ticket, ApprovalEvidence evidence)
        {
            var parts = new List<string>();

            string modifySubtypeLine = BuildModifySubtypeLine(ticket, evidence);
            if (!string.IsNullOrWhiteSpace(modifySubtypeLine))
                parts.Add(modifySubtypeLine);

            if (evidence.ScopeObjectCount.HasValue && evidence.ScopeObjectCount.Value >= 0)
                parts.Add($"本次涉及对象约 {evidence.ScopeObjectCount.Value} 个");

            if (evidence.TargetFieldCount.HasValue && evidence.TargetFieldCount.Value > 1)
                parts.Add($"会同时写入 {evidence.TargetFieldCount.Value} 个目标字段");

            if (evidence.ScopeValueCount.HasValue && evidence.ScopeValueCount.Value >= 0)
                parts.Add($"目标写入单元约 {evidence.ScopeValueCount.Value} 个");

            if (evidence.ExistingNonEmptyTargetCount.HasValue && evidence.ExistingNonEmptyTargetCount.Value > 0)
                parts.Add($"其中已有成果会被改写约 {evidence.ExistingNonEmptyTargetCount.Value} 个");

            return parts.Count == 0 ? "" : string.Join("，", parts) + "。";
        }

        private static string BuildModifySubtypeLine(ApprovalTicket ticket, ApprovalEvidence evidence)
        {
            string action = DescribeAction(ticket.CanonicalToolName, ticket.ToolName);

            if (action.Contains("面积", StringComparison.Ordinal))
                return "这是一次面积成果覆写。";

            if (action.Contains("长度", StringComparison.Ordinal) || action.Contains("几何属性", StringComparison.Ordinal))
                return "这是一次几何成果覆写。";

            if (action.Contains("字段值", StringComparison.Ordinal) || action.Contains("赋值", StringComparison.Ordinal))
                return "这是一次字段成果批量覆写。";

            if (action.Contains("形状", StringComparison.Ordinal))
                return "这是一次空间形状修改。";

            return evidence.ModifySubtype switch
            {
                ToolModifySubtype.QuantityGateCandidate => "这一步会直接写入现有图层字段。",
                ToolModifySubtype.SchemaOrSystemModify => "这一步涉及字段、结构或系统状态修改。",
                ToolModifySubtype.ManualReviewModify => "这一步的影响边界暂时不适合自动判断，建议人工再核对一次。",
                ToolModifySubtype.SpecializedModify => "这一步属于带专门业务语义的修改操作。",
                _ => ""
            };
        }

        private static string BuildScopeLine(ApprovalTarget? target, ApprovalEvidence evidence)
        {
            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(target?.WhereClause))
                parts.Add("当前步骤带有筛选条件");
            else if (evidence.UsesFilteredScope)
                parts.Add("当前步骤带有过滤条件");

            if (evidence.UsesSelectionScope)
                parts.Add("当前步骤已按选择集收窄范围");

            return parts.Count == 0 ? "" : string.Join("，", parts) + "。";
        }

        private static string BuildConfidenceLine(ApprovalEvidence evidence)
        {
            if (evidence.IsReliable)
                return "";

            if (!string.IsNullOrWhiteSpace(evidence.ReliabilityReason))
                return $"暂停原因：{evidence.ReliabilityReason.Trim()}。";

            return "暂停原因：系统暂时无法可靠识别影响边界。";
        }

        private static string BuildBackupLine(ApprovalEvidence evidence)
        {
            if (!string.IsNullOrWhiteSpace(evidence.BackupTarget))
                return $"相关备份对象：{evidence.BackupTarget}。";

            if (!string.IsNullOrWhiteSpace(evidence.BackupPath))
                return $"备份位置：{evidence.BackupPath}。";

            return "";
        }

        private static string FirstNonEmpty(params string?[] values) =>
            values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "";

        private static bool IsExactBatch(ApprovalTicket ticket) =>
            ticket.Scope == ApprovalScope.ExactBatch && ticket.CoveredCalls.Count > 1;
    }
}
