using FluentAssertions;
using NSubstitute;
using SpatialAgent.Runtime.Contracts;
using SpatialAgent.Services;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    public class LoggingMigrationTests
    {
        [Fact]
        public void Ledger_migration_moves_the_active_stream_instead_of_silently_ignoring_it()
        {
            string root = Path.Combine(Path.GetTempPath(), "ledger_migrate_" + Guid.NewGuid().ToString("N"));
            string source = Path.Combine(root, "fallback");
            string target = Path.Combine(root, "project");
            try
            {
                string oldPath;
                string newPath;
                using (var ledger = new VerificationLedger())
                {
                    ledger.Initialize(source);
                    oldPath = ledger.FilePath!;
                    ledger.MigrateTo(target);
                    newPath = ledger.FilePath!;
                }
                newPath.Should().StartWith(target);
                File.Exists(newPath).Should().BeTrue();
                File.Exists(oldPath).Should().BeFalse();
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task Snapshot_uses_typed_map_contract()
        {
            string root = Path.Combine(Path.GetTempPath(), "snapshot_typed_" + Guid.NewGuid().ToString("N"));
            var map = Substitute.For<IMapReader>();
            map.GetMapContextAsync().Returns(new MapContextSnapshot
            {
                IsAvailable = true,
                MapName = "Main",
                Wkid = 4490,
                Layers = new[]
                {
                    new LayerInfo
                    {
                        Name = "roads",
                        GeometryType = "Polyline",
                        FeatureCount = 7,
                        Fields = new List<string> { "OBJECTID", "length" }
                    }
                }
            });
            var project = Substitute.For<IProjectReader>();
            project.GetProjectInfoAsync().Returns("{}");
            try
            {
                string snapshotPath;
                using (var snapshotter = new ContextSnapshotter(map, project))
                {
                    snapshotter.Initialize(root);
                    await snapshotter.SnapshotAsync("test", 1);
                    snapshotter.Flush();
                    snapshotPath = snapshotter.FilePath!;
                }
                string json = File.ReadAllText(snapshotPath);

                json.Should().Contain("\"map\":\"Main\"");
                json.Should().Contain("\"crs\":\"4490\"");
                json.Should().Contain("\"type\":\"Polyline\"");
                json.Should().Contain("\"count\":7");
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        }
    }
}
