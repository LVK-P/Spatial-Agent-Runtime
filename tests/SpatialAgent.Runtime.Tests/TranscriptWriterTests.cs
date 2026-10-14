using FluentAssertions;
using Newtonsoft.Json.Linq;
using SpatialAgent.Runtime.Contracts;
using SpatialAgent.Services;
using SpatialAgent.Core;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    /// <summary>
    /// TranscriptWriter unit tests — initialization, migration, event
    /// count consistency, dispose safety, and sanitization edge cases.
    /// </summary>
    /// <remarks>中文：TranscriptWriter单元测试——初始化、迁移、事件计数一致性、丢弃安全性和净化边缘案例。</remarks>
    public class TranscriptWriterTests : IDisposable
    {
        private TranscriptWriter? _tw;
        private string? _filePath;

        private TranscriptWriter Init()
        {
            var dir = Path.Combine(Path.GetTempPath(), "tw_test_" + Guid.NewGuid().ToString("N")[..8]);
            _tw = new TranscriptWriter();
            _tw.Initialize(dir);
            _filePath = _tw.FilePath;
            return _tw;
        }

        public void Dispose()
        {
            _tw?.Dispose();
            if (_filePath != null && File.Exists(_filePath))
            {
                try { File.Delete(_filePath); } catch { }
            }
        }

        [Fact]
        public void Shutdown_observation_is_durable_and_log_closed_is_last()
        {
            var tw = Init();
            tw.Write(new { type = "queued_before_shutdown" });
            tw.WriteHostShutdownObserved(
                "session-1",
                "WM_CLOSE",
                SessionStatus.Completed,
                false);

            tw.Dispose();

            var events = File.ReadAllLines(_filePath!)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(JObject.Parse)
                .ToList();
            events.Should().Contain(item =>
                (string?)item["type"] == "host_shutdown_observed");
            ((string?)events.Last()["type"]).Should().Be("log_closed");
        }

        [Fact]
        public void Initialize_creates_file_and_writes_log_initialized()
        {
            var tw = Init();
            tw.IsEnabled.Should().BeTrue();
            tw.FilePath.Should().NotBeNull();
            File.Exists(tw.FilePath).Should().BeTrue($"file should exist at {tw.FilePath}");
            // log_initialized is written during Initialize
            // ★ P0-2：Flush 排空 Channel 后再检查 EventCount
            // 中文：log_initialized 是在 Initialize P0-2：Flush 排空 Channel 后再检查 EventCount 期间编写的
            tw.Flush();
            tw.EventCount.Should().BeGreaterOrEqualTo(1);
        }

        [Fact]
        public void Tool_lifecycle_is_durable_without_close_or_async_drain()
        {
            var tw = Init();
            var bus = new SpatialAgent.Runtime.EventBus();
            tw.SubscribeTo(bus);

            bus.Publish(new AgentEvent
            {
                EventType = "tool.started",
                Turn = 2,
                Metadata = new()
                {
                    ["tool"] = "management_CalculateField",
                    ["step"] = "4",
                    ["risk"] = "Modify"
                },
                Data = new()
                {
                    ["args"] = new Dictionary<string, object?>
                    {
                        ["in_table"] = "roads"
                    }
                }
            });

            using var stream = new FileStream(
                _filePath!,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            reader.ReadToEnd().Should()
                .Contain("\"type\":\"tool_start\"")
                .And.Contain("\"step\":4");
        }

        [Fact]
        public void Host_progress_and_gp_warmup_events_are_reconstructable()
        {
            var tw = Init();
            var bus = new SpatialAgent.Runtime.EventBus();
            tw.SubscribeTo(bus);

            bus.Publish(new AgentEvent
            {
                EventType = "runtime.gp_warmup",
                Metadata = new() { ["phase"] = "ready" },
                Data = new() { ["message"] = "ready" }
            });
            bus.Publish(new AgentEvent
            {
                EventType = "tool.progress",
                Turn = 2,
                Metadata = new()
                {
                    ["tool"] = "analysis_Buffer",
                    ["step"] = "3",
                    ["phase"] = "execute_heartbeat",
                    ["elapsed_ms"] = "5000",
                    ["stalled"] = "false"
                },
                Data = new() { ["message"] = "working" }
            });
            bus.Publish(new AgentEvent
            {
                EventType = "execution.host_state_changed",
                Turn = 2,
                Metadata = new()
                {
                    ["tool"] = "analysis_Buffer",
                    ["step"] = "3",
                    ["decision"] = "ExternalChange",
                    ["planned_revision"] = "before",
                    ["current_revision"] = "after"
                },
                Data = new() { ["reason"] = "map changed" }
            });
            bus.Publish(new AgentEvent
            {
                EventType = "execution.infrastructure_baseline_adopted",
                Turn = 2,
                Metadata = new()
                {
                    ["tool"] = "management_DeleteField",
                    ["step"] = "4",
                    ["before_revision"] = "before",
                    ["after_revision"] = "backup"
                }
            });

            tw.Flush();
            using var stream = new FileStream(
                _filePath!,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            string content = reader.ReadToEnd();
            content.Should().Contain("\"type\":\"gp_warmup\"");
            content.Should().Contain("\"type\":\"tool_progress\"");
            content.Should().Contain("\"type\":\"host_state_changed\"");
            content.Should().Contain("\"type\":\"infrastructure_baseline\"");
        }

        [Fact]
        public void Initialize_twice_is_idempotent()
        {
            var tw = Init();
            var path1 = tw.FilePath;
            var count1 = tw.EventCount;

            tw.Initialize(Path.Combine(Path.GetTempPath(), "tw_test2_" + Guid.NewGuid().ToString("N")[..8])); // 第二次调用不执行任何操作。 / The second call is a no-op.

            tw.FilePath.Should().Be(path1, "second Initialize should be no-op");
            tw.EventCount.Should().Be(count1, "no additional events written");
        }

        [Fact]
        public void Write_increments_event_count()
        {
            // ★ P0-2：Write() 经 Channel<T> 异步写入，计数在 Dispose 后排空。
            // 验证文件内容包含写入的事件（端到端断言替代 EventCount 实时断言）。
            // English：P0-2: Write() is written asynchronously via Channel, and the count is empty after Dispose. Verify that the file content contains the written event (end-to-end assertions replace EventCount real-time assertions).
            var tw = new TranscriptWriter();
            tw.Initialize(Path.Combine(Path.GetTempPath(), "tw_inc_" + Guid.NewGuid().ToString("N")[..8]));
            var fp = tw.FilePath;
            try
            {
                tw.Write(new { type = "test", value = 42 });
                tw.WriteSessionEnd(1, 1, 0, 0, 100);
                // Dispose → Flush → 排空 Channel → 关闭文件
                // English：Dispose → Flush → Empty Channel → Close the file
                tw.Dispose();

                // 验证文件内容
                // English：Verify the contents of the documents
                var content = File.ReadAllText(fp!);
                content.Should().Contain("\"type\":\"test\"");
                content.Should().Contain("\"type\":\"session_end\"");
                // ★ P0-2：session_end 应包含 transcript_integrity 字段
                // English：P0-2:session_end should include transcript_integrity fields
                content.Should().Contain("transcript_integrity");
            }
            finally
            {
                if (fp != null && File.Exists(fp))
                {
                    try { File.Delete(fp); } catch { }
                }
            }
        }

        [Fact]
        public void Dispose_then_write_does_not_throw()
        {
            var tw = Init();
            tw.Dispose();

            var act = () => tw.Write(new { type = "after_dispose" });
            act.Should().NotThrow("writes after dispose are silently ignored");
        }

        [Fact]
        public void Write_before_initialize_does_not_throw()
        {
            var tw = new TranscriptWriter();
            var act = () => tw.Write(new { type = "before_init" });
            act.Should().NotThrow("writes before Initialize are silently ignored");
        }

        [Fact]
        public void Session_start_and_user_message_are_recorded()
        {
            var tw = Init();
            tw.WriteSessionStart("s1", "m1", "low", @"G:\projects\test");
            tw.WriteUserMessage(1, "你好");

            tw.Dispose();
            var lines = File.ReadAllLines(_filePath!);
            // Should have: log_initialized, session_start, user_msg
            // 中文：应该有：log_initialized，session_start，user_msg
            lines.Should().HaveCountGreaterOrEqualTo(3);
            var content = string.Join("\n", lines);
            content.Should().Contain("session_start");
            content.Should().Contain("user_msg");
            content.Should().Contain("你好");
        }

        [Fact]
        public void MigrateTo_moves_file_to_new_directory()
        {
            var tw = Init();
            tw.WriteSessionStart("s1", "m1", "", null);
            var oldPath = tw.FilePath!;

            var newDir = Path.Combine(Path.GetTempPath(), "migrate_test_" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                tw.MigrateTo(newDir);
                tw.FilePath.Should().StartWith(newDir);
                File.Exists(tw.FilePath).Should().BeTrue();
                File.Exists(oldPath).Should().BeFalse("old file should be moved");
            }
            finally
            {
                tw.Dispose();
                if (Directory.Exists(newDir))
                {
                    try { Directory.Delete(newDir, true); } catch { }
                }
            }
        }

        [Fact]
        public void Write_context_preserves_full_text_and_flags_truncation()
        {
            // ★ Phase A5（2026-07-20）：WriteContext 不再截断——全量写入，
            // 但附带 project_truncated/map_truncated 标记。
            // English：Phase A5 (2026-07-20): WriteContext no longer truncates—full write, but with project_truncated/map_truncated marking.
            var tw = Init();
            var longProject = new string('A', 600);
            tw.WriteContext(1, longProject, "map", 3, 2);

            tw.Dispose();
            var content = File.ReadAllText(_filePath!);
            // 全量 600 字符应完整保留
            // English：The full 600 characters should be fully retained
            content.Should().Contain(new string('A', 600));
            // project_truncated 标记应为 true
            // English：project_truncated The tag should be true
            content.Should().Contain("\"project_truncated\":true");
            // project_len 应为 600
            // English：project_len should be 600
            content.Should().Contain("\"project_len\":600");
            // map 未超出限制，map_truncated 应为 false
            // English：If the map does not exceed the limit, map_truncated should be false
            content.Should().Contain("\"map_truncated\":false");
        }

        // ════════════════════════════════════════════════════════════════
        // ★ P0-2：transcript_integrity 在 session_end 中声明
        // ════════════════════════════════════════════════════════════════
        // English：P0-2: transcript_integrity is declared in session_end

        [Fact]
        public void WriteSessionEnd_includes_transcript_integrity_after_flush()
        {
            var tw = Init();
            tw.WriteSessionStart("s1", "m1", "", null);
            tw.WriteUserMessage(1, "测试消息");
            tw.WriteSessionEnd(1, 2, 0, 0, 5000);

            tw.Dispose();
            var content = File.ReadAllText(_filePath!);

            // session_end JSONL 行必须包含 transcript_integrity 对象
            // English：session_end JSONL lines must contain transcript_integrity objects
            content.Should().Contain("transcript_integrity");
            content.Should().Contain("\"total_events\"");
            content.Should().Contain("\"lost_events\"");
        }

        [Fact]
        public void Lost_events_counter_is_zero_for_clean_session()
        {
            var tw = Init();
            tw.WriteSessionStart("s1", "m1", "", null);
            tw.WriteSessionEnd(1, 1, 0, 0, 1000);

            tw.Dispose();

            tw.LostEventCount.Should().Be(0);
            tw.FirstLossAt.Should().BeNull();
        }

        [Fact]
        public void ResetLossCounter_clears_previous_state()
        {
            var tw = Init();
            // 在 Initialize 中已调用 ResetLossCounter，确认初始状态
            // English：ResetLossCounter is called in Initialize to confirm the initial state
            tw.LostEventCount.Should().Be(0);

            tw.WriteSessionEnd(1, 1, 0, 0, 1000);
            tw.Dispose();

            // 无写入错误 → LostEventCount 仍然为 0
            // English：No write errors → LostEventCount remains 0
            tw.LostEventCount.Should().Be(0);
        }

        [Fact]
        public void Flush_drain_and_recreate_allows_continued_writes()
        {
            var tw = Init();
            tw.Write(new { type = "event_before_flush", n = 1 });
            tw.Flush();

            // Flush 后应能继续写入
            // English：After flushing, writing should continue
            tw.Write(new { type = "event_after_flush", n = 2 });
            tw.WriteSessionEnd(1, 2, 0, 0, 1000);
            tw.Dispose();

            var content = File.ReadAllText(_filePath!);
            content.Should().Contain("event_before_flush");
            content.Should().Contain("event_after_flush");
            content.Should().Contain("session_end");
        }

        // ════════════════════════════════════════════════════════════════
        // ★ Phase A1：diagnostics 事件写入 transcript
        // ════════════════════════════════════════════════════════════════
        // English：Phase A1: Diagnostics event writes transcript

        [Fact]
        public void Diagnostics_warning_events_are_recorded()
        {
            var tw = Init();
            var bus = new Runtime.EventBus();
            tw.SubscribeTo(bus);

            bus.Publish(new AgentEvent
            {
                EventType = "diagnostics.warning",
                Turn = 1,
                Metadata = new()
                {
                    ["area"] = "ContextManager.Map",
                    ["message"] = "Map context refresh failed",
                    ["exception"] = "System.TimeoutException",
                    ["stacktrace"] = "at ContextManager.RefreshMap() in ..."
                }
            });

            tw.Dispose();
            var content = File.ReadAllText(_filePath!);
            content.Should().Contain("\"type\":\"diagnostics\"");
            content.Should().Contain("\"level\":\"WARN\"");
            content.Should().Contain("ContextManager.Map");
            content.Should().Contain("Map context refresh failed");
            content.Should().Contain("System.TimeoutException");
        }

        [Fact]
        public void Diagnostics_error_events_include_stacktrace()
        {
            var tw = Init();
            var bus = new Runtime.EventBus();
            tw.SubscribeTo(bus);

            bus.Publish(new AgentEvent
            {
                EventType = "diagnostics.error",
                Turn = 2,
                Metadata = new()
                {
                    ["area"] = "AgentRuntime.ProcessAsync",
                    ["message"] = "LLM call failed",
                    ["exception"] = "System.Net.Http.HttpRequestException",
                    ["error"] = "Connection refused",
                    ["stacktrace"] = "at DeepSeekProvider.SendAsync() line 42",
                    ["hresult"] = "80004005"
                }
            });

            tw.Dispose();
            var content = File.ReadAllText(_filePath!);
            content.Should().Contain("\"level\":\"ERROR\"");
            content.Should().Contain("\"stacktrace\":\"at DeepSeekProvider.SendAsync() line 42\"");
            content.Should().Contain("\"hresult\":\"80004005\"");
        }

        [Fact]
        public void Run_id_is_injected_when_correlation_context_active()
        {
            // ★ Phase A4：CorrelationContext 活跃时，Write() 自动注入 run_id/span_id
            // English：Phase A4: When CorrelationContext is active, Write() automatically injects run_id/span_id
            using (CorrelationContext.BeginRun("test-run-42"))
            {
                using (CorrelationContext.BeginSpan("test_span"))
                {
                    var tw = Init();
                    tw.Write(new { type = "test_event", value = 1 });
                    tw.Dispose();

                    var content = File.ReadAllText(_filePath!);
                    content.Should().Contain("\"run_id\":\"test-run-42\"");
                    content.Should().Contain("test_span");  // 内容中包含 span_id。 / span_id appears in the content.
                }
            }
        }

        [Fact]
        public void Tool_exposure_is_recorded_with_counts()
        {
            var tw = Init();
            tw.WriteToolExposure(1, 25, 40, 30,
                new List<string> { "management_Buffer", "management_Clip" },
                new List<string> { "management_Erase" },
                "management_Buffer");

            tw.Dispose();
            var content = File.ReadAllText(_filePath!);
            content.Should().Contain("\"type\":\"tool_exposure\"");
            content.Should().Contain("\"tier1_count\":25");
            content.Should().Contain("\"tier2_count\":40");
            content.Should().Contain("\"always_on_count\":30");
            content.Should().Contain("\"total_exposed\":65");
            content.Should().Contain("management_Buffer");
            content.Should().Contain("\"chain_source\":\"management_Buffer\"");
        }

        [Fact]
        public void Repeated_llm_tool_schemas_are_referenced_by_hash()
        {
            var tw = Init();
            var tools = new[]
            {
                new { name = "management_Project", parameters = new { type = "object" } }
            };
            tw.WriteLLMRequest(1, new[] { "first" }, tools, "prompt-a");
            tw.WriteLLMRequest(1, new[] { "second" }, tools, "prompt-a");

            tw.Dispose();
            var requests = File.ReadAllLines(_filePath!)
                .Select(JObject.Parse)
                .Where(item => item.Value<string>("type") == "llm_request")
                .ToList();

            requests.Should().HaveCount(2);
            requests[0]["tools"].Should().NotBeNull();
            requests[0].Value<bool>("tools_reused").Should().BeFalse();
            requests[1]["tools"].Should().BeNull();
            requests[1].Value<bool>("tools_reused").Should().BeTrue();
            requests[1].Value<string>("tools_hash")
                .Should().Be(requests[0].Value<string>("tools_hash"));
        }

        [Fact]
        public void Session_end_includes_logging_health_when_monitor_configured()
        {
            var tw = Init();
            var monitor = new Services.LoggingHealthMonitor();
            tw.SetLoggingHealthMonitor(monitor);

            // 制造一次假故障
            // English：Creating a false fault
            monitor.ReportWriteFailure("ledger", "DiskFull");

            tw.WriteSessionStart("s1", "m1", "", null);
            tw.WriteSessionEnd(1, 3, 0, 0, 5000);
            tw.Dispose();

            var content = File.ReadAllText(_filePath!);
            content.Should().Contain("logging_health");
            content.Should().Contain("\"all_healthy\":false");
            content.Should().Contain("Ledger(失败1次)");
        }
    }
}
