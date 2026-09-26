using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SpatialAgent.Services;
using SpatialAgent.Runtime;
using SpatialAgent.Runtime.Contracts;
using SpatialAgent.Core;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    /// <summary>
    /// ★ Phase 5C：轻量集成测试（不需 ArcGIS Pro）。
    /// 覆盖 Transcript 完整链路 + Checkpoint 保存/恢复。
    /// </summary>
    public class IntegrationTests
    {
        // ════════════════════════════════════════════════════════════════
        // Transcript 完整链路
        // ════════════════════════════════════════════════════════════════

        [Fact]
        public void Transcript_api_produces_valid_event_count()
        {
            var tmpDir = Path.Combine(Path.GetTempPath(), $"transcript_test_{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(tmpDir);
                var transcript = new TranscriptWriter();
                transcript.Initialize(tmpDir);

                // Write session events — verify API doesn't throw
                transcript.WriteSessionStart("test-session-1", "deepseek-v4", "high", null);
                transcript.WriteUserMessage(1, "请统计图层要素数量");
                transcript.WriteContext(1, "TestProject", "MapInfo", 3, 1);
                transcript.WriteLLMRequest(1, "[]", "[]", "abc123");
                transcript.WriteLLMResponse(1, "[]", "已完成统计", 1500, 100, 50);
                transcript.WriteToolStart(1, 1, "analysis_Statistics",
                    new Dictionary<string, object?> { ["in_features"] = "BASE_01" });
                transcript.WriteToolEnd(1, 1, "analysis_Statistics", true,
                    new { featureCount = 42 }, null, 300, 0);
                transcript.WriteVerification(1, 1, "analysis_Statistics",
                    "verify_feature_count", "featureCount", ">0", "42", "pass", null, "");
                transcript.WriteSessionEnd(1, 1, 0, 0, 2000);
                transcript.Flush();

                // API-level verification: event count should reflect written events
                transcript.EventCount.Should().BeGreaterThan(0, "写入事件后计数应 >0");
                transcript.FilePath.Should().NotBeNull("Initialize 后应有文件路径");
                transcript.IsEnabled.Should().BeTrue();
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        [Fact]
        public void Transcript_session_boundary_events_are_recorded()
        {
            var tmpDir = Path.Combine(Path.GetTempPath(), $"transcript_test_{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(tmpDir);
                var transcript = new TranscriptWriter();
                transcript.Initialize(tmpDir);

                var initCount = transcript.EventCount;
                transcript.WriteSessionStart("sid-1", "m1", "high", "/tmp/test");
                transcript.WriteSessionEnd(1, 1, 0, 0, 1000);

                // After session boundary writes, event count should increase
                transcript.EventCount.Should().BeGreaterThan(initCount,
                    "session_start + session_end 应增加事件计数");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // ════════════════════════════════════════════════════════════════
        // Checkpoint 保存/恢复
        // ════════════════════════════════════════════════════════════════

        [Fact]
        public async Task Checkpoint_save_and_restore_preserves_state()
        {
            var tmpDir = Path.Combine(Path.GetTempPath(), $"ckpt_test_{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(tmpDir);
                var store = new FileRunCheckpointStore(tmpDir);

                // Save checkpoint
                var original = new AgentRunState
                {
                    ThreadId = "thread-42",
                    Turn = 3,
                    Round = 2,
                    Status = SessionStatus.Running,
                    UserMessage = "请继续",
                    Plan = new AgentPlan
                    {
                        Goal = "test goal",
                        Steps = new List<PlanStep>
                        {
                            new() { Id = 1, Description = "step 1", Status = PlanStepStatus.Completed },
                            new() { Id = 2, Description = "step 2", Status = PlanStepStatus.Pending }
                        }
                    }
                };
                await store.SaveAsync(original);

                var restored = await store.LoadMostRecentAsync(CancellationToken.None);

                restored.Should().NotBeNull();
                restored!.ThreadId.Should().Be("thread-42");
                restored.Turn.Should().Be(3);
                restored.Round.Should().Be(2);
                restored.Plan.Should().NotBeNull();
                restored.Plan!.Goal.Should().Be("test goal");
                restored.Plan.Steps.Should().HaveCount(2);
                restored.Plan.Steps[0].Id.Should().Be(1);
                restored.Plan.Steps[1].Id.Should().Be(2);
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        [Fact]
        public async Task Checkpoint_latest_overwrites_previous()
        {
            var tmpDir = Path.Combine(Path.GetTempPath(), $"ckpt_test_{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(tmpDir);
                var store = new FileRunCheckpointStore(tmpDir);

                // Save first checkpoint
                await store.SaveAsync(new AgentRunState { ThreadId = "t1", Turn = 1 });

                // Save second checkpoint (same thread)
                await store.SaveAsync(new AgentRunState { ThreadId = "t1", Turn = 5 });

                // Should restore latest
                var restored = await store.LoadMostRecentAsync(System.Threading.CancellationToken.None);
                restored.Should().NotBeNull();
                restored!.Turn.Should().Be(5, "应恢复最新的 checkpoint");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }
    }
}
