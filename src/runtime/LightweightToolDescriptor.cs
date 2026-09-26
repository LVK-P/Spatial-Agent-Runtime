using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Runtime
{
    /// <summary>
    /// Wraps any IToolDescriptor and strips parameter descriptions, enum values,
    /// and titles down to name+type+required only. Used for Tier-2 (context
    /// top-N) tools — the model sees what parameters a tool takes without the
    /// full descriptive payload that dominates per-turn token consumption.
    ///
    /// Tier-1 (always-on + skill-declared) tools are NOT wrapped — they keep
    /// full parameter detail so the model can construct correct calls without
    /// needing a second lookup step.
    /// </summary>
    internal sealed class LightweightToolDescriptor : IToolDescriptor
    {
        private readonly IToolDescriptor _inner;

        public LightweightToolDescriptor(IToolDescriptor inner) => _inner = inner;

        public string Name => _inner.Name;
        public string Description => _inner.Description;   // summary (首句), no change needed
        public string Category => _inner.Category;
        public ToolAnnotation Annotation => _inner.Annotation;
        public IReadOnlyList<string> RequiredParameters => _inner.RequiredParameters;
        public VerificationRule? Verification => _inner.Verification;
        public IReadOnlyDictionary<string, string>? DefaultParameters => _inner.DefaultParameters;

        /// <summary>
        /// Stripped parameters: only name + type + required flag.
        /// Per-parameter descriptions, enums, titles, and default values are
        /// dropped — they are the dominant cost in tool-list token load.
        /// </summary>
        public IReadOnlyDictionary<string, ParameterProperty> Parameters
        {
            get
            {
                var stripped = new Dictionary<string, ParameterProperty>();
                foreach (var kv in _inner.Parameters)
                {
                    stripped[kv.Key] = new ParameterProperty
                    {
                        Type = kv.Value.Type,
                        // Description, Enum, DefaultValue intentionally null/empty
                    };
                }
                return stripped;
            }
        }

        public override string ToString() => $"{Name} (lightweight)";
    }
}
