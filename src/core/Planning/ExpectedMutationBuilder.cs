using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Core
{
    /// <summary>
    /// Derives a plan-level mutation budget from compiled steps and effects.
    /// Workspace-routed outputs are included so host probes close the loop with
    /// agent write routing (agentworkspace.gdb), without special-casing tools.
    /// </summary>
    public static class ExpectedMutationBuilder
    {
        public static ExpectedMutationSet Build(AgentPlan plan)
        {
            var set = new ExpectedMutationSet();
            foreach (var step in plan.Steps)
            {
                var effect = step.Effect;
                var effects = effect?.Profile.Effects ?? DataEffectKind.None;
                if ((effects & ~DataEffectKind.ReadData) == DataEffectKind.None
                    && step.WriteSet.Count == 0)
                    continue;

                var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                void Add(string? value)
                {
                    if (string.IsNullOrWhiteSpace(value))
                        return;
                    keys.Add(value.Trim());
                }

                foreach (string write in step.WriteSet)
                    Add(write);
                if (effect != null)
                {
                    Add(effect.Target.Dataset);
                    Add(effect.Target.Layer);
                    Add(effect.Target.OutputTarget);
                }

                if (step.ToolCall?.Arguments != null)
                {
                    foreach (var pair in step.ToolCall.Arguments)
                    {
                        string name = pair.Key ?? "";
                        if (ParameterSemanticResolver.Classify(name) ==
                            ParameterSemanticKind.OutputDataset)
                        {
                            Add(pair.Value?.ToString());
                        }
                    }
                }

                foreach (string key in keys)
                {
                    set.Items.Add(new ExpectedMutation
                    {
                        ResourceKey = key,
                        Dataset = effect?.Target.Dataset ?? "",
                        Layer = effect?.Target.Layer ?? "",
                        OutputTarget = effect?.Target.OutputTarget ?? key,
                        Effects = effects,
                        StepId = step.Id
                    });
                }
            }

            return set;
        }
    }
}
