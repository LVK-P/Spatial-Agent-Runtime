using System.Collections.Generic;

namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// ★ ADR 0056 / Phase 4B：Transcript 工具执行记录接口。
    /// 从 ITranscriptWriter 拆分——记录工具执行、验证和合成输入/输出。
    /// </summary>
    /// <remarks>English：ADR 0056 / Phase 4B: Transcript tool executes the recording interface. Split from ITranscriptWriter—record tool execution, validation, and synthesis of input/output.</remarks>
    public interface ITranscriptToolWriter
    {
        void WriteToolStart(int turn, int step, string toolName, Dictionary<string, object?> args);
        void WriteToolEnd(int turn, int step, string toolName, bool success, object? output, string? error, long elapsedMs, int retryCount);
        void WriteVerification(int turn, int step, string toolName, string ruleType, string target, string expected, string actual, string verdict, object? sample, string detail);
        void WriteSynthInput(int turn, object observations, bool hasFailures, List<string>? rulesInjected);
        void WriteSynthOutput(int turn, string reply, long elapsedMs);
    }
}
