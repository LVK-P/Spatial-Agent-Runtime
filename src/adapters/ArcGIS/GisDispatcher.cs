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
namespace SpatialAgent.Adapters.ArcGIS
{
    /// <summary>
    /// ArcGIS Pro implementation of IGisDispatcher.
    /// All GIS data access flows through this single entry point.
    /// </summary>
    public class ProGisDispatcher : IGisDispatcher
    {
        public async Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Direct delegate return — no async lambda.
                // QueuedTask.Run<T>(Func<Task<T>>) resolves correctly.
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
    public class GisOperationException : Exception
    {
        public GisOperationException(string message, Exception? inner = null)
            : base(message, inner) { }
    }
}
