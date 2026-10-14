using FluentAssertions;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    public class StateReconcilerTests
    {
        private const string CurrentMap = "地图：测试\n· BASE_01：面要素\n· ROAD_02：线要素";

        // ── Truthfulness guards ──
        // 中文：诚实守护者

        [Fact]
        public void Missing_transcript_produces_no_fabricated_layer_claims()
        {
            // Old behavior: pastLayers extraction fails silently → every current
            // layer reported as "新增图层（上次会话时不存在）" — a fabricated claim.
            // 中文：旧行为：pastLayers 提取在所有当前层→无声地失败，报告为“新增图层（上次会话时不存在）”——这是捏造的说法。
            var report = StateReconciler.Reconcile(
                transcriptPath: null, currentMapInfo: CurrentMap, threadId: "t-1");

            report.LayersNew.Should().BeEmpty();
            report.LayersMissing.Should().BeEmpty();
            report.NeedsAttention.Should().BeFalse();
        }

        [Fact]
        public void Empty_current_map_produces_no_missing_claims()
        {
            var report = StateReconciler.Reconcile(
                transcriptPath: null, currentMapInfo: "", threadId: "t-1",
                pastLayers: new[] { "BASE_01" });

            report.LayersMissing.Should().BeEmpty();
            report.NeedsAttention.Should().BeFalse();
        }

        [Fact]
        public void Diffs_layers_when_both_sides_are_known()
        {
            var report = StateReconciler.Reconcile(
                transcriptPath: null, currentMapInfo: CurrentMap, threadId: "t-1",
                pastLayers: new[] { "BASE_01", "DELETED_09" });

            report.LayersStillPresent.Should().ContainSingle().Which.Should().Be("BASE_01");
            report.LayersMissing.Should().ContainSingle().Which.Should().Be("DELETED_09");
            report.LayersNew.Should().ContainSingle().Which.Should().Be("ROAD_02");
            report.NeedsAttention.Should().BeTrue();
        }

        [Fact]
        public void Prompt_injection_omits_tool_chain_line_when_unknown()
        {
            var report = StateReconciler.Reconcile(
                transcriptPath: null, currentMapInfo: "", threadId: "t-1");

            report.ToPromptInjection().Should().NotContain("上次工具链");
        }

        // ── Transcript location + per-session slicing ──
        // Transcript files are transcript_{timestamp}.jsonl, one per window
        // lifetime, holding multiple sessions back to back.
        // 中文：转录地点 + 每次会话切片 转录文件是 transcript_{timestamp}.jsonl，每个窗口寿命一个，连续保存多个会话。

        private static string WriteTranscript(params string[] lines)
        {
            string dir = Directory.CreateDirectory(Path.Combine(
                Path.GetTempPath(), "spatial-agent-reconciler-" + Guid.NewGuid().ToString("N"))).FullName;
            string path = Path.Combine(dir, "transcript_20260718_160000.jsonl");
            File.WriteAllLines(path, lines);
            return path;
        }

        [Fact]
        public void ReadSessionLines_slices_only_the_requested_thread()
        {
            string path = WriteTranscript(
                "{\"type\":\"session_start\",\"session\":\"thread-1\"}",
                "{\"type\":\"context_gathered\",\"turn\":1,\"map\":\"· BASE_01：面要素\"}",
                "{\"type\":\"tool_end\",\"turn\":1,\"tool\":\"management_AddField\"}",
                "{\"type\":\"session_start\",\"session\":\"thread-2\"}",
                "{\"type\":\"context_gathered\",\"turn\":1,\"map\":\"· OTHER_99：点要素\"}");

            var lines = StateReconciler.ReadSessionLines(path, "thread-1");

            var layers = StateReconciler.ExtractReferencedLayers(lines);
            layers.Should().ContainSingle().Which.Should().Be("BASE_01");

            var tools = StateReconciler.ExtractToolChain(lines);
            tools.Should().ContainSingle().Which.Should().Be("management_AddField");
        }

        [Fact]
        public void FindTranscriptForThread_locates_the_file_containing_the_session()
        {
            string path = WriteTranscript(
                "{\"type\":\"session_start\",\"session\":\"pro-20260718-161432-036\"}",
                "{\"type\":\"user_msg\",\"turn\":1,\"content\":\"hi\"}");
            string logDir = Path.GetDirectoryName(path)!;

            StateReconciler.FindTranscriptForThread(logDir, "pro-20260718-161432-036")
                .Should().Be(path);
            StateReconciler.FindTranscriptForThread(logDir, "pro-unknown")
                .Should().BeNull();
        }

        [Fact]
        public void Reconcile_uses_sliced_transcript_for_layer_diff()
        {
            // thread-1 used BASE_01; thread-2's OTHER_99 must not leak into the diff
            // 中文：thread-1 使用的是BASE_01;螺纹2的OTHER_99不能漏到差速器里
            string path = WriteTranscript(
                "{\"type\":\"session_start\",\"session\":\"thread-1\"}",
                "{\"type\":\"context_gathered\",\"turn\":1,\"map\":\"· BASE_01：面要素\"}",
                "{\"type\":\"session_start\",\"session\":\"thread-2\"}",
                "{\"type\":\"context_gathered\",\"turn\":1,\"map\":\"· OTHER_99：点要素\"}");

            var report = StateReconciler.Reconcile(path, CurrentMap, "thread-1");

            report.LayersStillPresent.Should().ContainSingle().Which.Should().Be("BASE_01");
            report.LayersMissing.Should().BeEmpty();           // OTHER_99 不归属于 thread-1。 / OTHER_99 is not attributed to thread-1.
            report.LayersNew.Should().ContainSingle().Which.Should().Be("ROAD_02");
        }
    }
}
