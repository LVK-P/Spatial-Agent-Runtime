using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Core
{
    /// <summary>
    /// Converts static tool metadata plus resolved invocation arguments into a
    /// compact effect model. Domain packs may provide a richer static profile;
    /// this resolver supplies a conservative fallback for every tool.
    /// </summary>
    public sealed class DefaultInvocationEffectResolver : IInvocationEffectResolver
    {
        public InvocationEffect Resolve(
            IToolDescriptor? descriptor,
            ToolCall call,
            DomainContext? domain = null)
        {
            var profile = descriptor is IToolEffectDescriptor effectDescriptor
                ? Clone(effectDescriptor.EffectProfile)
                : DeriveProfile(descriptor);

            string name = descriptor?.Name ?? call.Name;
            string lowerName = name.ToLowerInvariant();
            var target = ApprovalProtocol.BuildTarget(
                call.Arguments,
                descriptor);
            bool hasWhere = !string.IsNullOrWhiteSpace(target.WhereClause);
            bool hasSelection = domain?.Selection.Count > 0
                && (string.IsNullOrWhiteSpace(target.Layer)
                    || string.IsNullOrWhiteSpace(domain.Selection.LayerName)
                    || target.Layer.Equals(domain.Selection.LayerName, StringComparison.OrdinalIgnoreCase));

            var scope = profile.Effects.HasFlag(DataEffectKind.CreateDataset)
                && !profile.Effects.HasFlag(DataEffectKind.UpdateRows)
                    ? EffectTargetScope.OutputOnly
                    : hasWhere
                        ? EffectTargetScope.Filtered
                        : hasSelection
                            ? EffectTargetScope.Selection
                            : HasExplicitObjectSet(call.Arguments)
                                ? EffectTargetScope.ExplicitObjectSet
                                : EffectTargetScope.EntireDataset;

            if (LooksLikeGeometryMutation(lowerName))
                profile.Effects |= DataEffectKind.ModifyGeometry;
            if (LooksLikeProjectionMutation(lowerName))
                profile.Effects |= DataEffectKind.ModifySpatialReference;
            if (LooksLikeExternalPublish(lowerName))
                profile.Effects |= DataEffectKind.ExternalPublish;
            if (RequestsOverwrite(call.Arguments))
                profile.Effects |= DataEffectKind.OverwriteOutput;

            int? count = scope == EffectTargetScope.Selection
                ? domain?.Selection.Count
                : TryReadExplicitCount(call.Arguments);

            return new InvocationEffect
            {
                ToolName = name,
                Profile = profile,
                Scope = scope,
                Target = target,
                EstimatedObjectCount = count,
                TargetDatasetCount = EstimateTargetDatasetCount(call.Arguments),
                OutputExists = profile.Effects.HasFlag(DataEffectKind.OverwriteOutput),
                UsesSelection = scope == EffectTargetScope.Selection,
                UsesWhereClause = scope == EffectTargetScope.Filtered,
                Summary = BuildSummary(profile, scope, count),
                Confidence = descriptor == null
                    ? EvidenceConfidence.Low
                    : string.IsNullOrWhiteSpace(target.Dataset)
                      && string.IsNullOrWhiteSpace(target.Layer)
                      && !profile.Effects.HasFlag(DataEffectKind.CreateDataset)
                        ? EvidenceConfidence.Medium
                        : EvidenceConfidence.High,
                EvidenceSource = descriptor == null
                    ? "conservative-default"
                    : "tool-profile+resolved-invocation",
                DesiredTargetState = ResolveDesiredTargetState(descriptor?.Verification)
            };
        }

        private static TargetStateExpectation ResolveDesiredTargetState(
            VerificationRule? verification)
        {
            if (verification == null || string.IsNullOrWhiteSpace(verification.Type))
                return TargetStateExpectation.Unknown;

            string type = verification.Type.Trim().ToLowerInvariant();
            bool existenceRule = type.Contains("exists", StringComparison.Ordinal)
                || type.Contains("field", StringComparison.Ordinal);
            if (verification.Invert && existenceRule)
                return TargetStateExpectation.Absent;
            if (type.Contains("populated", StringComparison.Ordinal))
                return TargetStateExpectation.Populated;
            if (existenceRule)
                return TargetStateExpectation.Present;
            return TargetStateExpectation.Unknown;
        }

        public static EffectProfile DeriveProfile(IToolDescriptor? descriptor)
        {
            ToolRiskLevel risk = ToolSafetySemantics.ResolveRisk(descriptor);
            ToolSafetyProfile? safety = ToolSafetySemantics.GetProfile(descriptor);
            string name = descriptor?.Name ?? "unknown";
            string lowerName = name.ToLowerInvariant();

            DataEffectKind effects = DataEffectKind.ReadData;
            ReversibilityKind reversibility = ReversibilityKind.NaturallyReversible;

            if (risk == ToolRiskLevel.Increment)
                effects |= DataEffectKind.CreateDataset;
            else if (risk == ToolRiskLevel.Modify)
                effects |= DataEffectKind.UpdateRows;
            else if (risk == ToolRiskLevel.Destructive)
            {
                reversibility = ReversibilityKind.BackupRequired;
                effects |= InferDestructiveEffect(lowerName);
            }

            if (safety?.Capabilities.HasFlag(ToolSafetyCapability.CreatesOutput) == true)
                effects |= DataEffectKind.CreateDataset;
            if (safety?.Capabilities.HasFlag(ToolSafetyCapability.MutatesExistingData) == true)
                effects |= DataEffectKind.UpdateRows;
            if (safety?.Capabilities.HasFlag(ToolSafetyCapability.ChangesSchema) == true)
                effects |= DataEffectKind.AlterSchema;
            if (safety?.Capabilities.HasFlag(ToolSafetyCapability.DeletesData) == true
                && !effects.HasFlag(DataEffectKind.DeleteRows)
                && !effects.HasFlag(DataEffectKind.DeleteDataset))
                effects |= InferDestructiveEffect(lowerName);

            return new EffectProfile
            {
                Id = BuildProfileId(effects),
                Effects = effects,
                Reversibility = reversibility,
                RequiresInfluenceProbe =
                    safety?.RequiresReliableScope == true
                    || effects.HasFlag(DataEffectKind.UpdateRows)
                    || effects.HasFlag(DataEffectKind.DeleteRows),
                RequiresBackup = risk == ToolRiskLevel.Destructive
                    || safety?.NeedsBackup == true,
                SingleWriterRequired = risk != ToolRiskLevel.ReadOnly,
                ProbeKind = effects.HasFlag(DataEffectKind.AlterSchema)
                    ? "schema"
                    : effects.HasFlag(DataEffectKind.UpdateRows)
                      || effects.HasFlag(DataEffectKind.DeleteRows)
                        ? "row-scope"
                        : effects.HasFlag(DataEffectKind.CreateDataset)
                            ? "output-collision"
                            : "none",
                ApprovalGroupKey = BuildProfileId(effects),
                Source = descriptor == null ? "conservative-default" : "descriptor-derived",
                Confidence = descriptor == null ? EvidenceConfidence.Low : EvidenceConfidence.Medium
            };
        }

        private static DataEffectKind InferDestructiveEffect(string lowerName)
        {
            if (lowerName.Contains("deletefield") || lowerName.Contains("alterfield"))
                return DataEffectKind.AlterSchema;
            if (lowerName.Contains("truncate")
                || lowerName.Contains("deleterow")
                || lowerName.Contains("deletefeature"))
                return DataEffectKind.DeleteRows;
            return DataEffectKind.DeleteDataset;
        }

        private static string BuildProfileId(DataEffectKind effects)
        {
            if (effects.HasFlag(DataEffectKind.DeleteDataset)) return "delete-dataset";
            if (effects.HasFlag(DataEffectKind.DeleteRows)) return "delete-rows";
            if (effects.HasFlag(DataEffectKind.AlterSchema)) return "alter-schema";
            if (effects.HasFlag(DataEffectKind.UpdateRows)) return "update-rows";
            if (effects.HasFlag(DataEffectKind.CreateDataset)) return "create-dataset";
            return "read-data";
        }

        private static EffectProfile Clone(EffectProfile source) => new()
        {
            Id = source.Id,
            Version = source.Version,
            Effects = source.Effects,
            Reversibility = source.Reversibility,
            RequiresInfluenceProbe = source.RequiresInfluenceProbe,
            RequiresBackup = source.RequiresBackup,
            SingleWriterRequired = source.SingleWriterRequired,
            ProbeKind = source.ProbeKind,
            ApprovalGroupKey = source.ApprovalGroupKey,
            Source = source.Source,
            Confidence = source.Confidence
        };

        private static bool LooksLikeGeometryMutation(string name) =>
            name.Contains("repairgeometry") || name.Contains("simplify")
            || name.Contains("densify") || name.Contains("snap");

        private static bool LooksLikeProjectionMutation(string name) =>
            name.Contains("defineprojection") || name.Contains("project");

        private static bool LooksLikeExternalPublish(string name) =>
            name.Contains("publish") || name.Contains("share") || name.Contains("upload");

        private static bool HasExplicitObjectSet(IReadOnlyDictionary<string, object?> args) =>
            args.Keys.Any(k => k.Contains("objectid", StringComparison.OrdinalIgnoreCase)
                || k.Equals("oids", StringComparison.OrdinalIgnoreCase));

        private static bool RequestsOverwrite(IReadOnlyDictionary<string, object?> args)
        {
            foreach (var kv in args)
            {
                if (!kv.Key.Contains("overwrite", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (bool.TryParse(kv.Value?.ToString(), out bool value) && value)
                    return true;
            }
            return false;
        }

        private static int? TryReadExplicitCount(IReadOnlyDictionary<string, object?> args)
        {
            foreach (var kv in args)
            {
                if (!kv.Key.Contains("objectid", StringComparison.OrdinalIgnoreCase)
                    && !kv.Key.Equals("oids", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (kv.Value is System.Collections.IEnumerable sequence && kv.Value is not string)
                    return sequence.Cast<object?>().Count();
                string value = kv.Value?.ToString() ?? "";
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Length;
            }
            return null;
        }

        private static int EstimateTargetDatasetCount(IReadOnlyDictionary<string, object?> args)
        {
            foreach (var kv in args)
            {
                if (!kv.Key.Contains("input", StringComparison.OrdinalIgnoreCase)
                    && !kv.Key.StartsWith("in_", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (kv.Value is System.Collections.IEnumerable sequence && kv.Value is not string)
                    return Math.Max(1, sequence.Cast<object?>().Count());
            }
            return 1;
        }

        private static string BuildSummary(
            EffectProfile profile,
            EffectTargetScope scope,
            int? count)
        {
            string effect = profile.Id switch
            {
                "delete-dataset" => "删除数据集",
                "delete-rows" => "删除记录",
                "alter-schema" => "修改数据结构",
                "update-rows" => "更新记录",
                "create-dataset" => "创建数据集",
                _ => "读取数据"
            };
            string scopeText = scope switch
            {
                EffectTargetScope.Selection => "当前选择集",
                EffectTargetScope.Filtered => "筛选范围",
                EffectTargetScope.EntireDataset => "整个数据集",
                EffectTargetScope.OutputOnly => "新输出",
                EffectTargetScope.ExplicitObjectSet => "指定对象集合",
                _ => "范围未确定"
            };
            return count.HasValue
                ? $"{effect}；范围：{scopeText}；估计对象数：{count.Value}"
                : $"{effect}；范围：{scopeText}";
        }
    }

    /// <summary>
    /// Enforces the extension authority boundary: a domain pack may add
    /// effects and stronger safeguards, but it cannot erase effects, backup
    /// requirements, single-writer constraints, or reversibility risk.
    /// </summary>
    public static class EffectProfilePolicy
    {
        public static EffectProfile MergeIncreaseOnly(
            EffectProfile baseline,
            EffectProfile extension)
        {
            ArgumentNullException.ThrowIfNull(baseline);
            ArgumentNullException.ThrowIfNull(extension);
            bool extensionCoversBaseline =
                (extension.Effects & baseline.Effects) == baseline.Effects;
            string profileId = extensionCoversBaseline
                && !string.IsNullOrWhiteSpace(extension.Id)
                && !extension.Id.Equals("unknown", StringComparison.OrdinalIgnoreCase)
                    ? extension.Id
                    : baseline.Id;

            return new EffectProfile
            {
                Id = profileId,
                Version = extensionCoversBaseline
                    ? ChooseSpecific(baseline.Version, extension.Version)
                    : baseline.Version,
                Effects = baseline.Effects | extension.Effects,
                Reversibility = (ReversibilityKind)Math.Max(
                    (int)baseline.Reversibility,
                    (int)extension.Reversibility),
                RequiresInfluenceProbe =
                    baseline.RequiresInfluenceProbe
                    || extension.RequiresInfluenceProbe,
                RequiresBackup =
                    baseline.RequiresBackup
                    || extension.RequiresBackup,
                SingleWriterRequired =
                    baseline.SingleWriterRequired
                    || extension.SingleWriterRequired,
                ProbeKind = ChooseSpecific(
                    baseline.ProbeKind,
                    extension.ProbeKind),
                ApprovalGroupKey = extensionCoversBaseline
                    ? ChooseSpecific(
                        baseline.ApprovalGroupKey,
                        extension.ApprovalGroupKey)
                    : baseline.ApprovalGroupKey,
                Source = string.Join(
                    "+",
                    new[] { baseline.Source, extension.Source }
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Distinct(StringComparer.OrdinalIgnoreCase)),
                Confidence = (EvidenceConfidence)Math.Max(
                    (int)baseline.Confidence,
                    (int)extension.Confidence)
            };
        }

        private static string ChooseSpecific(string baseline, string extension) =>
            string.IsNullOrWhiteSpace(extension)
            || extension.Equals("none", StringComparison.OrdinalIgnoreCase)
            || extension.Equals("unknown", StringComparison.OrdinalIgnoreCase)
                ? baseline
                : extension;
    }
}
