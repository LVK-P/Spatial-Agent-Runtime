using SpatialAgent.Core;
using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Runtime.Planning
{
    /// <summary>
    /// Lazy, priority-ordered registry for vertical GIS behavior. A pack contains
    /// metadata and recipes only; it cannot execute ArcGIS operations.
    /// </summary>
    /// <remarks>中文：懒惰的、优先排序的垂直GIS行为注册表。一个包只包含元数据和配方;它无法执行ArcGIS操作。</remarks>
    public sealed class DomainPackRegistry
    {
        public const string RuntimeApiVersion = "3.1";
        private readonly List<IDomainPack> _packs = new();

        public DomainPackRegistry(IEnumerable<IDomainPack>? packs = null)
        {
            foreach (var pack in packs ?? CreateDefaults())
                Register(pack);
        }

        public IReadOnlyList<IDomainPack> Packs => _packs.AsReadOnly();

        public void Register(IDomainPack pack)
        {
            ArgumentNullException.ThrowIfNull(pack);
            ValidateManifest(pack);
            if (_packs.Any(p => p.Id.Equals(pack.Id, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    $"Domain pack '{pack.Id}' is already registered.");
            _packs.Add(pack);
            _packs.Sort((left, right) => right.Priority.CompareTo(left.Priority));
        }

        public IDomainPack Resolve(IToolDescriptor descriptor) =>
            _packs.FirstOrDefault(pack => pack.CanHandle(descriptor))
            ?? _packs.First(pack => pack.Id == "generic");

        public static IReadOnlyList<IDomainPack> CreateDefaults() =>
            new IDomainPack[]
            {
                new KeywordDomainPack(
                    "territorial-planning", "Territorial planning", 100,
                    new[] { "land use", "territorial", "用地", "国土空间" },
                    new[]
                    {
                        "land-code-mapping",
                        "planning-area-calculation",
                        "planning-quality-check"
                    },
                    profileFactory: descriptor =>
                    {
                        var profile = DefaultInvocationEffectResolver.DeriveProfile(descriptor);
                        string name = descriptor.Name.ToLowerInvariant();
                        if (name.Contains("calculate_area")
                            || name.Contains("calculate_length")
                            || name.Contains("map_field_values"))
                        {
                            profile.Id = "planning-field-overwrite";
                            profile.Effects = DataEffectKind.ReadData | DataEffectKind.UpdateRows;
                            profile.RequiresInfluenceProbe = true;
                            profile.ProbeKind = "row-scope";
                            profile.ApprovalGroupKey = "planning-field-overwrite";
                            profile.Confidence = EvidenceConfidence.High;
                        }
                        profile.Source = "domain-pack:territorial-planning";
                        return profile;
                    },
                    matchPredicate: descriptor => IsTerritorialPlanningTool(descriptor.Name)),
                new KeywordDomainPack(
                    "network", "Network analysis", 80,
                    new[] { "network", "route", "location-allocation", "servicearea", "trace" },
                    new[] { "network-route", "network-service-area" },
                    matchPredicate: descriptor => HasPrefix(
                        descriptor.Name,
                        "na_", "un_", "tn_", "nd_")),
                new KeywordDomainPack(
                    "raster", "Raster and imagery", 70,
                    new[] { "raster", "spatial analyst", "image analyst", "mosaic", "pixel" },
                    new[] { "raster-analysis", "raster-export" },
                    matchPredicate: descriptor => HasPrefix(
                        descriptor.Name,
                        "sa_", "ia_", "md_")),
                new KeywordDomainPack(
                    "schema", "Geodatabase schema", 65,
                    new[] { "schema", "domain", "subtype", "relationship class" },
                    new[] { "schema-change", "schema-validation" },
                    profileFactory: descriptor =>
                    {
                        var profile = DefaultInvocationEffectResolver.DeriveProfile(descriptor);
                        if (ToolSafetySemantics.ResolveRisk(descriptor) >= ToolRiskLevel.Modify)
                        {
                            profile.Effects |= DataEffectKind.AlterSchema;
                            profile.Id = "alter-schema";
                            profile.ProbeKind = "schema";
                            profile.RequiresBackup = true;
                            profile.Reversibility = ReversibilityKind.BackupRequired;
                            profile.ApprovalGroupKey = "alter-schema";
                        }
                        return profile;
                    },
                    matchPredicate: descriptor => IsSchemaMutation(descriptor.Name)),
                new KeywordDomainPack(
                    "cartography", "Map and cartography", 55,
                    new[] { "cartography", "symbology", "label", "layout", "map", "layer" },
                    new[] { "map-state-change", "layout-export" },
                    matchPredicate: descriptor => HasPrefix(
                        descriptor.Name,
                        "cartography_")),
                new KeywordDomainPack(
                    "vector", "Vector geoprocessing", 40,
                    new[] { "analysis", "management", "feature", "geometry", "overlay", "proximity" },
                    new[] { "vector-overlay", "vector-cleanup" },
                    matchPredicate: descriptor => HasPrefix(
                        descriptor.Name,
                        "analysis_", "management_")),
                new KeywordDomainPack(
                    "generic", "Generic GIS", int.MinValue,
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    matchesAll: true)
            };

        private static bool IsTerritorialPlanningTool(string name)
        {
            string normalized = name.ToLowerInvariant();
            return normalized is "cc_calculate_area"
                or "cc_calculate_length"
                or "cc_lookup_land_code"
                or "cc_map_field_values"
                or "cc_read_rows";
        }

        private static bool HasPrefix(string name, params string[] prefixes) =>
            prefixes.Any(prefix =>
                name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        private static bool IsSchemaMutation(string name)
        {
            string shortName = ToolNameResolver.ExtractShortName(name);
            string[] operations =
            {
                "AddField", "DeleteField", "AlterField", "CreateDomain",
                "DeleteDomain", "AssignDomain", "RemoveDomain", "AddIndex",
                "RemoveIndex", "CreateRelationship", "DeleteRelationship",
                "SetSubtype", "AddSubtype", "RemoveSubtype"
            };
            return operations.Any(operation =>
                shortName.Contains(operation, StringComparison.OrdinalIgnoreCase));
        }

        private static void ValidateManifest(IDomainPack pack)
        {
            DomainPackManifest manifest = pack.Manifest
                ?? throw new InvalidOperationException(
                    $"Domain pack '{pack.Id}' has no manifest.");
            if (!pack.Id.Equals(manifest.Id, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Domain pack id '{pack.Id}' does not match manifest id '{manifest.Id}'.");
            }
            if (!Version.TryParse(manifest.Version, out _))
            {
                throw new InvalidOperationException(
                    $"Domain pack '{pack.Id}' has invalid version '{manifest.Version}'.");
            }
            if (!Version.TryParse(RuntimeApiVersion, out var runtime)
                || !Version.TryParse(manifest.MinimumRuntimeVersion, out var minimum))
            {
                throw new InvalidOperationException(
                    $"Domain pack '{pack.Id}' has invalid runtime compatibility metadata.");
            }
            if (runtime < minimum)
            {
                throw new InvalidOperationException(
                    $"Domain pack '{pack.Id}' requires runtime {minimum} or later.");
            }
            if (!string.IsNullOrWhiteSpace(manifest.MaximumRuntimeVersion))
            {
                if (!Version.TryParse(manifest.MaximumRuntimeVersion, out var maximum))
                {
                    throw new InvalidOperationException(
                        $"Domain pack '{pack.Id}' has invalid maximum runtime version.");
                }
                if (runtime > maximum)
                {
                    throw new InvalidOperationException(
                        $"Domain pack '{pack.Id}' supports runtime {maximum} or earlier.");
                }
            }
            if (manifest.EffectAuthority != DomainPackEffectAuthority.IncreaseOnly)
            {
                throw new InvalidOperationException(
                    $"Domain pack '{pack.Id}' requests unsupported effect authority.");
            }
        }
    }

    internal sealed class KeywordDomainPack : IDomainPack
    {
        private readonly string[] _keywords;
        private readonly bool _matchesAll;
        private readonly Func<IToolDescriptor, EffectProfile>? _profileFactory;
        private readonly Func<IToolDescriptor, bool>? _matchPredicate;

        public KeywordDomainPack(
            string id,
            string displayName,
            int priority,
            IEnumerable<string> keywords,
            IEnumerable<string> recipeIds,
            Func<IToolDescriptor, EffectProfile>? profileFactory = null,
            bool matchesAll = false,
            Func<IToolDescriptor, bool>? matchPredicate = null)
        {
            Id = id;
            DisplayName = displayName;
            Priority = priority;
            _keywords = keywords.ToArray();
            RecipeIds = recipeIds.ToArray();
            _profileFactory = profileFactory;
            _matchesAll = matchesAll;
            _matchPredicate = matchPredicate;
            Manifest = new DomainPackManifest
            {
                Id = id,
                Version = "1.0.0",
                MinimumRuntimeVersion = DomainPackRegistry.RuntimeApiVersion,
                EffectAuthority = DomainPackEffectAuthority.IncreaseOnly
            };
        }

        public string Id { get; }
        public string DisplayName { get; }
        public int Priority { get; }
        public DomainPackManifest Manifest { get; }
        public IReadOnlyList<string> RecipeIds { get; }

        public bool CanHandle(IToolDescriptor descriptor)
        {
            if (_matchesAll)
                return true;
            if (_matchPredicate?.Invoke(descriptor) == true)
                return true;
            string haystack = $"{descriptor.Name} {descriptor.Category} {descriptor.Description}";
            return _keywords.Any(keyword =>
                haystack.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        public EffectProfile? ResolveEffectProfile(IToolDescriptor descriptor) =>
            _profileFactory?.Invoke(descriptor);
    }
}
