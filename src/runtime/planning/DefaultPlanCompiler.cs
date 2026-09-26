using SpatialAgent.Runtime.Contracts;
using SpatialAgent.Core;

namespace SpatialAgent.Runtime.Planning
{
    /// <summary>
    /// Turns model-proposed calls into an immutable, evidence-bearing physical
    /// plan. It does not ask the model to restate a plan and therefore adds no
    /// latency for the common desktop path.
    /// </summary>
    public sealed class DefaultPlanCompiler : IPlanCompiler
    {
        private readonly ToolRegistry _tools;
        private readonly DomainPackRegistry _domainPacks;
        private readonly IInvocationEffectResolver _effects;
        private readonly ICapabilityCatalog _capabilities;

        public DefaultPlanCompiler(
            ToolRegistry tools,
            DomainPackRegistry domainPacks,
            IInvocationEffectResolver effects,
            ICapabilityCatalog? capabilities = null)
        {
            _tools = tools;
            _domainPacks = domainPacks;
            _effects = effects;
            _capabilities = capabilities ?? new DefaultCapabilityCatalog();
        }

        public AgentPlan Compile(PlanningRequest request, int firstStepId = 1)
        {
            var plan = new AgentPlan
            {
                Goal = request.Goal,
                ProjectRevision = request.ContextRevision
            };

            int id = firstStepId;
            foreach (var proposedCall in request.ToolCalls)
            {
                string canonical = _tools.ResolveName(proposedCall.Name) ?? proposedCall.Name;
                var descriptor = _tools.GetDescriptor(canonical);
                var call = CloneCall(proposedCall, canonical, descriptor);
                var effect = _effects.Resolve(descriptor, call, request.Domain);
                IDomainPack? pack = descriptor == null ? null : _domainPacks.Resolve(descriptor);
                EffectProfile? packProfile = descriptor == null ? null : pack?.ResolveEffectProfile(descriptor);
                if (packProfile != null)
                    effect.Profile = EffectProfilePolicy.MergeIncreaseOnly(
                        effect.Profile,
                        packProfile);

                var (reads, writes) = ResolveDataFlow(descriptor, call, effect);
                var step = new PlanStep
                {
                    Id = id++,
                    Description = descriptor?.Description ?? call.Name,
                    ToolCall = call,
                    RequestedToolName = proposedCall.Name,
                    CanonicalToolName = canonical,
                    ExecutionToolName = descriptor is IToolIdentityDescriptor identity
                        ? identity.ExecutionName
                        : canonical,
                    Effect = effect,
                    DomainPackId = pack?.Id ?? "generic",
                    ReadSet = reads,
                    WriteSet = writes,
                    Preconditions = BuildPreconditions(descriptor, effect),
                    Postconditions = BuildPostconditions(descriptor, effect),
                    ProvidedCapabilities = _capabilities
                        .ResolveProvidedCapabilities(descriptor, call, effect)
                        .ToList()
                };
                IReadOnlyList<string> appliedGoalParameters =
                    GoalParameterConstraintBinder.Apply(
                        call,
                        descriptor,
                        step.ProvidedCapabilities,
                        request.ParameterConstraints);
                if (appliedGoalParameters.Count > 0)
                    step.Preconditions.AddRange(appliedGoalParameters.Select(
                        item => "goal_parameter_bound:" + item));
                step.ProofRequirements = ParameterScopedProofBuilder.Build(
                    step,
                    descriptor,
                    request.ParameterConstraints,
                    request.ProofObligations,
                    request.Domain);
                step.ActionId = ApprovalProtocol.ComputeActionId(
                    request.GoalContractFingerprint,
                    step.ProvidedCapabilities,
                    effect.Target,
                    effect.DesiredTargetState,
                    request.ParameterConstraints,
                    ApprovalProtocol.ComputeCallSignature(
                        canonical,
                        call.Arguments));

                foreach (var prior in plan.Steps)
                {
                    if (HasDataHazard(prior, step))
                        step.DependsOn.Add(prior.Id);
                    if (HasSuccessDependency(prior, step))
                        step.RequiresSuccessfulSteps.Add(prior.Id);
                }

                // ArcGIS Pro's geoprocessing host remains a single writer even
                // when two writes have no detectable path overlap.
                if (step.WriteSet.Count > 0)
                {
                    var lastWriter = plan.Steps.LastOrDefault(
                        candidate => candidate.WriteSet.Count > 0);
                    if (lastWriter != null && !step.DependsOn.Contains(lastWriter.Id))
                        step.DependsOn.Add(lastWriter.Id);
                }

                plan.Steps.Add(step);
            }

            return plan;
        }

        private static ToolCall CloneCall(
            ToolCall call,
            string canonicalName,
            IToolDescriptor? descriptor)
        {
            var arguments = new Dictionary<string, object?>(
                call.Arguments,
                StringComparer.OrdinalIgnoreCase);
            foreach (var item in descriptor?.DefaultParameters
                         ?? new Dictionary<string, string>())
            {
                arguments.TryAdd(item.Key, item.Value);
            }
            return new ToolCall
            {
                Id = call.Id,
                Name = canonicalName,
                Arguments = arguments
            };
        }

        private static bool HasDataHazard(PlanStep prior, PlanStep current)
        {
            var priorReads = prior.ReadSet.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var priorWrites = prior.WriteSet.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var currentReads = current.ReadSet.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var currentWrites = current.WriteSet.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return priorWrites.Overlaps(currentReads)
                || priorReads.Overlaps(currentWrites)
                || priorWrites.Overlaps(currentWrites);
        }

        private static bool HasSuccessDependency(
            PlanStep prior,
            PlanStep current)
        {
            var priorWrites = prior.WriteSet.ToHashSet(
                StringComparer.OrdinalIgnoreCase);
            var currentReads = current.ReadSet.ToHashSet(
                StringComparer.OrdinalIgnoreCase);
            return priorWrites.Overlaps(currentReads);
        }

        private static (List<string> Reads, List<string> Writes) ResolveDataFlow(
            IToolDescriptor? descriptor,
            ToolCall call,
            InvocationEffect effect)
        {
            var reads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var writes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IReadOnlySet<string> declaredInputs = descriptor is IToolDataflowDescriptor flow
                ? flow.InputParameters.ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IReadOnlySet<string> declaredOutputs = descriptor is IToolDataflowDescriptor outputFlow
                ? outputFlow.OutputParameters.ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var argument in call.Arguments)
            {
                // Declared contracts are authoritative. Name heuristics are a
                // compatibility fallback only for legacy descriptors.
                bool output = declaredOutputs.Count > 0
                    ? declaredOutputs.Contains(argument.Key)
                    : ParameterNameClassifier.IsOutputParameter(argument.Key);
                bool input = declaredInputs.Count > 0
                    ? declaredInputs.Contains(argument.Key)
                    : ParameterNameClassifier.IsLikelyInputParameter(argument.Key);

                foreach (string value in ExtractDataValues(argument.Value))
                {
                    string identity = NormalizeDatasetIdentity(value);
                    if (string.IsNullOrWhiteSpace(identity))
                        continue;
                    if (output)
                        writes.Add(identity);
                    else if (input)
                        reads.Add(identity);
                }
            }

            if (effect.Profile.Effects.HasFlag(DataEffectKind.UpdateRows)
                || effect.Profile.Effects.HasFlag(DataEffectKind.DeleteRows)
                || effect.Profile.Effects.HasFlag(DataEffectKind.DeleteDataset)
                || effect.Profile.Effects.HasFlag(DataEffectKind.AlterSchema))
            {
                string target = effect.Target.Dataset ?? effect.Target.Layer ?? "";
                if (!string.IsNullOrWhiteSpace(target))
                {
                    target = NormalizeDatasetIdentity(target);
                    reads.Add(target);
                    writes.Add(target);
                }
            }

            return (reads.ToList(), writes.ToList());
        }

        private static IEnumerable<string> ExtractDataValues(object? value)
        {
            if (value == null)
                yield break;
            if (value is string text)
            {
                foreach (string part in text.Split(
                             ';',
                             StringSplitOptions.RemoveEmptyEntries
                             | StringSplitOptions.TrimEntries))
                    yield return part;
                yield break;
            }
            if (value is System.Collections.IEnumerable sequence)
            {
                foreach (var item in sequence)
                {
                    foreach (string nested in ExtractDataValues(item))
                        yield return nested;
                }
                yield break;
            }
            yield return value.ToString() ?? "";
        }

        private static string NormalizeDatasetIdentity(string value) =>
            value.Trim().Replace('/', '\\').TrimEnd('\\');

        private static List<string> BuildPreconditions(
            IToolDescriptor? descriptor,
            InvocationEffect effect)
        {
            var result = new List<string> { "tool_exists" };
            if (descriptor?.RequiredParameters.Count > 0)
                result.Add("required_parameters_valid");
            if (effect.Profile.SingleWriterRequired)
                result.Add("arcgis_single_writer");
            if (effect.Profile.RequiresInfluenceProbe)
                result.Add($"impact_probe:{effect.Profile.ProbeKind}");
            if (effect.Profile.RequiresBackup)
                result.Add("backup_before_write");
            return result;
        }

        private static List<string> BuildPostconditions(
            IToolDescriptor? descriptor,
            InvocationEffect effect)
        {
            var result = new List<string>();
            if (descriptor?.Verification != null)
                result.Add($"verify:{descriptor.Verification.Type}:{descriptor.Verification.Target}");
            if (effect.Profile.Effects.HasFlag(DataEffectKind.CreateDataset))
                result.Add("output_exists");
            if (effect.Profile.Effects.HasFlag(DataEffectKind.UpdateRows)
                || effect.Profile.Effects.HasFlag(DataEffectKind.DeleteRows))
                result.Add("affected_scope_matches_evidence");
            return result;
        }
    }
}
