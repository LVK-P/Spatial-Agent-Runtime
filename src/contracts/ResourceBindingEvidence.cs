namespace SpatialAgent.Runtime.Contracts
{
    public sealed class ResourceResolutionEvidence
    {
        public string EvidenceId { get; set; } = "";
        public string BindingMention { get; set; } = "";
        public string CandidateId { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string MapMemberId { get; set; } = "";
        public string DatasetId { get; set; } = "";
        public string SchemaFingerprint { get; set; } = "";
        public string GeometryType { get; set; } = "";
        public List<string> Fields { get; set; } = new();
        public string LineageRoot { get; set; } = "";
        public int LineageDepth { get; set; }
        public string ToolCallId { get; set; } = "";
        public int StepId { get; set; }
        public bool IdentityMatched { get; set; }
        public bool SchemaMatched { get; set; }
        public bool LineageMatched { get; set; }
        public bool IsAdmissible =>
            IdentityMatched && SchemaMatched && LineageMatched;
        public string Detail { get; set; } = "";
    }

    public sealed class ResourceBindingPatch
    {
        public string BindingMention { get; set; } = "";
        public GoalResourceResolutionStatus PreviousStatus { get; set; }
        public GoalResourceResolutionStatus NextStatus { get; set; }
        public string ResolvedCandidateId { get; set; } = "";
        public string ResolvedTarget { get; set; } = "";
        public List<string> EvidenceCandidateIds { get; set; } = new();
        public List<string> EvidenceIds { get; set; } = new();
        public string Reason { get; set; } = "";
    }

    public sealed class ResourceBindingPromotionResult
    {
        public List<ResourceResolutionEvidence> Evidence { get; set; } = new();
        public List<ResourceBindingPatch> Patches { get; set; } = new();
        public bool HasChanges => Patches.Any(patch =>
            patch.NextStatus != patch.PreviousStatus
            || !string.IsNullOrWhiteSpace(patch.ResolvedCandidateId));
    }

    /// <summary>
    /// Evaluates completed support probes and proposes binding patches. The
    /// policy cannot execute tools, approve mutations or parse model prose.
    /// </summary>
    public interface IResourceBindingEvidencePromoter
    {
        ResourceBindingPromotionResult Evaluate(
            ResolvedGoalScope scope,
            AgentPlan plan,
            IReadOnlyList<StepExecutionResult> results,
            DomainContext domain);
    }
}
