using FluentAssertions;
using Newtonsoft.Json;
using SpatialAgent.Runtime.Contracts;
using SpatialAgent.Services;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    public class SessionIndexTests
    {
        private static string CreateTempProject()
            => Directory.CreateDirectory(Path.Combine(
                Path.GetTempPath(), "spatial-agent-sessionindex-" + Guid.NewGuid().ToString("N"))).FullName;

        private static void WriteCheckpoint(string projectFolder, string threadId, AgentRunState state)
        {
            string dir = Path.Combine(projectFolder, ".agent", "checkpoints", threadId);
            Directory.CreateDirectory(dir);
            File.WriteAllText(
                Path.Combine(dir, $"turn_{state.Turn:0000}_round_{state.Round:00}.json"),
                JsonConvert.SerializeObject(state));
        }

        [Fact]
        public void Load_skips_latest_json_pointer_and_reads_real_checkpoint()
        {
            string root = CreateTempProject();
            var state = new AgentRunState
            {
                ThreadId = "thread-a",
                Turn = 2,
                Round = 1,
                UserMessage = "最后一条消息",
                SavedAt = new DateTime(2026, 7, 18, 8, 0, 0, DateTimeKind.Utc),
                Messages = new List<Message>
                {
                    new() { Role = "user", Content = "计算各地块面积" },
                    new()
                    {
                        Role = "assistant",
                        Content = "",
                        ToolCalls = new List<ToolCall>
                        {
                            new() { Name = "management_AddField" },
                            new() { Name = "management_CalculateField" }
                        }
                    },
                    new() { Role = "assistant", Content = "已完成" }
                }
            };
            WriteCheckpoint(root, "thread-a", state);

            // latest.json pointer: same turn/round as the real checkpoint but hollow.
            // Enumerated first alphabetically — the old "*.json" scan let it win the
            // tie and blank out Summary/tool info.
            // 中文：latest.json 指针与真实检查点具有相同轮次，但内容为空。按字母顺序它排在前面；旧式“*.json”扫描会在并列时误选它，并显示错误的摘要/工具信息。
            string dir = Path.Combine(root, ".agent", "checkpoints", "thread-a");
            File.WriteAllText(Path.Combine(dir, "latest.json"), JsonConvert.SerializeObject(new
            {
                threadId = "thread-a",
                turn = 2,
                round = 1,
                path = "x",
                savedAt = state.SavedAt
            }));

            var index = new SessionIndex(root);
            index.Load();

            var sessions = index.ListSessions();
            sessions.Should().HaveCount(1);
            var info = sessions[0];
            info.Summary.Should().Be("计算各地块面积");          // 第一条用户消息，非空。 / First user message, not blank.
            info.ToolCount.Should().Be(2);                        // 实际调用的工具数。 / Number of tools actually called.
            info.ToolChain.Should().Be("management_AddField → management_CalculateField");
            info.TurnCount.Should().Be(2);
        }

        [Fact]
        public void Sessions_sort_by_saved_at_not_by_date_label_string()
        {
            string root = CreateTempProject();

            // Cross-year boundary: "12-31" > "01-02" as strings, so the old
            // DateLabel string sort would list the OLDER session first.
            // 中文：跨年边界：“12-31”>“01-02”作为字符串，旧的DateLabel字符串排序会优先列出较旧的会话。
            WriteCheckpoint(root, "thread-old", new AgentRunState
            {
                ThreadId = "thread-old",
                Turn = 1,
                SavedAt = new DateTime(2025, 12, 31, 10, 0, 0, DateTimeKind.Utc),
                Messages = new List<Message> { new() { Role = "user", Content = "old" } }
            });
            WriteCheckpoint(root, "thread-new", new AgentRunState
            {
                ThreadId = "thread-new",
                Turn = 1,
                SavedAt = new DateTime(2026, 1, 2, 10, 0, 0, DateTimeKind.Utc),
                Messages = new List<Message> { new() { Role = "user", Content = "new" } }
            });

            var index = new SessionIndex(root);
            index.Load();

            var sessions = index.ListSessions();
            sessions.Should().HaveCount(2);
            sessions[0].ThreadId.Should().Be("thread-new");
            sessions[1].ThreadId.Should().Be("thread-old");
        }

        [Fact]
        public void Directory_without_turn_checkpoints_is_ignored()
        {
            string root = CreateTempProject();
            string dir = Path.Combine(root, ".agent", "checkpoints", "thread-empty");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "latest.json"), "{\"threadId\":\"thread-empty\"}");

            var index = new SessionIndex(root);
            index.Load();

            index.ListSessions().Should().BeEmpty();
        }
    }
}
