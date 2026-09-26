using System;
using System.Collections.Generic;
using System.Linq;
using SpatialAgent.Core;
using SpatialAgent.Runtime;
using SpatialAgent.Runtime.Contracts;

// ────────────────────────────────────────────────────────────────
// SafetyGate.cs — Tool risk classification and permission enforcement.
//
// All tools are classified into four risk tiers per ADR 0051:
//   ReadOnly    — 无需确认，直接执行
//   Increment   — 创建型 Modify（Buffer/Clip/Statistics 等），直接执行，不备份
//   Modify      — 覆写型 Modify（CalculateField/cc_calculate_area 重算等），直接执行，不备份
//   Destructive — 默认阻止；批准后先自动备份再执行，备份失败则拒绝执行
//
// Evaluate() enforces the session permission level.
// ────────────────────────────────────────────────────────────────
namespace SpatialAgent.Services
{
    /// <summary>
    /// Approval request sent to the UI.
    /// </summary>
    public class ApprovalRequest
    {
        public string Operation { get; set; } = "";
        public string ToolName { get; set; } = "";
        public Dictionary<string, object?> Parameters { get; set; } = new();
        public ToolRiskLevel Level { get; set; }
        public string? BackupPath { get; set; }
        public string WarningMessage { get; set; } = "";
    }

    /// <summary>
    /// SafetyGate — 分类工具安全级别，生成审批请求，触发备份。
    /// </summary>
    public class SafetyGate : SpatialAgent.Runtime.Contracts.ISafetyGate
    {
        private readonly Dictionary<string, ToolRiskLevel> _classifications = new(StringComparer.OrdinalIgnoreCase);
        private ToolRegistry? _toolRegistry;

        public SafetyGate()
        {
            RegisterDefaults();
            RegisterCatalogClassifications();
        }

        /// <summary>Inject ToolRegistry for descriptor-based risk lookup (fallback for dynamic tools).</summary>
        public void SetToolRegistry(ToolRegistry registry) => _toolRegistry = registry;

        /// <summary>
        /// Register tool safety classification (supports wildcard prefix matching).
        /// </summary>
        public void RegisterClassification(string toolNamePattern, ToolRiskLevel level)
        {
            _classifications[toolNamePattern] = level;
        }

        /// <summary>
        /// Classify a tool into its safety level.
        /// </summary>
        public ToolRiskLevel Classify(string toolName)
        {
            // Normalize through resolver for consistent matching
            string schemaName = ToolNameResolver.NormalizeSchemaName(toolName);
            string shortName = ToolNameResolver.ExtractShortName(toolName);

            if (TryLookupExactRule(schemaName, out var exactLevel))
                return exactLevel;

            if (TryLookupDescriptor(toolName, schemaName, shortName, out var descriptorLevel))
                return descriptorLevel;

            if (TryLookupShortOrWildcardRule(shortName, out var ruleLevel))
                return ruleLevel;

            // ★ P0-3 修复（2026-07-20）：
            // 未知工具 = 未经验证 → 最严格分类（Destructive），要求用户审批。
            // 理由：
            //   1. "未知"可能是 LLM 幻觉编造的工具名，也可能来自未审计的本地 manifest
            //   2. 新工具应显式注册风险级别后才能放行
            //   3. GIS 垂直场景下，误执行一次工具的成本远高于多一次审批确认
            //   4. 2026 年安全实践：unknown → deny/require-approval，不是 unknown → allow
            // 原逻辑为 "fail closed as Modify so ReadOnly mode cannot execute them" ——
            //   这在 ReadOnly 模式下有效，但在 FullAccess 模式下 Modify = 直接放行。
            AgentDiagnostics.Warn("SafetyGate.Classify",
                $"工具「{toolName}」未在安全分类表中注册。" +
                "默认归类为 Destructive——需要用户审批才能执行。" +
                "如果这是新增工具，请在 SafetyGate.RegisterClassification() 中显式注册其风险级别。");
            return ToolRiskLevel.Destructive;
        }

        public SafetyDecision Evaluate(string toolName, AgentPermissionLevel permissionLevel)
        {
            var level = Classify(toolName);
            var decision = new SafetyDecision
            {
                ToolName = toolName,
                RiskLevel = level
            };

            switch (permissionLevel)
            {
                case AgentPermissionLevel.FullAccess:
                    decision.Kind = SafetyDecisionKind.Allow;
                    return decision;

                case AgentPermissionLevel.RequestApproval:
                    if (level == ToolRiskLevel.Destructive)
                    {
                        decision.Kind = SafetyDecisionKind.RequireApproval;
                        decision.Reason = GetWarningMessage(toolName, level);
                        return decision;
                    }

                    decision.Kind = SafetyDecisionKind.Allow;
                    return decision;

                case AgentPermissionLevel.ReadOnly:
                    if (level == ToolRiskLevel.ReadOnly)
                    {
                        decision.Kind = SafetyDecisionKind.Allow;
                        return decision;
                    }

                    decision.Kind = SafetyDecisionKind.Block;
                    decision.Reason = level == ToolRiskLevel.Destructive
                        ? $"当前权限为只读，无法执行不可逆操作：{toolName}"
                        : $"当前权限为只读，无法执行修改操作：{toolName}";
                    return decision;

                default:
                    decision.Kind = SafetyDecisionKind.Block;
                    decision.Reason = $"未知权限级别，已阻止工具：{toolName}";
                    return decision;
            }
        }

        private bool TryLookupExactRule(string schemaName, out ToolRiskLevel level)
        {
            return _classifications.TryGetValue(schemaName, out level);
        }

        private bool TryLookupShortOrWildcardRule(string shortName, out ToolRiskLevel level)
        {
            if (_classifications.TryGetValue(shortName, out level))
                return true;

            foreach (var kv in _classifications)
            {
                if (kv.Key.EndsWith("*") &&
                    shortName.StartsWith(kv.Key.TrimEnd('*'), StringComparison.OrdinalIgnoreCase))
                {
                    level = kv.Value;
                    return true;
                }
            }

            level = ToolRiskLevel.ReadOnly;
            return false;
        }

        private bool TryLookupDescriptor(string toolName, string schemaName, string shortName, out ToolRiskLevel level)
        {
            level = ToolRiskLevel.ReadOnly;
            if (_toolRegistry == null)
                return false;

            var desc = _toolRegistry.GetDescriptor(toolName)
                ?? _toolRegistry.GetDescriptor(schemaName)
                ?? _toolRegistry.GetDescriptor(shortName);

            if (desc == null)
                return false;

            // ★ ADR 0055：IToolSafetyDescriptor.SafetyProfile 优先于 Annotation。
            // ToolSafetySemantics.ResolveRisk() 会取 DeclaredRisk 和 MaxInternalRisk
            // 的高值，再回退到 FromAnnotation（未实现 IToolSafetyDescriptor 时）。
            level = ToolSafetySemantics.ResolveRisk(desc);
            return true;
        }

        /// <summary>
        /// Whether user confirmation is needed.
        /// </summary>
        public bool NeedsApproval(string toolName, out ApprovalRequest request)
        {
            ToolRiskLevel level = Classify(toolName);
            request = new ApprovalRequest
            {
                ToolName = toolName,
                Level = level,
                WarningMessage = GetWarningMessage(toolName, level)
            };

            return level == ToolRiskLevel.Destructive;
        }

        /// <summary>
        /// Whether this is a destructive operation.
        /// </summary>
        public bool IsDestructive(string toolName) => Classify(toolName) == ToolRiskLevel.Destructive;

        /// <summary>
        /// Whether a backup is needed before execution.
        /// </summary>
        public bool NeedsBackup(string toolName) => Classify(toolName) == ToolRiskLevel.Destructive;

        private string GetWarningMessage(string toolName, ToolRiskLevel level)
        {
            string shortName = toolName.Contains('.') ? toolName[(toolName.LastIndexOf('.') + 1)..] : toolName;

            string action = shortName switch
            {
                "DeleteField" => "删除字段",
                "DeleteFeatures" => "删除要素",
                "DeleteRows" => "删除记录",
                "Delete" => "删除数据",
                "TruncateTable" => "清空表",
                "AlterField" => "修改字段定义",
                "Erase" => "擦除要素",
                "CalculateField" => "覆盖字段值",
                _ => "修改数据"
            };

            return level switch
            {
                ToolRiskLevel.Destructive =>
                    $"⚠️ 危险操作：{action}\n此操作会永久修改数据。已自动备份。确认执行？",
                ToolRiskLevel.Modify =>
                    $"🔧 修改操作：{action}\n此操作会覆写已有数据。确认执行？",
                ToolRiskLevel.Increment =>
                    $"📝 创建操作：{action}\n将创建新数据，不会修改已有字段值。",
                _ => ""
            };
        }

        // ────────────────────────────────────────────────────────────────
        // Default safety classification
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// ★ 安全门一致性（2026-07-24）：
        /// - 非 GP 高级工具（get_map_info 等）在此注册。
        /// - GP 工具可由 RegisterCatalogClassifications() 从本地 catalog 加载并覆盖默认注册。
        /// - 此处保留 GP 工具条目作为 catalog 加载失败时的 fallback，其值与 catalog 保持一致。
        /// </summary>
        private void RegisterDefaults()
        {
            // ── ReadOnly ──
            foreach (string tool in new[] {
                "get_map_info", "list_layers", "describe_layer", "get_selection",
                "get_project_info", "SelectLayerByAttribute", "MakeFeatureLayer",
                "GetCount", "Search", "Describe"
            }) RegisterClassification(tool, ToolRiskLevel.ReadOnly);

            // ── Increment — 创建新数据/新结构，不覆写既有字段值 ──
            foreach (string tool in new[] {
                "Buffer", "Clip", "Intersect", "Union", "Merge", "Dissolve",
                "Near", "SpatialJoin", "Project", "CopyFeatures", "Append",
                "FeatureToPolygon", "Erase", "Statistics", "SummaryStatistics",
                "AddJoin", "AddField", "TableToTable"
            }) RegisterClassification(tool, ToolRiskLevel.Increment);

            // ── Modify — 覆写既有字段值 ──
            foreach (string tool in new[] {
                "CalculateField"
            }) RegisterClassification(tool, ToolRiskLevel.Modify);

            // ── Destructive — 删除数据/结构，不可逆 ──
            foreach (string tool in new[] {
                "DeleteField", "DeleteFeatures", "DeleteRows", "Delete",
                "TruncateTable", "AlterField"
            }) RegisterClassification(tool, ToolRiskLevel.Destructive);
        }

        private void RegisterCatalogClassifications()
        {
            foreach (var entry in ToolRiskCatalog.Instance.Entries)
            {
                if (!string.IsNullOrWhiteSpace(entry.FunctionName))
                    RegisterClassification(entry.FunctionName, entry.RiskLevel);

                if (!string.IsNullOrWhiteSpace(entry.ExecutionName))
                    RegisterClassification(entry.ExecutionName, entry.RiskLevel);
            }
        }
    }
}
