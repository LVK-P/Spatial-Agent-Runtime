using FluentAssertions;
using Newtonsoft.Json;
using SpatialAgent.Runtime.Contracts;
using SpatialAgent.Services;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    public sealed class ProjectMemoryStoreTests
    {
        [Fact]
        public void Progress_snapshots_replace_previous_progress_for_same_source()
        {
            string root = CreateTempRoot();
            try
            {
                var store = new ProjectMemoryStore(root);
                store.RecordSessionSummary(new SessionSummary
                {
                    Date = "2026-07-26 10:00",
                    User = "ArcGISPro",
                    WhatWasDone = "进行中：1 轮交互，1 次工具调用"
                });
                store.RecordSessionSummary(new SessionSummary
                {
                    Date = "2026-07-26 10:03",
                    User = "ArcGISPro",
                    WhatWasDone = "进行中：2 轮交互，6 次工具调用"
                });

                string summary = store.GetSummary();
                summary.Should().Contain("进行中：2 轮交互，6 次工具调用");
                summary.Should().NotContain("进行中：1 轮交互，1 次工具调用");
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void Legacy_mojibake_progress_is_repaired_and_collapsed_on_load()
        {
            string root = CreateTempRoot();
            try
            {
                string folder = Path.Combine(root, ".agent");
                Directory.CreateDirectory(folder);
                var memory = new ProjectMemory
                {
                    SessionSummaries = new()
                    {
                        new SessionSummary
                        {
                            Date = "2026-07-26 10:00",
                            User = "ArcGISPro",
                            WhatWasDone = "乱码前缀1 乱码轮次，2 次工具调用"
                        },
                        new SessionSummary
                        {
                            Date = "2026-07-26 10:01",
                            User = "ArcGISPro",
                            WhatWasDone = "乱码前缀3 乱码轮次，7 次工具调用"
                        }
                    }
                };
                File.WriteAllText(
                    Path.Combine(folder, "memory.json"),
                    JsonConvert.SerializeObject(memory));

                var store = new ProjectMemoryStore(root);
                string summary = store.GetSummary();

                summary.Should().Contain("进行中：3 轮交互，7 次工具调用");
                summary.Should().NotContain("乱码");
                summary.Split("进行中：").Should().HaveCount(2);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        private static string CreateTempRoot()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "spatial-agent-memory-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }
    }
}
