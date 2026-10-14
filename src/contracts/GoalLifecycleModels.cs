using System;
using System.Collections.Generic;
using System.Linq;

namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Relationship between a user turn and the currently active goal.
    /// A universal quantifier is deliberately not a continuation signal.
    /// </summary>
    /// <remarks>中文：用户回合与当前激活目标之间的关系。全称量词故意不是延续信号。</remarks>
    public enum GoalTurnRelation
    {
        New,
        Continue,
        Refine,
        Correct,
        Cancel
    }

    public enum GoalDeliverableKind
    {
        Dataset,
        Field,
        Metric,
        Classification,
        MapPresentation,
        Report
    }

    public sealed class GoalDeliverable
    {
        public string Id { get; set; } = "";
        public GoalDeliverableKind Kind { get; set; }
        public string Description { get; set; } = "";
        public string CapabilityId { get; set; } = "";
        public bool Required { get; set; } = true;
    }

    public enum GoalProofKind
    {
        TargetCoverage,
        CapabilityExecution,
        VerifiedPostcondition,
        ParameterSemantics,
        MapPresentation
    }

    public sealed class GoalProofObligation
    {
        public string Id { get; set; } = "";
        public GoalProofKind Kind { get; set; }
        public string CapabilityId { get; set; } = "";
        public string Description { get; set; } = "";
        public bool RequiresValidation { get; set; }
        public bool Required { get; set; } = true;
        public Dictionary<string, string> Expected { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Stable logical GIS capabilities. User language and ArcGIS tool names
    /// both map to these ids; neither layer depends directly on the other.
    /// </summary>
    /// <remarks>中文：稳定的逻辑 GIS 能力。用户语言和 ArcGIS 工具名称都映射到这些 ID，两个层次彼此不直接依赖。</remarks>
    public static class GisCapabilityIds
    {
        public const string SpatialAggregateWithin =
            "gis.spatial.aggregate.within";
        public const string MeasureArea = "gis.measure.area";
        public const string MeasureLength = "gis.measure.length";
        public const string ComputeMetric = "gis.metric.compute";
        public const string ClassifyNumeric = "gis.numeric.classify";
        public const string RenderMap = "gis.map.render";
        public const string TransformCrs = "gis.crs.transform";
        public const string AlterFieldSchema = "gis.schema.field";
        public const string CreateArtifact = "gis.artifact.create";
    }

    public sealed class CapabilityClosureResult
    {
        public List<string> RequiredCapabilities { get; set; } = new();
        public List<string> ProvidedCapabilities { get; set; } = new();
        public List<string> MissingCapabilities { get; set; } = new();

        /// <summary>
        /// Capabilities that no tool declares via the catalog, but at least one
        /// plan step's tool identity (name / description) plausibly relates to
        /// the capability. These are treated as Soft warnings rather than Hard
        /// errors — the tool probably provides the capability and the catalog
        /// mapping is just incomplete. Proof closure still applies post-exec.
        /// </summary>
        /// <remarks>中文：工具目录未声明这些能力，但至少一个计划步骤的工具标识（名称/描述）与能力合理相关。它们作为软警告而非硬错误处理，因为工具可能具备该能力，只是目录映射不完整；执行后的证据闭环仍然适用。</remarks>
        public List<string> PlausibleButUnconfirmed { get; set; } = new();

        public bool IsComplete => MissingCapabilities.Count == 0;

    }

    public sealed class GoalProofEvaluation
    {
        public List<string> RequiredProofs { get; set; } = new();
        public List<string> SatisfiedProofs { get; set; } = new();
        public List<string> MissingProofs { get; set; } = new();
        public bool IsComplete => MissingProofs.Count == 0;
    }

    public interface ITurnRelationResolver
    {
        GoalTurnRelation Resolve(
            string userMessage,
            ResolvedGoalScope? previousGoal,
            CompiledGoalContract? patch = null);
    }

    public interface IGoalScopeResolver
    {
        ResolvedGoalScope Resolve(
            string goal,
            ResolvedGoalScope? previousGoal,
            DomainContext context);
    }

    public interface ICapabilityCatalog
    {
        IReadOnlyList<string> ResolveProvidedCapabilities(
            IToolDescriptor? descriptor,
            ToolCall call,
            InvocationEffect? effect);

        IReadOnlyList<string> SelectToolNames(
            IEnumerable<string> requiredCapabilities,
            IEnumerable<IToolDescriptor> descriptors);
    }

    public interface ICapabilityClosureValidator
    {
        CapabilityClosureResult Validate(
            ResolvedGoalScope goal,
            AgentPlan plan);
    }

    public interface IGoalClosureEvaluator
    {
        GoalProofEvaluation Evaluate(
            ResolvedGoalScope goal,
            TargetCoverageResult? coverage,
            IReadOnlyList<StepExecutionResult> results);
    }
}
