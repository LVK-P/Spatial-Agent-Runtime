using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Where the estimator got its evidence for the data-influence assessment.
    /// Determines auditability and drives fail-closed behavior.
    /// </summary>
    public enum EvidenceSource
    {
        Describe,
        AttributeTableProbe,
        ToolDeclaredDefault,
        SchemaContract,
        FallbackInference,
        Unknown
    }

    public sealed class DataInfluenceContext
    {
        public string ToolName { get; init; } = "";
        public string CanonicalToolName { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public ToolSafetyProfile? SafetyProfile { get; init; }
        public ToolGovernanceMetadata? Governance { get; init; }
    }

    /// <summary>
    /// Probe result for one tool call. The estimator tracks overwrite risk from
    /// the real layer state and can aggregate multiple target fields on a layer.
    /// </summary>
    public class DataInfluenceEstimate
    {
        private int _existingNonEmptyTargetCount;

        public string LayerName { get; set; } = "";
        public string? FieldName { get; set; }
        public IReadOnlyList<string> FieldNames { get; set; } = Array.Empty<string>();
        public int FeatureCount { get; set; }

        /// <summary>
        /// Objects in the current execution scope. This can be the full layer,
        /// the current selection, or a conservative fallback.
        /// </summary>
        public int ScopeObjectCount { get; set; } = -1;

        /// <summary>
        /// Count of target fields that this write may update.
        /// </summary>
        public int TargetFieldCount { get; set; }

        /// <summary>
        /// Estimated write cells in the current scope.
        /// ScopeObjectCount * TargetFieldCount when both are known.
        /// </summary>
        public int ScopeValueCount { get; set; } = -1;

        /// <summary>
        /// Existing non-null values that would be overwritten.
        /// For multi-field writes this is the total across all target fields.
        /// </summary>
        public int ExistingNonEmptyTargetCount
        {
            get => _existingNonEmptyTargetCount;
            set => _existingNonEmptyTargetCount = value;
        }

        /// <summary>
        /// Backward-compatible alias for older scheduler/tests.
        /// </summary>
        public int NonNullCount
        {
            get => _existingNonEmptyTargetCount;
            set => _existingNonEmptyTargetCount = value;
        }

        /// <summary>
        /// Cells that are currently empty and are likely to be filled for the
        /// first time by this write.
        /// </summary>
        public int NewValueFillCount { get; set; }

        /// <summary>
        /// Estimated schema mutations caused by this call, for example implicit
        /// field creation before a calculate/write operation.
        /// This is retained for evidence only; quantity gate thresholds should
        /// not use it directly.
        /// </summary>
        public int SchemaMutationCount { get; set; }

        /// <summary>
        /// Estimated number of newly created outputs. Kept coarse for now.
        /// </summary>
        public int NewOutputCount { get; set; }

        public bool UsesSelectionScope { get; set; }
        public bool UsesFilteredScope { get; set; }
        public ToolModifySubtype ModifySubtype { get; set; } = ToolModifySubtype.Unknown;

        public bool IsReliable { get; set; } = true;
        public EvidenceSource Source { get; set; } = EvidenceSource.Describe;
        public string ReliabilityReason { get; set; } = "";

        /// <summary>
        /// Count used by the quantity gate. Prefer overwrite count when known;
        /// otherwise fall back to the inferred scope.
        /// </summary>
        public int EffectiveCount =>
            (FieldName != null || FieldNames.Count > 0)
                ? ExistingNonEmptyTargetCount
                : ScopeObjectCount >= 0 ? ScopeObjectCount : FeatureCount;

        public bool IsIncrementEffect =>
            (FieldName != null || FieldNames.Count > 0) && ExistingNonEmptyTargetCount == 0;

        public override string ToString()
        {
            if (string.IsNullOrEmpty(LayerName))
                return $"无法评估影响量（来源={Source}{(string.IsNullOrEmpty(ReliabilityReason) ? "" : $", {ReliabilityReason}")}）";

            string fieldLabel = BuildFieldLabel();
            string scopeText = ScopeObjectCount >= 0 ? $"，对象范围 {ScopeObjectCount} 条" : "";
            string valueScopeText = ScopeValueCount >= 0 ? $"，目标写入单元约 {ScopeValueCount} 个" : "";
            string selectionText = UsesSelectionScope ? "，已按选择集收窄" : "";
            string filterText = UsesFilteredScope ? "，存在过滤条件" : "";

            if (ExistingNonEmptyTargetCount == 0 && (FieldName != null || FieldNames.Count > 0))
                return $"“{LayerName}”的{fieldLabel}暂无已有值会被覆写{scopeText}{valueScopeText}{selectionText}{filterText}（来源={Source}）";

            return $"“{LayerName}”的{fieldLabel}约有 {EffectiveCount} 个已有值会被覆写{scopeText}{valueScopeText}{selectionText}{filterText}（来源={Source}）";
        }

        private string BuildFieldLabel()
        {
            if (FieldNames.Count > 1)
                return $"{FieldNames.Count} 个目标字段（{string.Join("、", FieldNames)}）";

            if (FieldNames.Count == 1)
                return $"“{FieldNames[0]}”字段";

            if (!string.IsNullOrWhiteSpace(FieldName))
                return $"“{FieldName}”字段";

            return "目标字段";
        }
    }

    /// <summary>
    /// Estimates the data influence of a tool call before execution.
    /// </summary>
    public interface IDataInfluenceEstimator
    {
        Task<DataInfluenceEstimate> EstimateAsync(ToolCall call, DataInfluenceContext? context = null);
        Task<IReadOnlyList<DataInfluenceEstimate>> EstimateBatchAsync(
            IReadOnlyList<ToolCall> calls,
            IReadOnlyList<DataInfluenceContext?>? contexts = null);
        void InvalidateCache();
    }
}
