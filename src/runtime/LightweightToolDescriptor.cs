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
    /// <remarks>中文：封装任意 IToolDescriptor，并把参数描述、枚举值和标题缩减为仅含 name、type、required 的模式。用于二级（上下文 Top-N）工具，使模型能看到工具参数，而无需承担占据每轮大量令牌的完整描述。一级（常驻 + 技能声明）工具不封装，保留完整参数细节，使模型无需二次查找即可构造正确调用。</remarks>
    internal sealed class LightweightToolDescriptor : IToolDescriptor
    {
        private readonly IToolDescriptor _inner;

        public LightweightToolDescriptor(IToolDescriptor inner) => _inner = inner;

        public string Name => _inner.Name;
        public string Description => _inner.Description;   // 摘要（首句），无需更改。 / Summary (first sentence); no change needed.
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
        /// <remarks>中文：简化参数：仅限名称 + 类型 + 必要标志。按参数描述、枚举、标题和默认值被剔除——它们是工具列表令牌负载的主要成本。</remarks>
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
                        // 中文：描述、枚举、默认值故意为空/空
                    };
                }
                return stripped;
            }
        }

        public override string ToString() => $"{Name} (lightweight)";
    }
}
