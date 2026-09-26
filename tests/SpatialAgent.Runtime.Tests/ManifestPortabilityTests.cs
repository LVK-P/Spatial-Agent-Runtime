using FluentAssertions;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    public sealed class ManifestPortabilityTests
    {
        [Fact]
        public void Repository_does_not_publish_vendor_generated_metadata()
        {
            string root = FindRepoRoot();
            string[] generatedFiles =
            {
                "tools_manifest.json",
                "tools_index.json",
                "tool_risk_catalog.json"
            };

            generatedFiles
                .Select(file => Path.Combine(root, file))
                .Should()
                .OnlyContain(path => !File.Exists(path),
                    "vendor-derived runtime metadata must be generated locally and stay untracked");

            string generatedDocumentation = Path.Combine(
                root,
                "knowledge",
                "documents",
                "tools");
            string[] generatedPages = Directory.Exists(generatedDocumentation)
                ? Directory.GetFiles(generatedDocumentation, "*.md", SearchOption.AllDirectories)
                : Array.Empty<string>();
            generatedPages.Should().BeEmpty(
                "generated vendor tool documentation must not be distributed");
        }

        private static string FindRepoRoot()
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                if (File.Exists(Path.Combine(current.FullName, "performance_baseline.json")))
                    return current.FullName;
                current = current.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate repository root.");
        }
    }
}
