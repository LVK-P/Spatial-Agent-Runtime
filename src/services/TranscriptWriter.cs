using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SpatialAgent.Runtime;
using SpatialAgent.Runtime.Contracts;
using SpatialAgent.Core;

// ================================================================
// TranscriptWriter.cs — Append-only JSONL conversation transcript.
//
// Subscribes to EventBus for automatic recording of tool execution,
// safety decisions, verification results, AND diagnostics events.
// Supports two-phase path: initial writes go to AppData; MigrateTo()
// moves them to the project directory once the project path is known.
//
// ★ Phase A（2026-07-20）：订阅 diagnostics.* 事件、自动附加
// CorrelationContext 的 run_id/span_id、WriteContext 全量写入。
//
// JSONL event types: session_start, user_msg, context_gathered,
// llm_request, llm_response, tool_start, tool_end, verification,
// synth_input, synth_output, session_end, diagnostics, tool_exposure.
// ================================================================
// 中文：================================================================ TranscriptWriter.cs — 仅附加的JSONL对话文字记录。订阅EventBus，自动记录工具执行、安全决策、验证结果及诊断事件。支持两阶段路径：初始写入发送给 AppData;一旦知道项目路径，MigrateTo（） 会将它们移到项目目录。Phase A（2026-07-20）：订阅 diagnostics.* 事件、自动附加 CorrelationContext 的 run_id/span_id、WriteContext 全量写入。JSONL事件类型：session_start、user_msg、context_gathered、llm_request、llm_response、tool_start、tool_end、验证、synth_input、synth_output、session_end、诊断、tool_exposure。================================================================
namespace SpatialAgent.Services
{
    public class TranscriptWriter : IDisposable,
        ITranscriptLifecycle, ITranscriptSessionWriter, ITranscriptTurnWriter,
        ITranscriptToolWriter, ITranscriptHealth
    {
        private readonly object _lock = new();
        private readonly object _directWriteLock = new();  // ★ P0-2 修复：关键事件同步写锁。 / Synchronous write lock for critical events.
        private string? _filePath;
        private StreamWriter? _writer;
        private bool _disposed;
        private readonly HashSet<string> _loggedToolSchemaHashes =
            new(StringComparer.Ordinal);

        // ── S5（Phase 3）：路径脱敏 Layer 2 锚点 ──
        // English：S5 (Phase 3): Path anxious Layer 2 anchor point
        /// <summary>项目根目录路径锚点（如 G:\Projects\MyMap）→ 脱敏为 {PROJECT}。</summary>
        /// <remarks>English：The root directory path anchor (e.g., G:\Projects\MyMap) → anxious to {PROJECT}.</remarks>
        public string? ProjectRoot { get; set; }
        /// <summary>工作空间根目录路径锚点 → 脱敏为 {WORKSPACE}。</summary>
        /// <remarks>English：The root directory path anchor point → is anonymized as {WORKSPACE}.</remarks>
        public string? WorkspaceRoot { get; set; }
        /// <summary>用户主目录路径锚点 → 脱敏为 {HOME}。</summary>
        /// <remarks>English：The user's home directory path anchor point → is anonymized as {HOME}.</remarks>
        public string? HomeFolder { get; set; }

        // ── P1-C（2026-07-20）：EventBus 接收计数器，用于一致性校验 ──
        // English：P1-C (2026-07-20): EventBus receive counter for consistency verification
        private long _toolStartReceived;
        private long _toolEndReceived;  // tool.completed 与 tool.failed 的总数。 / Total tool.completed plus tool.failed events.
        private long _submissionSequence;

        // ── 4A（Phase 4）：一致性校验 —— 依赖 IEventCounters 抽象而非 EventBus 具体类型 ──
        // English：4A (Phase 4): Consistency Check — Relies on IEventCounters abstraction rather than EventBus concrete types
        /// <summary>
        /// 使用发布端计数器进行交叉校验。通过 IEventCounters 解耦——
        /// 不再依赖 EventBus 具体实现（ADR 0056 / Phase 4A）。
        /// 如果 counters 为 null（如测试环境），仅做内部一致性检查。
        /// </summary>
        /// <remarks>English：Cross-check is performed using the publisher counter. Decoupling via IEventCounters—no longer relying on EventBus implementation (ADR 0056 / Phase 4A). If counters are null (as in the test environment), only perform internal conformance checks.</remarks>
        public void CheckConsistency(IEventCounters? counters = null)
        {
            long started = Interlocked.Read(ref _toolStartReceived);
            long ended = Interlocked.Read(ref _toolEndReceived);

            if (started != ended)
            {
                AgentDiagnostics.Error("TranscriptWriter.Consistency",
                    $"tool_started({started}) ≠ tool_completed+tool_failed({ended})——" +
                    $"{(started > ended ? $"丢失 {started - ended} 条工具结束事件" : $"多了 {ended - started} 条工具结束事件（异常状态）")}。" +
                    "审计记录不完整，请检查 EventBus subscriber 是否存在未捕获的异常。");
            }

            if (counters != null)
            {
                long pubStarted = counters.ToolStartedCount;
                long pubEnded = counters.ToolCompletedCount + counters.ToolFailedCount;
                if (pubStarted != started || pubEnded != ended)
                {
                    AgentDiagnostics.Error("TranscriptWriter.Consistency",
                        $"EventBus 发布端 (started={pubStarted}, ended={pubEnded}) 与 Transcript 接收端 (started={started}, ended={ended}) 不一致——" +
                        "存在 EventBus→Transcript 的事件丢失。");
                }
            }
        }
        private Channel<object>? _channel;
        private CancellationTokenSource? _consumerCts;
        private Task? _consumerTask;

        private long _lostEventCount;
        private string? _firstLossReason;
        private DateTime? _firstLossAt;

        // ── ★ Phase B4（2026-07-20）：日志健康监控 ──
        // English：Phase B4 (2026-07-20): Log health monitoring
        private ILoggingHealthMonitor? _healthMonitor;
        public void SetLoggingHealthMonitor(ILoggingHealthMonitor? hm) => _healthMonitor = hm;

        public string? FilePath => _filePath;
        // ★ P0-2：EventCount 的原子递增——Channel 消费者在独立线程中写入
        // English：P0-2: EventCount's atomic increase—Channel consumers write in independent threads
        private int _eventCountBacking;
        public int EventCount => _eventCountBacking;
        private void IncrementEventCount() => Interlocked.Increment(ref _eventCountBacking);
        public bool IsEnabled => _writer != null;

        /// <summary>
        /// ★ P0-2：返回自上次 ResetLossCounter（或创建）以来丢失的事件数量。
        /// WriteSessionEnd 自动包含此统计。
        /// </summary>
        /// <remarks>English：P0-2: Returns the number of events lost since the last ResetLossCounter (or creation). WriteSessionEnd automatically includes this statistic.</remarks>
        public long LostEventCount => Interlocked.Read(ref _lostEventCount);
        public DateTime? FirstLossAt => _firstLossAt;
        public string? FirstLossReason => _firstLossReason;

        /// <summary>重置丢失计数器（用于新的 session 开始前）。</summary>
        /// <remarks>English：Reset the loss counter (used before a new session starts).</remarks>
        public void ResetLossCounter()
        {
            Interlocked.Exchange(ref _lostEventCount, 0);
            _firstLossAt = null;
            _firstLossReason = null;
        }

        /// <summary>
        /// Initialize with a base directory. Creates transcript_<timestamp>.jsonl.
        /// ★ P0-2（2026-07-20）：同时启动 Channel<T> 异步消费者，
        /// 将 I/O 从 EventBus 的同步发布线程解耦。
        /// </summary>
        /// <remarks>English：Initialize with a base directory. Creates transcript_ .jsonl. P0-2 (2026-07-20): Simultaneous start of Channel asynchronous consumers, decoupling I/O from the synchronous publishing thread of EventBus.</remarks>
        public void Initialize(string logDir)
        {
            lock (_lock)
            {
                if (_writer != null) return;

                Directory.CreateDirectory(logDir);
                _filePath = Path.Combine(logDir, $"transcript_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");
                _writer = OpenWriter(_filePath);

                // ★ P0-2：创建有界 Channel（10000 条缓冲，满时丢弃最旧的）
                // English：P0-2: Create a Bounded Channel (10,000 buffers, discard the oldest one when full)
                _channel = Channel.CreateBounded<object>(
                    new BoundedChannelOptions(10000)
                    {
                        FullMode = BoundedChannelFullMode.Wait
                    });

                // 启动异步消费者
                // English：Launch asynchronous consumers
                _consumerCts = new CancellationTokenSource();
                _consumerTask = Task.Run(() => ConsumeLoopAsync(_consumerCts.Token));

                // 重置丢失计数器（新 session）
                // English：Reset the Lost Counter (New Session)
                ResetLossCounter();

                WriteCritical(new { type = "log_initialized", ts = DateTime.Now.ToString("O"), path = _filePath });
            }
        }

        /// <summary>
        /// ★ P0-2：后台消费者——从 Channel 读取事件并写入 StreamWriter。
        /// I/O 异常（磁盘满/文件锁定/网络断开）被隔离在此线程中，
        /// 不会阻塞 EventBus 的同步发布线程。
        /// </summary>
        /// <remarks>English：P0-2: Backend Consumer — Reads events from Channel and writes them to StreamWriter. I/O exceptions (disk full/file lock/network disconnection) are isolated in this thread and will not block the EventBus synchronous publishing thread.</remarks>
        private async Task ConsumeLoopAsync(CancellationToken ct)
        {
            if (_writer == null) return;  // 防御：不应被调用，同时消除 CS8602。 / Defensive guard; should not be called and avoids CS8602.
            var writer = _writer;         // 捕获非 null 引用，避免后续警告。 / Capture the non-null reference to avoid later warnings.

            // 累积缓冲：减少 StreamWriter 系统调用频率
            // English：Cumulative buffering: Reduces the frequency of StreamWriter system calls
            const int flushInterval = 50;  // 每 50 条刷新一次。 / Flush every 50 entries.
            int countSinceFlush = 0;

            while (await _channel!.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (_channel.Reader.TryRead(out var entry))
                {
                    try
                    {
                        string json = SerializeSanitized(entry);
                        // ★ P0-2 fix（2026-07-22）：与 WriteCritical 共享 _directWriteLock，
                        // 防止 channel 消费者和关键事件同步写产生 StreamWriter 竞态。
                        // English：P0-2 fix (2026-07-22): Shares _directWriteLock with WriteCritical to prevent StreamWriter contention from occurring when channel consumers and critical events write simultaneously.
                        lock (_directWriteLock)
                        {
                            writer.WriteLine(json);
                        }
                        IncrementEventCount();
                        countSinceFlush++;

                        // 如果之前有丢失，现在恢复了——写恢复事件
                        // English：If there was a loss before, it has now been restored—write about the recovery event
                        if (_firstLossAt != null)
                        {
                            var recovered = Interlocked.Exchange(ref _lostEventCount, 0);
                            var reason = _firstLossReason;
                            _firstLossAt = null;
                            _firstLossReason = null;
                            try
                            {
                                string recoveryJson = JsonConvert.SerializeObject(new
                                {
                                    type = "transcript_recovered",
                                    ts = DateTime.Now.ToString("O"),
                                    lost_events = recovered,
                                    reason
                                });
                                lock (_directWriteLock)
                                {
                                    writer.WriteLine(recoveryJson);
                                }
                            }
                            catch { /* 恢复事件写入失败，已是最坏情况。 / Recovery-event write failed; this is already the worst case. */ }
                        }

                        if (countSinceFlush >= flushInterval)
                        {
                            lock (_directWriteLock)
                            {
                                writer.Flush();
                            }
                            countSinceFlush = 0;
                        }
                    }
                    catch (IOException) when (!ct.IsCancellationRequested)
                    {
                        // ArcGIS Pro 文件锁定或网络断开 → 回退到同步计数模式
                        // English：ArcGIS Pro file lock or network disconnection → revert to synchronous counting mode
                        RecordLoss("IOException (file locked or network disconnected)");
                        await Task.Delay(1000, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        RecordLoss($"{ex.GetType().Name}: {ex.Message}");
                    }
                }

                // 最后 flush 剩余事件（★ P0-2 fix：与 WriteCritical 共享锁）
                // English：Final flush remaining event (P0-2 fix: shared lock with WriteCritical)
                try { lock (_directWriteLock) { writer.Flush(); } countSinceFlush = 0; }
                catch { RecordLoss("Flush failed"); }
            }
        }

        /// <summary>
        /// ★ P0-2：记录一次写入丢失，并发送诊断信号。
        /// ★ Phase B2（2026-07-20）：同步上报 LoggingHealthMonitor。
        /// </summary>
        /// <remarks>English：P0-2: Records a single write loss and sends a diagnostic signal. Phase B2 (2026-07-20): Simultaneously reported to LoggingHealthMonitor.</remarks>
        public void RecordLoss(string reason)
        {
            Interlocked.Increment(ref _lostEventCount);
            if (_firstLossAt == null)
            {
                _firstLossAt = DateTime.UtcNow;
                _firstLossReason = reason;
            }
            _healthMonitor?.ReportWriteFailure("transcript", reason);
            AgentDiagnostics.Error("TranscriptWriter",
                $"审计写入失败（累计丢失 {Interlocked.Read(ref _lostEventCount)} 条事件，" +
                $"原因：{reason}）。磁盘可能已满、文件被锁定、或权限不足。");
        }

        /// <summary>Subscribe to EventBus tool events for automatic recording.</summary>
        /// <remarks>中文：订阅EventBus工具事件，自动录制。</remarks>
        public void SubscribeTo(IEventBus eventBus)
        {
            eventBus.Subscribe("tool.started", e =>
            {
                Interlocked.Increment(ref _toolStartReceived);
                int turn = e.Turn ?? 0;
                int step = int.TryParse(e.Metadata?.GetValueOrDefault("step", "0"), out var s) ? s : 0;
                string tool = e.Metadata?.GetValueOrDefault("tool", "?") ?? "?";
                var args = SanitizeArgs(e.Data?.GetValueOrDefault("args"));
                WriteCritical(new { type = "tool_start", turn, step, ts = DateTime.Now.ToString("HH:mm:ss.fff"), tool, args });
            });
            eventBus.Subscribe("tool.completed", e =>
            {
                Interlocked.Increment(ref _toolEndReceived);
                int turn = e.Turn ?? 0;
                int step = int.TryParse(e.Metadata?.GetValueOrDefault("step", "0"), out var s) ? s : 0;
                string tool = e.Metadata?.GetValueOrDefault("tool", "?") ?? "?";
                bool success = bool.TryParse(e.Metadata?.GetValueOrDefault("success"), out var ok) && ok;
                long elapsed = long.TryParse(e.Metadata?.GetValueOrDefault("elapsed_ms"), out var ms) ? ms : 0;
                int retry = int.TryParse(e.Metadata?.GetValueOrDefault("retry", "0"), out var r) ? r : 0;
                var output = e.Data?.GetValueOrDefault("output");
                var error = e.Data?.GetValueOrDefault("error");
                WriteCritical(new { type = "tool_end", turn, step, ts = DateTime.Now.ToString("HH:mm:ss.fff"), tool, success, output, error, elapsed_ms = elapsed, retry, status = e.Metadata?.GetValueOrDefault("status", "?") });
            });
            eventBus.Subscribe("tool.failed", e =>
            {
                Interlocked.Increment(ref _toolEndReceived);
                int turn = e.Turn ?? 0;
                int step = int.TryParse(e.Metadata?.GetValueOrDefault("step", "0"), out var s) ? s : 0;
                string tool = e.Metadata?.GetValueOrDefault("tool", "?") ?? "?";
                long elapsed = long.TryParse(e.Metadata?.GetValueOrDefault("elapsed_ms"), out var ms) ? ms : 0;
                int retry = int.TryParse(e.Metadata?.GetValueOrDefault("retry", "0"), out var r) ? r : 0;
                var output = e.Data?.GetValueOrDefault("output");
                var error = e.Data?.GetValueOrDefault("error");
                WriteCritical(new { type = "tool_end", turn, step, ts = DateTime.Now.ToString("HH:mm:ss.fff"), tool, success = false, output, error, elapsed_ms = elapsed, retry, status = e.Metadata?.GetValueOrDefault("status", "Failed") });
            });
            eventBus.Subscribe("tool.progress", e =>
            {
                int turn = e.Turn ?? 0;
                int step = int.TryParse(
                    e.Metadata?.GetValueOrDefault("step", "0"),
                    out var parsedStep)
                    ? parsedStep
                    : 0;
                string tool = e.Metadata?.GetValueOrDefault("tool", "?") ?? "?";
                _ = long.TryParse(
                    e.Metadata?.GetValueOrDefault("elapsed_ms", "0"),
                    out long elapsed);
                _ = int.TryParse(
                    e.Metadata?.GetValueOrDefault("progress_pct", ""),
                    out int progress);
                Write(new
                {
                    type = "tool_progress",
                    turn,
                    step,
                    ts = DateTime.Now.ToString("HH:mm:ss.fff"),
                    tool,
                    phase = e.Metadata?.GetValueOrDefault("phase", "execute"),
                    elapsed_ms = elapsed,
                    progress_pct = e.Metadata?.ContainsKey("progress_pct") == true
                        ? progress
                        : (int?)null,
                    stalled = e.Metadata?.GetValueOrDefault("stalled", "false"),
                    message = e.Data?.GetValueOrDefault("message")
                });
            });
            eventBus.Subscribe("llm.progress", e =>
            {
                Write(new
                {
                    type = "llm_progress",
                    turn = e.Turn ?? 0,
                    ts = DateTime.Now.ToString("HH:mm:ss.fff"),
                    round = e.Metadata?.GetValueOrDefault("round", "0"),
                    phase = e.Metadata?.GetValueOrDefault("phase", "planning"),
                    elapsed_ms = e.Metadata?.GetValueOrDefault("elapsed_ms", "0"),
                    stalled = e.Metadata?.GetValueOrDefault("stalled", "false"),
                    message = e.Data?.GetValueOrDefault("message")
                });
            });
            eventBus.Subscribe("llm.semantic_empty_response", e =>
            {
                WriteCritical(new
                {
                    type = "llm_semantic_empty",
                    turn = e.Turn ?? 0,
                    ts = DateTime.Now.ToString("HH:mm:ss.fff"),
                    round = e.Metadata?.GetValueOrDefault("round", "0"),
                    finish_reason = e.Metadata?.GetValueOrDefault("finish_reason", ""),
                    recovery_attempt = e.Metadata?.GetValueOrDefault("recovery_attempt", "1"),
                    completion_tokens = e.Data?.GetValueOrDefault("completion_tokens"),
                    action = e.Data?.GetValueOrDefault("action")
                });
            });
            eventBus.Subscribe("execution.host_state_changed", e =>
            {
                int turn = e.Turn ?? 0;
                int step = int.TryParse(
                    e.Metadata?.GetValueOrDefault("step", "0"),
                    out var parsedStep)
                    ? parsedStep
                    : 0;
                WriteCritical(new
                {
                    type = "host_state_changed",
                    turn,
                    step,
                    ts = DateTime.Now.ToString("HH:mm:ss.fff"),
                    tool = e.Metadata?.GetValueOrDefault("tool", "?"),
                    decision = e.Metadata?.GetValueOrDefault("decision", "?"),
                    planned_revision = e.Metadata?.GetValueOrDefault("planned_revision", ""),
                    current_revision = e.Metadata?.GetValueOrDefault("current_revision", ""),
                    changed = e.Metadata?.GetValueOrDefault("changed", ""),
                    reason = e.Data?.GetValueOrDefault("reason")
                });
            });
            eventBus.Subscribe("execution.infrastructure_baseline_adopted", e =>
            {
                Write(new
                {
                    type = "infrastructure_baseline",
                    turn = e.Turn ?? 0,
                    ts = DateTime.Now.ToString("HH:mm:ss.fff"),
                    tool = e.Metadata?.GetValueOrDefault("tool", "?"),
                    step = e.Metadata?.GetValueOrDefault("step", "0"),
                    before_revision = e.Metadata?.GetValueOrDefault("before_revision", ""),
                    after_revision = e.Metadata?.GetValueOrDefault("after_revision", ""),
                    backup_count = e.Metadata?.GetValueOrDefault("backup_count", "0"),
                    reason = e.Data?.GetValueOrDefault("reason")
                });
            });
            eventBus.Subscribe("runtime.gp_warmup", e =>
            {
                Write(new
                {
                    type = "gp_warmup",
                    ts = DateTime.Now.ToString("HH:mm:ss.fff"),
                    phase = e.Metadata?.GetValueOrDefault("phase", "?"),
                    progress_pct = e.Metadata?.GetValueOrDefault("progress_pct", ""),
                    message = e.Data?.GetValueOrDefault("message"),
                    error = e.Data?.GetValueOrDefault("error")
                });
            });
            eventBus.Subscribe("safety.approval_required", e =>
            {
                int turn = e.Turn ?? 0;
                int step = int.TryParse(e.Metadata?.GetValueOrDefault("step", "0"), out var s) ? s : 0;
                string tool = e.Metadata?.GetValueOrDefault("tool", "?") ?? "?";
                var args = SanitizeArgs(e.Data?.GetValueOrDefault("args"));
                var reason = e.Data?.GetValueOrDefault("reason");
                WriteCritical(new
                {
                    type = "safety_approval_required",
                    turn,
                    step,
                    ts = DateTime.Now.ToString("HH:mm:ss.fff"),
                    tool,
                    risk = e.Metadata?.GetValueOrDefault("risk", "?"),
                    required_permission = e.Metadata?.GetValueOrDefault("required_permission", "?"),
                    ticket_id = e.Metadata?.GetValueOrDefault("ticket_id", ""),
                    approval_kind = e.Metadata?.GetValueOrDefault("approval_kind", ""),
                    call_signature = e.Metadata?.GetValueOrDefault("call_signature", ""),
                    target_signature = e.Metadata?.GetValueOrDefault("target_signature", ""),
                    args,
                    reason,
                    ticket = e.Data?.GetValueOrDefault("ticket"),
                    status = "AwaitingApproval"
                });
            });
            eventBus.Subscribe("safety.influence_approval_required", e =>
            {
                int turn = e.Turn ?? 0;
                int step = int.TryParse(e.Metadata?.GetValueOrDefault("step", "0"), out var s) ? s : 0;
                string tool = e.Metadata?.GetValueOrDefault("tool", "?") ?? "?";
                var args = SanitizeArgs(e.Data?.GetValueOrDefault("args"));
                WriteCritical(new
                {
                    type = "safety_influence_approval",
                    turn,
                    step,
                    ts = DateTime.Now.ToString("HH:mm:ss.fff"),
                    tool,
                    risk = e.Metadata?.GetValueOrDefault("risk", "?"),
                    influence_count = e.Metadata?.GetValueOrDefault("influence_count", "0"),
                    scope_object_count = e.Metadata?.GetValueOrDefault("scope_object_count", "?"),
                    target_field_count = e.Metadata?.GetValueOrDefault("target_field_count", "?"),
                    scope_value_count = e.Metadata?.GetValueOrDefault("scope_value_count", "?"),
                    existing_nonempty_target_count = e.Metadata?.GetValueOrDefault("existing_nonempty_target_count", "?"),
                    new_value_fill_count = e.Metadata?.GetValueOrDefault("new_value_fill_count", "?"),
                    schema_mutation_count = e.Metadata?.GetValueOrDefault("schema_mutation_count", "?"),
                    uses_selection_scope = e.Metadata?.GetValueOrDefault("uses_selection_scope", "false"),
                    uses_filtered_scope = e.Metadata?.GetValueOrDefault("uses_filtered_scope", "false"),
                    source = e.Metadata?.GetValueOrDefault("source", "?"),
                    modify_subtype = e.Metadata?.GetValueOrDefault("modify_subtype", "?"),
                    layer = e.Metadata?.GetValueOrDefault("layer", "?"),
                    field = e.Metadata?.GetValueOrDefault("field", "?"),
                    reliable = e.Metadata?.GetValueOrDefault("reliable", "false"),
                    ticket_id = e.Metadata?.GetValueOrDefault("ticket_id", ""),
                    approval_kind = e.Metadata?.GetValueOrDefault("approval_kind", ""),
                    call_signature = e.Metadata?.GetValueOrDefault("call_signature", ""),
                    target_signature = e.Metadata?.GetValueOrDefault("target_signature", ""),
                    reason = e.Data?.GetValueOrDefault("reason"),
                    estimate = e.Data?.GetValueOrDefault("estimate"),
                    estimate_details = e.Data?.GetValueOrDefault("estimate_details"),
                    ticket = e.Data?.GetValueOrDefault("ticket"),
                    status = "AwaitingApproval"
                });
            });
            eventBus.Subscribe("safety.approval_resumed", e =>
            {
                int turn = e.Turn ?? 0;
                WriteCritical(new
                {
                    type = "approval_resumed",
                    turn,
                    ts = DateTime.Now.ToString("HH:mm:ss.fff"),
                    approved = e.Metadata?.GetValueOrDefault("approved", "false"),
                    action = e.Metadata?.GetValueOrDefault("action", "?"),
                    ticket_id = e.Metadata?.GetValueOrDefault("ticket_id", ""),
                    approval_kind = e.Metadata?.GetValueOrDefault("approval_kind", ""),
                    required_permission = e.Metadata?.GetValueOrDefault("required_permission", ""),
                    approved_steps = e.Metadata?.GetValueOrDefault("approved_steps", ""),
                    approved_tools = e.Metadata?.GetValueOrDefault("approved_tools", ""),
                    rejected_steps = e.Metadata?.GetValueOrDefault("rejected_steps", ""),
                    detail = e.Data?.GetValueOrDefault("approved") ?? e.Data?.GetValueOrDefault("rejected")
                });
            });
            eventBus.Subscribe("safety.approval_rejected", e =>
            {
                int turn = e.Turn ?? 0;
                WriteCritical(new
                {
                    type = "safety_approval_rejected",
                    turn,
                    ts = DateTime.Now.ToString("HH:mm:ss.fff"),
                    tools = e.Metadata?.GetValueOrDefault("tools", ""),
                    reason = e.Metadata?.GetValueOrDefault("reason", "UserRejected"),
                    ticket_id = e.Metadata?.GetValueOrDefault("ticket_id", ""),
                    detail = e.Data?.GetValueOrDefault("rejected_steps")
                });
            });
            eventBus.Subscribe("safety.blocked", e =>
            {
                int turn = e.Turn ?? 0;
                int step = int.TryParse(e.Metadata?.GetValueOrDefault("step", "0"), out var s) ? s : 0;
                string tool = e.Metadata?.GetValueOrDefault("tool", "?") ?? "?";
                var args = SanitizeArgs(e.Data?.GetValueOrDefault("args"));
                var blockReason = e.Data?.GetValueOrDefault("block_reason");
                WriteCritical(new { type = "tool_end", turn, step, ts = DateTime.Now.ToString("HH:mm:ss.fff"), tool, success = false, args, error = blockReason, status = "Blocked" });
            });
            eventBus.Subscribe("validation.failed", e =>
            {
                int turn = e.Turn ?? 0;
                int step = int.TryParse(e.Metadata?.GetValueOrDefault("step", "0"), out var s) ? s : 0;
                string tool = e.Metadata?.GetValueOrDefault("tool", "?") ?? "?";
                Write(new
                {
                    type = "validation_result",
                    turn,
                    step,
                    ts = DateTime.Now.ToString("HH:mm:ss.fff"),
                    tool,
                    verdict = "FAIL",
                    rule = e.Metadata?.GetValueOrDefault("rule", "?"),
                    detail = e.Metadata?.GetValueOrDefault("detail", "")
                });
            });
            eventBus.Subscribe("validation.warned", e =>
            {
                int turn = e.Turn ?? 0;
                int step = int.TryParse(e.Metadata?.GetValueOrDefault("step", "0"), out var s) ? s : 0;
                string tool = e.Metadata?.GetValueOrDefault("tool", "?") ?? "?";
                Write(new
                {
                    type = "validation_result",
                    turn,
                    step,
                    ts = DateTime.Now.ToString("HH:mm:ss.fff"),
                    tool,
                    verdict = "WARN",
                    rule = e.Metadata?.GetValueOrDefault("rule", "?"),
                    detail = e.Metadata?.GetValueOrDefault("detail", "")
                });
            });

            // ── ★ Phase A1（2026-07-20）：订阅 diagnostics 事件，确保诊断告警落盘 ──
            // English：Phase A1 (2026-07-20): Subscribe to diagnostics events to ensure diagnostic alerts are triggered
            eventBus.Subscribe("diagnostics.warning", e =>
            {
                WriteCritical(new
                {
                    type = "diagnostics",
                    level = "WARN",
                    ts = DateTime.Now.ToString("HH:mm:ss.fff"),
                    turn = e.Turn ?? 0,
                    area = e.Metadata?.GetValueOrDefault("area", "?"),
                    message = e.Metadata?.GetValueOrDefault("message", ""),
                    exception = e.Metadata?.GetValueOrDefault("exception", ""),
                    stacktrace = e.Metadata?.GetValueOrDefault("stacktrace", "")
                });
            });
            eventBus.Subscribe("diagnostics.error", e =>
            {
                WriteCritical(new
                {
                    type = "diagnostics",
                    level = "ERROR",
                    ts = DateTime.Now.ToString("HH:mm:ss.fff"),
                    turn = e.Turn ?? 0,
                    area = e.Metadata?.GetValueOrDefault("area", "?"),
                    message = e.Metadata?.GetValueOrDefault("message", ""),
                    exception = e.Metadata?.GetValueOrDefault("exception", ""),
                    error = e.Metadata?.GetValueOrDefault("error", ""),
                    stacktrace = e.Metadata?.GetValueOrDefault("stacktrace", ""),
                    hresult = e.Metadata?.GetValueOrDefault("hresult", "")
                });
            });
            // ── Phase 1 Operational Streaming 补全 ──
            // 中文：第一阶段 作战流 补全
            eventBus.Subscribe("tool.param_autocorrected", e =>
            {
                int turn = e.Turn ?? 0;
                int step = int.TryParse(e.Metadata?.GetValueOrDefault("step", "0"), out var s) ? s : 0;
                string tool = e.Metadata?.GetValueOrDefault("tool", "?") ?? "?";
                var corrections = e.Data?.GetValueOrDefault("corrections");
                Write(new
                {
                    type = "tool_param_corrected",
                    turn, step,
                    ts = DateTime.Now.ToString("HH:mm:ss.fff"),
                    tool,
                    corrections
                });
            });
            eventBus.Subscribe("safety.risk_upgraded", e =>
            {
                int turn = e.Turn ?? 0;
                WriteCritical(new
                {
                    type = "safety_risk_upgraded",
                    turn,
                    ts = DateTime.Now.ToString("HH:mm:ss.fff"),
                    original_tool = e.Metadata?.GetValueOrDefault("original_tool", "?"),
                    resolved_tool = e.Metadata?.GetValueOrDefault("resolved_tool", "?"),
                    original_risk = e.Metadata?.GetValueOrDefault("original_risk", "?"),
                    upgraded_risk = e.Metadata?.GetValueOrDefault("upgraded_risk", "?")
                });
            });
            eventBus.Subscribe("workspace.backup_created", e =>
            {
                int turn = e.Turn ?? 0;
                int step = int.TryParse(e.Metadata?.GetValueOrDefault("step", "0"), out var s) ? s : 0;
                string tool = e.Metadata?.GetValueOrDefault("tool", "?") ?? "?";
                string path = e.Metadata?.GetValueOrDefault("path", "") ?? "";
                Write(new
                {
                    type = "workspace_backup",
                    turn, step,
                    ts = DateTime.Now.ToString("HH:mm:ss.fff"),
                    tool,
                    path
                });
            });
            eventBus.Subscribe("diagnostics.info", e =>
            {
                Write(new
                {
                    type = "diagnostics",
                    level = "INFO",
                    ts = DateTime.Now.ToString("HH:mm:ss.fff"),
                    turn = e.Turn ?? 0,
                    area = e.Metadata?.GetValueOrDefault("area", "?"),
                    message = e.Metadata?.GetValueOrDefault("message", "")
                });
            });

            foreach (string eventType in new[]
                     {
                         "plan.generated",
                         "plan.validation",
                         "plan.reviewed",
                         "plan.review_gate"
                     })
            {
                string subscribedType = eventType;
                eventBus.Subscribe(subscribedType, e =>
                {
                    Write(new
                    {
                        type = subscribedType.Replace('.', '_'),
                        turn = e.Turn ?? 0,
                        ts = DateTime.Now.ToString("HH:mm:ss.fff"),
                        metadata = e.Metadata,
                        data = SanitizeArgs(e.Data)
                    });
                });
            }

            foreach (string eventType in new[]
                     {
                         "goal.compiled",
                         "goal.progress",
                         "goal.closed",
                         "goal.residual"
                     })
            {
                string subscribedType = eventType;
                eventBus.Subscribe(subscribedType, e =>
                {
                    WriteCritical(new
                    {
                        type = subscribedType.Replace('.', '_'),
                        turn = e.Turn ?? 0,
                        ts = DateTime.Now.ToString("HH:mm:ss.fff"),
                        metadata = e.Metadata,
                        data = SanitizeArgs(e.Data)
                    });
                });
            }

            eventBus.Subscribe("safety.approval_invalidated", e =>
            {
                WriteCritical(new
                {
                    type = "safety_approval_invalidated",
                    turn = e.Turn ?? 0,
                    ts = DateTime.Now.ToString("HH:mm:ss.fff"),
                    metadata = e.Metadata,
                    data = SanitizeArgs(e.Data)
                });
            });
            eventBus.Subscribe("safety.approval_bound", e =>
            {
                WriteCritical(new
                {
                    type = "safety_approval_bound",
                    turn = e.Turn ?? 0,
                    ts = DateTime.Now.ToString("HH:mm:ss.fff"),
                    metadata = e.Metadata,
                    data = SanitizeArgs(e.Data)
                });
            });
            eventBus.Subscribe("checkpoint.saved", e =>
            {
                WriteCritical(new
                {
                    type = "checkpoint_saved",
                    turn = e.Turn ?? 0,
                    ts = DateTime.Now.ToString("HH:mm:ss.fff"),
                    metadata = e.Metadata
                });
            });
        }

        /// <summary>Parameter names that contain sensitive values and must be scrubbed from logs.</summary>
        /// <remarks>中文：包含敏感值的参数名称，必须从日志中清除。</remarks>
        private static readonly HashSet<string> SensitiveParamKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "api_key", "apikey", "key", "password", "passwd", "secret",
            "access_key", "secret_key", "accesskey", "secretkey",
            "token", "refresh_token", "auth_token", "authorization",
            "credential", "credentials", "private_key", "privatekey",
            "sde_user_password", "gdb_admin_password", "database_admin_password",
            "pdf_password", "password_protect", "user_password",
            "save_username_password"
        };

        /// <summary>
        /// Recursively scrub sensitive values from an object graph before logging.
        /// Replaces known sensitive field values with "[REDACTED]".
        /// </summary>
        /// <remarks>中文：在日志记录前递归地从对象图中擦除敏感值。将已知的敏感场值替换为“[已编辑]”。</remarks>
        private object? SanitizeArgs(object? value)
        {
            if (value == null) return null;

            if (value is JObject jo)
            {
                var cleaned = new JObject();
                foreach (var prop in jo.Properties())
                {
                    if (SensitiveParamKeys.Contains(prop.Name))
                        cleaned[prop.Name] = "[REDACTED]";
                    else
                        cleaned[prop.Name] = SanitizeArgs(prop.Value) as JToken ?? JValue.CreateNull();
                }
                return cleaned;
            }

            if (value is JArray ja)
                return new JArray(ja.Select(v => SanitizeArgs(v) as JToken ?? JValue.CreateNull()));

            if (value is Dictionary<string, object?> dict)
            {
                var cleaned = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in dict)
                {
                    if (SensitiveParamKeys.Contains(kv.Key))
                        cleaned[kv.Key] = "[REDACTED]";
                    else
                        cleaned[kv.Key] = SanitizeArgs(kv.Value);
                }
                return cleaned;
            }

            // ★ S5（Phase 3）：Layer 2 路径脱敏——递归时检测字符串值中的绝对路径
            // English：S5 (Phase 3): Layer 2 path anonymization—detects absolute paths in string values during recursion
            if (value is string strValue)
                return SanitizePathInString(strValue);

            return value;
        }

        /// <summary>
        /// ★ S5（Phase 3）：Layer 2 路径脱敏——对字符串值做路径锚点前缀匹配。
        /// 匹配顺序：projectRoot → {PROJECT}、workspaceRoot → {WORKSPACE}、
        /// homeFolder → {HOME}，未匹配绝对路径 → {PATH:.ext}。
        /// </summary>
        /// <remarks>English：S5 (Phase 3): Layer 2 path anonymization—performs path anchor prefix matching for string values. Matching order: projectRoot → {PROJECT}, workspaceRoot → {WORKSPACE}, homeFolder → {HOME}; if absolute path does not match, → {PATH:.ext}.</remarks>
        private string SanitizePathInString(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return value;

            string sanitized = ReplaceIgnoreCase(value, ProjectRoot, "{PROJECT}");
            sanitized = ReplaceIgnoreCase(sanitized, WorkspaceRoot, "{WORKSPACE}");
            sanitized = ReplaceIgnoreCase(sanitized, HomeFolder, "{HOME}");
            sanitized = UserPathInValueRegex.Replace(sanitized, "%USERPROFILE%");
            sanitized = GdbPathInValueRegex.Replace(sanitized, "{GDB}");
            return AbsolutePathInValueRegex.Replace(sanitized, match =>
            {
                string candidate = match.Value.TrimEnd(' ', '.', ')', ']', '}');
                string suffix = match.Value[candidate.Length..];
                string ext = Path.GetExtension(candidate);
                string replacement = string.IsNullOrEmpty(ext)
                    ? "{PATH}"
                    : $"{{PATH:{ext.ToLowerInvariant()}}}";
                return replacement + suffix;
            });
        }

        private static string ReplaceIgnoreCase(string value, string? oldValue, string replacement)
        {
            if (string.IsNullOrWhiteSpace(oldValue))
                return value;
            return Regex.Replace(
                value,
                Regex.Escape(oldValue.TrimEnd('\\')),
                _ => replacement,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static readonly Regex UserPathInValueRegex = new(
            @"[A-Za-z]:\\Users\\[^\\\r\n\t""<>|,，;；]+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex GdbPathInValueRegex = new(
            @"[A-Za-z]:\\[^\r\n\t""<>|,，;；]*?\.gdb",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex AbsolutePathInValueRegex = new(
            @"(?<![A-Za-z0-9_])(?:[A-Za-z]:\\|\\\\)[^\r\n\t""<>|,，;；]+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// ★ 路径脱敏 Layer 1（2026-07-20）：
        /// 在序列化后的 JSON 字符串中替换 Windows 绝对路径中的用户/项目信息。
        /// 处理 JSON 中已转义的反斜杠（\\ → 正则匹配 \\\\）。
        ///
        /// 规则：
        ///   1. C:\Users\<用户名>\  →  %USERPROFILE%\
        ///   2. 盘符:\...\.gdb       →  {GDB}
        ///   3. UNC 路径             →  {UNC}
        ///
        /// Layer 2（后续）可改为预序列化递归清洗 + 可配置锚点。
        /// </summary>
        /// <remarks>English：Path Anonymization Layer 1 (2026-07-20): Replace user/project information in Windows absolute paths with serialized JSON strings. Handles escaped backslashes in JSON (\\ → regex matching \\\\). Rules: 1. C:\Users\ \ → %USERPROFILE%\ 2. Drive letter: \...\.gdb → {GDB} 3. UNC path→ {UNC} Layer 2 (later) can be changed to pre-serialized recursive cleaning + configurable anchors.</remarks>
        private string SerializeSanitized(object entry)
        {
            var serializer = JsonSerializer.Create(new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
                ReferenceLoopHandling = ReferenceLoopHandling.Ignore
            });
            JToken token = JToken.FromObject(entry, serializer);
            SanitizeToken(token);
            return token.ToString(Formatting.None);
        }

        private void SanitizeToken(JToken token)
        {
            if (token is JObject obj)
            {
                foreach (var property in obj.Properties().ToList())
                {
                    if (SensitiveParamKeys.Contains(property.Name))
                        property.Value = "[REDACTED]";
                    else
                        SanitizeToken(property.Value);
                }
                return;
            }
            if (token is JArray array)
            {
                foreach (var child in array)
                    SanitizeToken(child);
                return;
            }
            if (token is JValue value && value.Type == JTokenType.String)
                value.Value = SanitizePathInString(value.Value<string>() ?? "");
        }

        /// <summary>
        /// Move transcript to a new directory (called when project path becomes known).
        /// </summary>
        /// <remarks>中文：将转录文件移到新目录（项目路径已知时调用）。</remarks>
        public void MigrateTo(string newLogDir)
        {
            lock (_lock)
            {
                if (_writer == null || _filePath == null) return;

                string oldPath = _filePath;
                string currentDir = Path.GetDirectoryName(oldPath) ?? "";
                if (Path.GetFullPath(currentDir).Equals(
                        Path.GetFullPath(newLogDir),
                        StringComparison.OrdinalIgnoreCase))
                    return;

                // ★ P0-2：暂停消费者，flush 排空，然后迁移
                // English：P0-2: Pause the consumer, flush empty, then migrate
                _channel?.Writer.TryComplete();
                try { _consumerTask?.Wait(TimeSpan.FromSeconds(5)); } catch { }
                _writer.Flush();
                _writer.Dispose();

                Directory.CreateDirectory(newLogDir);
                string newPath = Path.Combine(newLogDir, Path.GetFileName(_filePath));

                if (File.Exists(oldPath))
                {
                    if (File.Exists(newPath))
                    {
                        // Merge: append old content to new file
                        // 中文：合并：将旧内容附加到新文件中
                        File.AppendAllText(newPath, File.ReadAllText(oldPath));
                        File.Delete(oldPath);
                    }
                    else
                    {
                        File.Move(oldPath, newPath);
                    }
                }

                _filePath = newPath;
                _writer = OpenWriter(_filePath);

                // ★ P0-2：重新创建 Channel 和消费者
                // English：P0-2: Recreate Channels and Consumers
                _channel = Channel.CreateBounded<object>(
                    new BoundedChannelOptions(10000)
                    {
                        FullMode = BoundedChannelFullMode.Wait
                    });
                _consumerCts?.Dispose();
                _consumerCts = new CancellationTokenSource();
                _consumerTask = Task.Run(() => ConsumeLoopAsync(_consumerCts.Token));

                WriteCritical(new { type = "log_migrated", ts = DateTime.Now.ToString("O"), path = _filePath });
            }
        }

        public void Close() => Dispose();

        private static StreamWriter OpenWriter(string path)
        {
            var stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite);
            return new StreamWriter(stream, Encoding.UTF8)
            {
                AutoFlush = false
            };
        }

        /// <summary>
        /// ★ P0-2 fix（2026-07-22）：关键事件（安全审批、诊断错误、会话边界）
        /// 绕过异步 Channel 直接同步落盘——这些事件不可丢，丢了就无法独立复盘审批链路。
        /// 安全事件频率极低（每会话 &lt; 100），同步 I/O 不影响性能。
        /// </summary>
        /// <remarks>English：P0-2 fix (2026-07-22): Key events (security approval, diagnostic errors, session boundaries) bypass asynchronous channels and synchronize directly—these events must not be lost; if lost, the approval chain cannot be independently reviewed. Security incidents are extremely low (&amp;lt; 100 per session), and synchronous I/O does not affect performance.</remarks>
        private void WriteCritical(object entry)
        {
            if (_writer == null) return;

            lock (_directWriteLock)
            {
                try
                {
                    object enriched = EnrichForWrite(entry);
                    string json = SerializeSanitized(enriched);
                    _writer.WriteLine(json);
                    _writer.Flush(); // 关键事件立即刷盘。 / Flush critical events to disk immediately.
                    IncrementEventCount();
                }
                catch (Exception ex)
                {
                    RecordLoss($"Critical write failed: {ex.GetType().Name} — {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Write any object as a JSONL line. No truncation.
        /// ★ P0-2（2026-07-20）：改为 Channel<T> 异步生产——
        /// 不再在调用线程上执行 StreamWriter I/O。
        /// Channel is non-blocking for producers; a full buffer rejects the new
        /// event so loss accounting remains exact and observable.
        /// ★ Phase A4（2026-07-20）：如果 entry 是匿名对象/Dictionary，
        /// 自动注入 CorrelationContext 的 run_id 和 span_id。
        /// </summary>
        /// <remarks>中文：可以把任意对象写成 JSONL 行。没有截断。P0-2（2026-07-20）：改为 Channel 异步生产—— 不再在调用线程上执行 StreamWriter I/O。频道对制片人来说是无阻塞的;满缓冲区会拒绝新事件，因此损失核算保持准确且可观察。Phase A4（2026-07-20）：如果 entry 是匿名对象/Dictionary， 自动注入 CorrelationContext 的 run_id 和 span_id。</remarks>
        public void Write(object entry)
        {
            if (_writer == null || _channel == null) return;

            // ★ Phase A4：自动注入关联 ID（仅在 entry 支持动态扩展时）
            // English：Phase A4: Automatically inject associated IDs (only when entry supports dynamic expansion)
            object enriched = EnrichForWrite(entry);

            // 非阻塞写入 Channel——Channel 满时 TryWrite 返回 false
            // English：Non-blocking write to the Channel—when the channel is full, TryWrite returns false
            if (!_channel.Writer.TryWrite(enriched))
            {
                // Channel 满了（消费者跟不上生产者）→ 丢弃此事件
                // English：Channels are full (consumers can't keep up with producers) → discard this event
                RecordLoss("Channel full — consumer too slow. Event dropped.");
            }
        }

        /// <summary>
        /// ★ Phase A4：如果 entry 是匿名类型（编译器生成的 DisplayClass），
        /// 尝试转为 Dictionary 并注入 run_id / span_id。
        /// 对于已经是 Dictionary 或 JObject 的对象也直接扩展。
        /// 对于基础类型或字符串，原样返回。
        /// </summary>
        /// <remarks>English：Phase A4: If entry is an anonymous type (DisplayClass generated by the compiler), try converting it to Dictionary and injecting run_id / span_id. Objects that are already Dictionary or JObject can also be extended directly. For base types or strings, return as is.</remarks>
        private static object EnrichWithCorrelation(object entry)
        {
            string runId = CorrelationContext.RunId;
            string spanId = CorrelationContext.SpanId;

            // 无活跃 run——不注入（早期初始化 / 测试场景）
            // English：Inactive run—no injection (early initialization / testing scenario)
            if (string.IsNullOrEmpty(runId) && string.IsNullOrEmpty(spanId))
                return entry;

            try
            {
                if (entry is Dictionary<string, object?> dict)
                {
                    if (!dict.ContainsKey("run_id") && !string.IsNullOrEmpty(runId))
                        dict["run_id"] = runId;
                    if (!dict.ContainsKey("span_id") && !string.IsNullOrEmpty(spanId))
                        dict["span_id"] = spanId;
                    return dict;
                }

                // 匿名对象 → 序列化为 JObject → 附加字段
                // English：Anonymous object → serialized as JObject → additional fields
                string json = JsonConvert.SerializeObject(entry, Formatting.None,
                    new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
                var jobj = JsonConvert.DeserializeObject<JObject>(json);
                if (jobj != null)
                {
                    if (!string.IsNullOrEmpty(runId)) jobj["run_id"] = runId;
                    if (!string.IsNullOrEmpty(spanId)) jobj["span_id"] = spanId;
                    return jobj;
                }
            }
            catch
            {
                // 注入失败不影响事件写入——用原始 entry
                // English：Injection failure does not affect event write—using the original entry
            }

            return entry;
        }

        private object EnrichForWrite(object entry)
        {
            object enriched = EnrichWithCorrelation(entry);
            long sequence = Interlocked.Increment(ref _submissionSequence);
            try
            {
                if (enriched is JObject jobj)
                {
                    jobj["event_seq"] = sequence;
                    return jobj;
                }
                if (enriched is Dictionary<string, object?> dict)
                {
                    dict["event_seq"] = sequence;
                    return dict;
                }

                string json = JsonConvert.SerializeObject(
                    enriched,
                    Formatting.None,
                    new JsonSerializerSettings
                    {
                        NullValueHandling = NullValueHandling.Ignore
                    });
                var converted = JObject.Parse(json);
                converted["event_seq"] = sequence;
                return converted;
            }
            catch
            {
                return enriched;
            }
        }

        /// <summary>
        /// Write a raw JSONL line (for pre-serialized content).
        /// </summary>
        /// <remarks>中文：写一行原始的 JSONL（用于预序列化内容）。</remarks>
        public void WriteLine(object entry)
        {
            Write(entry);
        }

        /// <summary>
        /// Convert an AgentEvent to transcript entry and write it.
        /// </summary>
        /// <remarks>中文：把AgentEvent转换成文字记录并写入。</remarks>
        public void WriteEvent(AgentEvent evt)
        {
            var entry = new Dictionary<string, object?>
            {
                ["type"] = evt.EventType,
                ["ts"] = evt.Timestamp.ToString("HH:mm:ss.fff"),
                ["session"] = evt.SessionId
            };

            if (evt.Turn.HasValue)
                entry["turn"] = evt.Turn.Value;

            // Flatten metadata into entry
            // 中文：将元数据扁平化为条目
            if (evt.Metadata != null)
            {
                foreach (var kv in evt.Metadata)
                    entry[kv.Key] = kv.Value;
            }

            // Attach structured data
            // 中文：附加结构化数据
            if (evt.Data != null)
            {
                foreach (var kv in evt.Data)
                    entry[kv.Key] = kv.Value;
            }

            Write(entry);
        }

        public void Flush()
        {
            // ★ P0-2 fix（2026-07-22）：排空 Channel → 等待消费者完成 → 明确成功或失败。
            // 关键事件已通过 WriteCritical 同步落盘，Channel 中仅剩非关键事件。
            // 固定 500ms 超时改为更长超时（5s），并在超时时记录诊断告警。
            // English：P0-2 fix (2026-07-22): Empty the channel → wait for consumers to complete → clarify success or failure. Critical events have been synchronously executed via WriteCritical to determine the order, leaving only non-critical events in the Channel. The fixed 500ms timeout is changed to a longer timeout (5 seconds), and diagnostic alerts are recorded during the timeout.
            int remaining = _channel?.Reader.Count ?? 0;
            try
            {
                _channel?.Writer.TryComplete();
                if (_consumerTask != null && !_consumerTask.IsCompleted)
                {
                    bool completed = _consumerTask.Wait(TimeSpan.FromSeconds(5));
                    if (!completed)
                    {
                        AgentDiagnostics.Warn("TranscriptWriter.Flush",
                            $"排空 Channel 超时（剩余 {_channel?.Reader.Count ?? -1} 条未处理事件）。" +
                            "消费者可能被阻塞——非关键事件可能丢失。");
                    }
                }
            }
            catch (Exception ex)
            {
                AgentDiagnostics.Warn("TranscriptWriter.Flush",
                    $"排空 Channel 异常：{ex.GetType().Name} — {ex.Message}");
            }

            // 重新创建 Channel 和消费者——允许后续写入
            // English：Recreate Channels and consumers—allowing subsequent writes
            lock (_lock)
            {
                if (_writer != null && !_disposed)
                {
                    _channel = Channel.CreateBounded<object>(
                        new BoundedChannelOptions(10000)
                        {
                            FullMode = BoundedChannelFullMode.Wait
                        });
                    _consumerCts?.Dispose();
                    _consumerCts = new CancellationTokenSource();
                    _consumerTask = Task.Run(() => ConsumeLoopAsync(_consumerCts.Token));
                }
            }

            try { _writer?.Flush(); } catch { }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;

                // Drain all previously accepted events first so log_closed is
                // physically the final lifecycle record, not merely the final
                // record submitted by a producer.
                // 中文：先提取所有先前接受的事件，使log_closed物理上成为最终生命周期记录，而不仅仅是制作者提交的最终记录。
                try { Flush(); }
                catch { }

                try
                {
                    WriteCritical(new { type = "log_closed", ts = DateTime.Now.ToString("O"), total_events = EventCount });
                }
                catch { }

                // 停止刚重建的 Channel 消费者
                // English：Stop the newly rebuilt Channel consumers
                try
                {
                    _channel?.Writer.TryComplete();
                    _consumerCts?.Cancel();
                    try { _consumerTask?.Wait(TimeSpan.FromSeconds(3)); }
                    catch (AggregateException) { }
                    _consumerCts?.Dispose();
                }
                catch { }

                try
                {
                    _writer?.Flush();
                    _writer?.Dispose();
                }
                catch { }
            }
        }

        // ================================================================
        // Convenience methods for structured entries
        // ================================================================
        // 中文：================================================================ 结构化条目的便利方法 ================================================================

        /// <summary>Record session start with model info.</summary>
        /// <remarks>中文：录制会话开始时有模型信息。</remarks>
        public void WriteSessionStart(string sessionId, string model, string thinkingEffort, string? projectPath)
        {
            WriteCritical(new
            {
                type = "session_start",
                ts = DateTime.Now.ToString("O"),
                session = sessionId,
                model,
                thinking = thinkingEffort,
                project = projectPath ?? "unknown"
            });
        }

        public void WriteSessionResume(string sessionId, string model, string thinkingEffort, string? projectPath)
        {
            WriteCritical(new
            {
                type = "session_resumed",
                ts = DateTime.Now.ToString("O"),
                session = sessionId,
                model,
                thinking_effort = thinkingEffort,
                project_path = projectPath
            });
        }

        public void WriteHostShutdown(
            string sessionId,
            string reason,
            SessionStatus status,
            bool hadActiveRun)
        {
            WriteCritical(new
            {
                type = "host_shutdown",
                ts = DateTime.Now.ToString("O"),
                session = sessionId,
                reason,
                session_status = status.ToString(),
                had_active_run = hadActiveRun
            });
        }

        public void WriteHostShutdownObserved(
            string sessionId,
            string reason,
            SessionStatus status,
            bool hadActiveRun)
        {
            WriteCritical(new
            {
                type = "host_shutdown_observed",
                ts = DateTime.Now.ToString("O"),
                session = sessionId,
                reason,
                session_status = status.ToString(),
                had_active_run = hadActiveRun
            });
        }

        public void WriteStaleSessionRecovered(
            string staleSessionId,
            string leaseId,
            DateTime lastHeartbeatAtUtc,
            DateTime recoveredAtUtc,
            SessionStatus status,
            bool checkpointTerminalized)
        {
            WriteCritical(new
            {
                type = "stale_session_recovered",
                ts = recoveredAtUtc.ToString("O"),
                stale_session = staleSessionId,
                lease_id = leaseId,
                last_heartbeat_at = lastHeartbeatAtUtc.ToString("O"),
                terminal_status = status.ToString(),
                checkpoint_terminalized = checkpointTerminalized,
                normal_session_end_synthesized = false
            });
        }

        /// <summary>Record a model / thinking-effort switch (provider-actual values).</summary>
        /// <remarks>中文：记录一个模型/思维-努力的切换（提供者-实际值）。</remarks>
        public void WriteModelSwitch(string reason, string model, string thinking)
        {
            Write(new
            {
                type = "model_switched",
                ts = DateTime.Now.ToString("O"),
                reason,
                model,
                thinking
            });
        }

        /// <summary>Record user message (complete, untruncated).</summary>
        /// <remarks>中文：录制用户消息（完整且未截断）。</remarks>
        public void WriteUserMessage(int turn, string content)
        {
            WriteCritical(new { type = "user_msg", turn, ts = DateTime.Now.ToString("HH:mm:ss.fff"), content });
        }

        /// <summary>
        /// Record context gathered for this turn.
        /// ★ Phase A5（2026-07-20）：不再截断 project/map 信息。
        /// 全量写入 + 附带截断标注标记（供分析脚本快速过滤）。
        /// </summary>
        /// <remarks>English：Record context gathered for this turn. Phase A5 (2026-07-20): No longer truncating project/map information. Full write + includes truncated annotation (for quick filtering by analysis scripts).</remarks>
        public void WriteContext(int turn, string projectInfo, string mapInfo, int ragHits, int skillsMatched,
            Dictionary<string, object>? budgetCuts = null)
        {
            const int projectHardLimit = 500;
            const int mapHardLimit = 1000;

            bool projectTruncated = projectInfo?.Length > projectHardLimit;
            bool mapTruncated = mapInfo?.Length > mapHardLimit;

            Write(new
            {
                type = "context_gathered",
                turn,
                ts = DateTime.Now.ToString("HH:mm:ss.fff"),
                project = projectInfo ?? "",
                project_len = projectInfo?.Length ?? 0,
                project_truncated = projectTruncated,
                map = mapInfo ?? "",
                map_len = mapInfo?.Length ?? 0,
                map_truncated = mapTruncated,
                rag_hits = ragHits,
                skills_matched = skillsMatched,
                budget_cuts = budgetCuts ?? new Dictionary<string, object>()
            });
        }

        /// <summary>
        /// ★ Phase C2（2026-07-20）：记录每轮工具暴露决策。
        /// 回答"模型当时能调什么工具"——排查"为什么没调 X 工具"的关键信息。
        /// </summary>
        /// <remarks>English：Phase C2 (2026-07-20): Records each round of tool exposure decisions. Answer "What tools could the model use at the time?" — Identify key information about "Why didn't you use tool X?"</remarks>
        public void WriteToolExposure(int turn, int tier1Count, int tier2Count, int alwaysOnCount,
            List<string> tier1Tools, List<string>? chainPredicted, string? chainSource)
        {
            Write(new
            {
                type = "tool_exposure",
                turn,
                ts = DateTime.Now.ToString("HH:mm:ss.fff"),
                tier1_count = tier1Count,
                tier2_count = tier2Count,
                always_on_count = alwaysOnCount,
                // always-on tools are already part of tier 1.
                // 中文：始终在线的工具已经是第一层保护的一部分。
                total_exposed = tier1Count + tier2Count,
                tier1_tools = tier1Tools,
                chain_predicted = chainPredicted ?? new List<string>(),
                chain_source = chainSource ?? ""
            });
        }

        /// <summary>Record LLM request (complete messages, no truncation).
        /// model/thinking are the provider's ACTUAL request parameters — link-level
        /// evidence that a UI model switch took effect, not just UI state.
        /// Repeated tool schemas are stored once and referenced by content hash.</summary>
        /// <remarks>中文：记录LLM请求（完整消息，无截断）。模型/思维是提供者实际的请求参数——链接层面的证据，表明UI模型切换生效，而不仅仅是UI状态。重复的工具模式存储一次，并通过内容哈希引用。</remarks>
        public void WriteLLMRequest(int turn, object messages, object? tools, string systemPromptHash, string model = "", string thinking = "")
        {
            string? toolsHash = null;
            bool toolsReused = false;
            object? toolsPayload = tools;
            if (tools != null)
            {
                string schemaJson = JsonConvert.SerializeObject(
                    tools,
                    Formatting.None);
                toolsHash = Convert.ToHexString(
                        SHA256.HashData(Encoding.UTF8.GetBytes(schemaJson)))
                    .ToLowerInvariant();
                lock (_lock)
                    toolsReused = !_loggedToolSchemaHashes.Add(toolsHash);
                if (toolsReused)
                    toolsPayload = null;
            }

            Write(new
            {
                type = "llm_request",
                turn,
                ts = DateTime.Now.ToString("HH:mm:ss.fff"),
                model = string.IsNullOrEmpty(model) ? null : model,
                thinking = string.IsNullOrEmpty(thinking) ? null : thinking,
                messages,
                tools = toolsPayload,
                tools_hash = toolsHash,
                tools_reused = toolsReused,
                system_prompt_hash = systemPromptHash
            });
        }

        /// <summary>Record LLM response (complete tool calls + token usage, no truncation).</summary>
        /// <remarks>中文：记录LLM响应（完成工具调用+令牌使用，不截断）。</remarks>
        public void WriteLLMResponse(int turn, object toolCalls, string? textContent,
            long elapsedMs, int promptTokens = 0, int completionTokens = 0,
            string finishReason = "", int recoveryAttempt = 0)
        {
            WriteCritical(new
            {
                type = "llm_response",
                turn,
                ts = DateTime.Now.ToString("HH:mm:ss.fff"),
                tool_calls = toolCalls,
                text = textContent,
                elapsed_ms = elapsedMs,
                prompt_tokens = promptTokens > 0 ? promptTokens : (int?)null,
                completion_tokens = completionTokens > 0 ? completionTokens : (int?)null,
                finish_reason = string.IsNullOrWhiteSpace(finishReason) ? null : finishReason,
                recovery_attempt = recoveryAttempt
            });
        }

        /// <summary>Record tool execution start.</summary>
        /// <remarks>中文：记录工具执行开始。</remarks>
        public void WriteToolStart(int turn, int step, string toolName, Dictionary<string, object?> args)
        {
            WriteCritical(new
            {
                type = "tool_start",
                turn,
                step,
                ts = DateTime.Now.ToString("HH:mm:ss.fff"),
                tool = toolName,
                args
            });
        }

        /// <summary>Record tool execution result.</summary>
        /// <remarks>中文：记录工具执行结果。</remarks>
        public void WriteToolEnd(int turn, int step, string toolName, bool success,
            object? output, string? error, long elapsedMs, int retryCount)
        {
            WriteCritical(new
            {
                type = "tool_end",
                turn,
                step,
                ts = DateTime.Now.ToString("HH:mm:ss.fff"),
                tool = toolName,
                success,
                output,
                error,
                elapsed_ms = elapsedMs,
                retry = retryCount
            });
        }

        /// <summary>Record verification result with expected/actual diff.</summary>
        /// <remarks>中文：记录带有预期/实际差异的验证结果。</remarks>
        public void WriteVerification(int turn, int step, string toolName,
            string ruleType, string target, string expected, string actual,
            string verdict, object? sample, string detail)
        {
            Write(new
            {
                type = "verification",
                turn,
                step,
                ts = DateTime.Now.ToString("HH:mm:ss.fff"),
                tool = toolName,
                rule = ruleType,
                target,
                expected,
                actual,
                verdict,
                sample,
                detail
            });
        }

        /// <summary>Record what data was passed to Synthesis.</summary>
        /// <remarks>中文：记录传递给Synthesis的数据。</remarks>
        public void WriteSynthInput(int turn, object observations, bool hasFailures, List<string>? rulesInjected)
        {
            Write(new
            {
                type = "synth_input",
                turn,
                ts = DateTime.Now.ToString("HH:mm:ss.fff"),
                observations,
                has_failures = hasFailures,
                rules_injected = rulesInjected ?? new List<string>()
            });
        }

        /// <summary>Record Synthesis output.</summary>
        /// <remarks>中文：录制合成输出。</remarks>
        public void WriteSynthOutput(int turn, string reply, long elapsedMs)
        {
            WriteCritical(new
            {
                type = "synth_output",
                turn,
                ts = DateTime.Now.ToString("HH:mm:ss.fff"),
                reply,
                elapsed_ms = elapsedMs
            });
        }

        /// <summary>Record session end with summary statistics.</summary>
        /// <remarks>中文：记录会话结束时，附有统计数据摘要。</remarks>
        public void WriteSessionEnd(int totalTurns, int toolCalls, int toolFailures,
            int verificationFailures, long totalElapsedMs)
        {
            WriteSessionEnd(
                "",
                totalTurns,
                toolCalls,
                toolFailures,
                verificationFailures,
                totalElapsedMs);
        }

        public void WriteSessionEnd(string sessionId, int totalTurns, int toolCalls, int toolFailures,
            int verificationFailures, long totalElapsedMs)
        {
            // ★ Phase B5：注入日志健康快照
            // English：Phase B5: Inject log health snapshots
            LoggingHealthSnapshot? health = _healthMonitor?.Snapshot();

            WriteCritical(new
            {
                type = "session_end",
                ts = DateTime.Now.ToString("O"),
                session = sessionId,
                total_turns = totalTurns,
                tool_calls = toolCalls,
                tool_failures = toolFailures,
                verification_failures = verificationFailures,
                total_elapsed_ms = totalElapsedMs,
                // ★ P0-2：session 结束时自报审计完整性
                // English：P0-2: Self-reported audit completeness at session end
                transcript_integrity = new
                {
                    total_events = _eventCountBacking,
                    lost_events = Interlocked.Read(ref _lostEventCount),
                    first_loss_at = _firstLossAt?.ToString("O"),
                    first_loss_reason = _firstLossReason
                },
                // ★ Phase B5：四层日志健康快照
                // English：Phase B5: Four-layer log health snapshot
                logging_health = health != null ? new
                {
                    all_healthy = health.AllHealthy,
                    fault_summary = health.FaultSummary(),
                    transcript = new { ok = health.TranscriptOk, lost_events = health.TranscriptLostEvents, first_loss_at = health.TranscriptFirstLossAt, last_error = health.TranscriptLastError },
                    ledger = new { ok = health.LedgerOk, write_failures = health.LedgerWriteFailures, total_checks = health.LedgerTotalChecks, passed = health.LedgerPassedChecks, failed = health.LedgerFailedChecks },
                    snapshot = new { ok = health.SnapshotOk, write_failures = health.SnapshotWriteFailures, snapshot_count = health.SnapshotCount },
                    audit = new { ok = health.AuditOk, write_failures = health.AuditWriteFailures, entry_count = health.AuditEntryCount }
                } : null
            });
            // ★ P1-C：session 结束前校验 EventBus→Transcript 事件一致性
            // English：P1-C: Verify EventBus→Transcript event consistency before session ends
            CheckConsistency();
            Flush();
        }
    }
}
