using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using SpatialAgent.Runtime.Contracts;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    /// <summary>
    /// Architecture guard: turns the documented layering contract into
    /// executable assertions. The core invariant of this codebase is that the
    /// Runtime layer stays host-agnostic — zero ArcGIS references, zero LLM
    /// references, and among project assemblies it may depend on Contracts and Core only.
    ///
    /// ADR 0056 introduced the Core layer between Contracts and Runtime for
    /// pure-logic shared infrastructure. Runtime is allowed and expected to
    /// reference Core.
    ///
    /// GetReferencedAssemblies() only verifies compiled assembly references.
    /// It is strong enough for project/host/UI dependency direction, but not
    /// for stray source-level using statements that never make it into IL.
    /// A companion source scan below covers that lighter-weight code-level guard.
    /// </summary>
    /// <remarks>中文：架构守护：将文档中的分层契约转换为可执行的断言。该代码库的核心不变量是运行时层保持主机无关——零ArcGIS引用，零LLM引用，在项目装配中可能仅依赖合同和核心。ADR 0056引入了合同与运行时之间的核心层，用于纯逻辑共享基础设施。允许且期望运行时引用 Core。GetReferencedAssemblies（） 仅验证已编译的汇编引用。它足够强大，适合项目/主机/界面依赖方向，但对于那些从未进入IL的零散源码级语句就不够。下面的配套源扫描涵盖了那个较轻的代码级守卫。</remarks>
    public class ArchitectureGuardTests
    {
        private static string[] ReferencedNames(Assembly assembly) =>
            assembly.GetReferencedAssemblies()
                .Select(a => a.Name ?? "")
                .ToArray();

        [Fact]
        public void Runtime_depends_only_on_contracts_and_core_among_project_assemblies()
        {
            var projectRefs = ReferencedNames(typeof(AgentRuntime).Assembly)
                .Where(n => n.StartsWith("SpatialAgent", StringComparison.OrdinalIgnoreCase));

            projectRefs.Should().OnlyContain(
                n => n == "SpatialAgent.Runtime.Contracts" || n == "SpatialAgent.Core",
                "Runtime 层只允许依赖 Contracts 和 Core——能力层类型（Tools/Services/Adapters/Knowledge）不得进入 Runtime（ADR 0056）");
        }

        [Fact]
        public void Runtime_has_no_arcgis_or_ui_assembly_references()
        {
            var refs = ReferencedNames(typeof(AgentRuntime).Assembly);

            refs.Should().NotContain(
                n => n.StartsWith("ArcGIS", StringComparison.OrdinalIgnoreCase) ||
                     n.StartsWith("Esri", StringComparison.OrdinalIgnoreCase),
                "Runtime 层零 ArcGIS 引用（架构核心约束）");

            refs.Should().NotContain(
                n => n == "PresentationFramework" || n == "PresentationCore" || n == "WindowsBase",
                "Runtime 层不得依赖 WPF——编排逻辑必须可无头运行");
        }

        [Fact]
        public void Contracts_references_no_project_gis_or_ui_assemblies()
        {
            var refs = ReferencedNames(typeof(AgentRunState).Assembly);

            refs.Should().NotContain(
                n => n.StartsWith("SpatialAgent", StringComparison.OrdinalIgnoreCase),
                "Contracts 是最内层，不得引用任何其他项目程序集");

            refs.Should().NotContain(
                n => n.StartsWith("ArcGIS", StringComparison.OrdinalIgnoreCase) ||
                     n.StartsWith("Esri", StringComparison.OrdinalIgnoreCase) ||
                     n == "PresentationFramework" || n == "PresentationCore" || n == "WindowsBase",
                "Contracts 零宿主/零 UI 依赖");
        }

        [Fact]
        public void Runtime_and_contracts_source_do_not_import_forbidden_host_namespaces()
        {
            string root = FindRepoRoot();
            var forbiddenPatterns = new[]
            {
                @"^\s*using\s+ArcGIS(\.|;)",
                @"^\s*using\s+Esri(\.|;)",
                @"^\s*using\s+System\.Windows(\.|;)",
                @"^\s*using\s+Microsoft\.AspNetCore(\.|;)"
            };

            var files = Directory
                .EnumerateFiles(Path.Combine(root, "src", "runtime"), "*.cs", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(Path.Combine(root, "src", "contracts"), "*.cs", SearchOption.AllDirectories))
                .Concat(Directory.EnumerateFiles(Path.Combine(root, "src", "core"), "*.cs", SearchOption.AllDirectories));

            var violations = new List<string>();
            foreach (var file in files)
            {
                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (forbiddenPatterns.Any(pattern => Regex.IsMatch(lines[i], pattern)))
                        violations.Add($"{Path.GetRelativePath(root, file)}:{i + 1}: {lines[i].Trim()}");
                }
            }

            violations.Should().BeEmpty("Runtime/Contracts/Core 源码层也不应直接导入宿主/UI/Web 命名空间");
        }

        [Fact]
        public void Core_references_no_host_or_ui_assemblies()
        {
            var coreAssembly = typeof(SpatialAgent.Core.ToolNameResolver).Assembly;
            var refs = ReferencedNames(coreAssembly);

            refs.Should().NotContain(
                n => n.StartsWith("ArcGIS", StringComparison.OrdinalIgnoreCase) ||
                     n.StartsWith("Esri", StringComparison.OrdinalIgnoreCase),
                "Core 层零 ArcGIS/Esri 引用（ADR 0056）");

            refs.Should().NotContain(
                n => n == "PresentationFramework" || n == "PresentationCore" || n == "WindowsBase",
                "Core 层不得依赖 WPF");

            refs.Should().NotContain(
                n => n.StartsWith("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase),
                "Core 层不得依赖 ASP.NET Core（ADR 0056：零 HTTP/宿主依赖）");
        }

        /// <summary>
        /// ADR 0056: Core 零宿主依赖——只能引用 Contracts 和 .NET 基础库。
        /// </summary>
        /// <remarks>English：ADR 0056: Core Zero Host Dependency — can only reference Contracts and .NET base libraries.</remarks>
        [Fact]
        public void Core_depends_only_on_contracts_among_project_assemblies()
        {
            var projectRefs = ReferencedNames(typeof(SpatialAgent.Core.ToolNameResolver).Assembly)
                .Where(n => n.StartsWith("SpatialAgent", StringComparison.OrdinalIgnoreCase));

            projectRefs.Should().OnlyContain(
                n => n == "SpatialAgent.Runtime.Contracts",
                "Core 层只允许依赖 Contracts——不得引用 Runtime/Services/Tools/Adapters/Knowledge（ADR 0056）");
        }

        // ════════════════════════════════════════════════════════════════
        // 5A（Phase 5）：外层不引用 Runtime——除白名单外
        // ════════════════════════════════════════════════════════════════
        // English：5A (Phase 5): Outer layers do not reference Runtime—except for the whitelist

        /// <summary>
        /// Phase 5A 白名单：因结构性依赖（ToolRegistry/EventBus/Observation）仍需保留
        /// Runtime 引用的项目。标注了预期解耦阶段。
        /// </summary>
        /// <remarks>English：Phase 5A Whitelist: Projects that still need to retain runtime references due to structural dependencies (ToolRegistry/EventBus/Observation). The expected decoupling stage is annotated.</remarks>
        private static readonly Dictionary<string, string> RuntimeReferenceWhitelist = new()
        {
            // Services 引用 Runtime 因 SafetyGate→ToolRegistry, TranscriptWriter→EventBus,
            // DynamicToolLoader→ToolRegistry, PostConditionValidationRule→Observation
            // 中文：Services 引用 Runtime 因 SafetyGate→ToolRegistry， TranscriptWriter→EventBus， DynamicToolLoader→ToolRegistry， PostConditionValidationRule→Observation
            ["SpatialAgent.Services"] = "Phase 4 ToolRegistry/事件接口继续下沉后移除（预计）"
        };

        /// <summary>
        /// 5A-3：Services/Tools/Adapters/Knowledge 不得引用 Runtime，除白名单外。
        /// </summary>
        /// <remarks>中文：5A-3：Services/Tools/Adapters/Knowledge 不得引用 Runtime，除白名单外。</remarks>
        [Fact]
        public void Capability_layers_do_not_reference_runtime_except_whitelist()
        {
            string root = FindRepoRoot();
            var srcDir = Path.Combine(root, "src");
            var capabilityDirs = new[] { "services", "tools", "adapters", "knowledge" };
            var errors = new List<string>();

            foreach (var dir in capabilityDirs)
            {
                var subDirs = Directory.Exists(Path.Combine(srcDir, dir))
                    ? Directory.GetDirectories(Path.Combine(srcDir, dir))
                    : new[] { Path.Combine(srcDir, dir) };

                foreach (var projDir in subDirs)
                {
                    var csprojFiles = Directory.GetFiles(projDir, "*.csproj");
                    foreach (var csproj in csprojFiles)
                    {
                        string content = File.ReadAllText(csproj);
                        if (content.Contains("SpatialAgent.Runtime.csproj"))
                        {
                            string projName = Path.GetFileNameWithoutExtension(csproj);
                            if (!RuntimeReferenceWhitelist.ContainsKey(projName))
                            {
                                errors.Add($"{projName} → Runtime reference NOT in whitelist");
                            }
                        }
                    }
                }
            }

            errors.Should().BeEmpty(
                "外层能力层不得引用 Runtime——除白名单中的结构性依赖外（ADR 0056 Phase 5A）");
        }

        /// <summary>
        /// 5A-4：白名单非空时验证——记录哪些项目仍依赖 Runtime 及其原因。
        /// 白名单为空时（即全部脱钩）此测试仍通过——意味着目标达成。
        /// </summary>
        /// <remarks>English：5A-4: Whitelist Non-Empty Time Validation—Records which projects still rely on Runtime and why. If the whitelist is empty (i.e., all decoupling), this test still passes—meaning the goal is achieved.</remarks>
        [Fact]
        public void Runtime_whitelist_items_have_documented_reason()
        {
            RuntimeReferenceWhitelist.Should().NotContain(
                kv => string.IsNullOrWhiteSpace(kv.Value),
                "白名单中每个条目都应有记录的保留原因和预期拆除时间");
        }

        // ════════════════════════════════════════════════════════════════
        // 5A-1/5A-2：Runtime 和 Core 的依赖方向已在 Phase 1 覆盖
        // ════════════════════════════════════════════════════════════════
        // Runtime_depends_only_on_contracts_and_core_among_project_assemblies ✅
        // Core_depends_only_on_contracts_among_project_assemblies ✅
        // Core_references_no_host_or_ui_assemblies ✅
        // English：5A-1/5A-2: Runtime and Core dependency directions have been covered by Runtime_depends_only_on_contracts_and_core_among_project_assemblies ✅ Core_depends_only_on_contracts_among_project_assemblies ✅ in Phase 1 Core_references_no_host_or_ui_assemblies ✅

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "SpatialAgent.RuntimeOnly.slnx")))
                    return dir.FullName;

                dir = dir.Parent;
            }

            throw new DirectoryNotFoundException("Unable to locate repository root for architecture source scan.");
        }
    }
}
