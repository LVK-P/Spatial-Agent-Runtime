using FluentAssertions;
using SpatialAgent.Runtime.Contracts;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    public class ResourcePathsTests
    {
        [Fact]
        public void Find_returns_null_for_missing_resource()
        {
            ResourcePaths.Find("definitely-not-a-real-resource-9f8e7d.json").Should().BeNull();
        }

        [Fact]
        public void Find_locates_file_next_to_the_contracts_assembly()
        {
            // In the Pro add-in the resources sit next to the assemblies in
            // AssemblyCache; in tests the bin dir plays that role.
            string root = ResourcePaths.PrimaryRoot();
            string name = $"resourcepaths-probe-{Guid.NewGuid():N}.txt";
            string path = Path.Combine(root, name);
            File.WriteAllText(path, "probe");

            try
            {
                ResourcePaths.Find(name).Should().Be(path);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Resolve_anchors_missing_paths_to_primary_root()
        {
            string resolved = ResourcePaths.Resolve("not-yet-created-dir");
            resolved.Should().StartWith(ResourcePaths.PrimaryRoot());
        }

        [Fact]
        public void Primary_root_is_the_assembly_directory_not_an_arbitrary_base()
        {
            string expected = Path.GetDirectoryName(typeof(ResourcePaths).Assembly.Location)!;
            ResourcePaths.PrimaryRoot().Should().Be(expected);
        }
    }
}
