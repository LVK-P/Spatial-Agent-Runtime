using System.Diagnostics;
using SpatialAgent.Runtime.Contracts;

namespace SpatialAgent.Core
{
    /// <summary>
    /// Lightweight diagnostics helper shared across layers.
    ///
    /// ★ Phase A2（2026-07-20）：所有级别自动附加堆栈跟踪。
    /// Warn/Error 在 ex 为 null 时捕获当前调用栈（Environment.StackTrace），
    /// 确保开发者可以定位到 throw 或 Warn 调用的源位置。
    /// </summary>
    public static class AgentDiagnostics
    {
        /// <summary>Extract stack trace from exception or capture current call stack.</summary>
        private static string CaptureStack(Exception? ex)
        {
            string stack = ex?.StackTrace ?? Environment.StackTrace;
            // Truncate to last 40 frames — full stack is rarely needed and bloats logs
            var lines = stack.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length <= 40) return stack;
            return string.Join("\n", lines[..40]) + $"\n... ({lines.Length - 40} more frames)";
        }

        public static void Warn(string area, string message, Exception? ex = null, IEventBus? eventBus = null)
        {
            string detail = ex == null
                ? message
                : $"{message} | {ex.GetType().Name}: {ex.Message}";

            Debug.WriteLine($"[Agent][WARN][{area}] {detail}");

            eventBus?.Publish(new AgentEvent
            {
                EventType = "diagnostics.warning",
                Metadata = new()
                {
                    ["area"] = area,
                    ["message"] = message,
                    ["exception"] = ex?.GetType().FullName ?? "",
                    ["error"] = ex?.Message ?? "",
                    ["stacktrace"] = CaptureStack(ex)
                }
            });
        }

        /// <summary>
        /// ★ P0-2 修复（2026-07-20）：Error 级别诊断——表示能力降级或数据丢失，
        /// 需要运维关注的严重事件。比 Warn 更高级别。
        /// ★ Phase A2（2026-07-20）：附加完整异常类型名（含命名空间）和堆栈。
        /// </summary>
        public static void Error(string area, string message, Exception? ex = null, IEventBus? eventBus = null)
        {
            string detail = ex == null
                ? message
                : $"{message} | {ex.GetType().Name}: {ex.Message}";

            Debug.WriteLine($"[Agent][ERROR][{area}] {detail}");

            eventBus?.Publish(new AgentEvent
            {
                EventType = "diagnostics.error",
                Metadata = new()
                {
                    ["area"] = area,
                    ["message"] = message,
                    ["exception"] = ex?.GetType().FullName ?? "",
                    ["error"] = ex?.Message ?? "",
                    ["stacktrace"] = CaptureStack(ex),
                    ["hresult"] = (ex as System.Runtime.InteropServices.ExternalException)?.ErrorCode.ToString("X8") ?? ""
                }
            });
        }

        public static void Info(string area, string message, IEventBus? eventBus = null)
        {
            Debug.WriteLine($"[Agent][INFO][{area}] {message}");

            eventBus?.Publish(new AgentEvent
            {
                EventType = "diagnostics.info",
                Metadata = new()
                {
                    ["area"] = area,
                    ["message"] = message
                }
            });
        }
    }
}
