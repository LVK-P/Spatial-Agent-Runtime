using System;
using System.Threading;
using System.Threading.Tasks;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using SpatialAgent.Runtime.Contracts;

// ================================================================
// ProGisDispatcher.cs — ArcGIS Pro MCT 线程调度器实现
//
// 将 IGisDispatcher 抽象落实到 ArcGIS Pro 的 QueuedTask.Run()。
// 含统一异常包装（GisOperationException）。
//
// Pro SDK 3.4 约束：
// - QueuedTask.Run() 不接受 CancellationToken（第二个参数是 Progressor）
// - 取消依赖 Task 级别的超时（Scheduler 创建 Linked CTS，超时后
//   Task 被取消，但 GP 工具可能继续在后台运行——这是 Pro SDK 固有限制）
// - lambda 不使用 async/await，直接返回 operation() 的 Task，
//   避免 async 状态机干扰 QueuedTask.Run 的重载解析
//
// 来源：跨实现架构审查后的宿主调度边界。
// ================================================================
// English：================================================================ ProGisDispatcher.cs — ArcGIS Pro MCT thread scheduler implementation: Abstracts IGisDispatcher into ArcGIS Pro's QueuedTask.Run(). Includes Uniform Exception Wrapping (GisOperationException). Pro SDK 3.4 constraints: - QueuedTask.Run() does not accept CancellationTokens (the second parameter is Progressor) - Removes dependency task-level timeout (Scheduler creates linked CTS; after timeout, the task is canceled, but GP tools may continue running in the background—this is an inherent limitation of Pro SDK) - lambda does not use async/await, Directly returns operation() to the Task, preventing async state machine interference with QueuedTask.Run overload parsing. Source: Host scheduling boundary after implementation architecture review. ================================================================
namespace SpatialAgent.Adapters.ArcGIS
{
    /// <summary>
    /// ArcGIS Pro implementation of IGisDispatcher.
    /// All GIS data access flows through this single entry point.
    /// </summary>
    /// <remarks>中文：ArcGIS Pro 实现了 IGisDispatcher。所有GIS数据访问都通过这个单一入口。</remarks>
    public class ProGisDispatcher : IGisDispatcher
    {
        public async Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Direct delegate return — no async lambda.
                // QueuedTask.Run<T>(Func<Task<T>>) resolves correctly.
                // 中文：直接代理返回——无异步lambda。QueuedTask.Run （Func >） 能正确解析。
                return await QueuedTask.Run(() => operation());
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new GisOperationException(
                    $"GIS operation failed: {ex.Message}", ex);
            }
        }

        public async Task RunAsync(Func<Task> operation, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // QueuedTask.Run(Func<Task>) — void-returning overload.
                // 中文：QueuedTask.Run（Func ） — void-returned overload。
                await QueuedTask.Run(() => operation());
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new GisOperationException(
                    $"GIS operation failed: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// Unified GIS operation exception for consistent error handling.
    /// </summary>
    /// <remarks>中文：统一的GIS操作异常，用于一致性的错误处理。</remarks>
    public class GisOperationException : Exception
    {
        public GisOperationException(string message, Exception? inner = null)
            : base(message, inner) { }
    }
}
