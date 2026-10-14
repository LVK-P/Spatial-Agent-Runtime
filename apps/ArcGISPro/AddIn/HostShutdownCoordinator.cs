using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using ArcGIS.Desktop.Framework;

namespace ArcGISProAIAgent
{
    /// <summary>
    /// Process-level two-phase shutdown barrier.
    /// 进程级两阶段关闭屏障。
    ///
    /// A close observation writes a reversible recovery checkpoint. An
    /// irreversible signal terminalizes the session and closes log streams.
    /// The main Pro window is attached lazily as well as at module startup:
    /// FrameworkApplication.MainWindow is commonly null during add-in
    /// initialization.
    /// 观察到关闭请求时写入可逆的恢复检查点；收到不可逆信号时终结会话并关闭日志流。
    /// 除模块启动时尝试挂接外，还会延迟挂接 Pro 主窗口，因为插件初始化期间
    /// FrameworkApplication.MainWindow 通常为空。
    /// </summary>
    internal sealed class HostShutdownCoordinator : IDisposable
    {
        private const int WmClose = 0x0010;
        private const int WmQueryEndSession = 0x0011;
        private const int WmEndSession = 0x0016;

        private readonly Action<string> _prepare;
        private readonly Action<string> _finalize;
        private readonly string _diagnosticPath;
        private int _signaled;
        private int _processHookAttached;
        private Window? _mainWindow;
        private HwndSource? _windowSource;

        public HostShutdownCoordinator(
            Action<string> prepare,
            Action<string> finalize)
        {
            _prepare = prepare ?? throw new ArgumentNullException(nameof(prepare));
            _finalize = finalize ?? throw new ArgumentNullException(nameof(finalize));
            _diagnosticPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "ai_agent_shutdown.log");
        }

        public void Attach()
        {
            if (Interlocked.Exchange(ref _processHookAttached, 1) == 0)
                AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            RefreshHostWindow("ModuleInitialize");
        }

        public void RefreshHostWindow(string source)
        {
            Window? candidate = FrameworkApplication.Current?.MainWindow;
            if (ReferenceEquals(candidate, _mainWindow))
            {
                TryAttachNativeHook();
                Trace($"main_window:unchanged:{source}:available={candidate != null}");
                return;
            }

            DetachHostWindow();
            _mainWindow = candidate;
            if (_mainWindow == null)
            {
                Trace($"main_window:missing:{source}");
                return;
            }

            _mainWindow.SourceInitialized += OnMainWindowSourceInitialized;
            _mainWindow.Closing += OnMainWindowClosing;
            _mainWindow.Closed += OnMainWindowClosed;
            TryAttachNativeHook();
            Trace($"main_window:attached:{source}:handle={GetHandle()}");
        }

        public void Prepare(string source)
        {
            Trace($"prepare:{source}");
            try
            {
                _prepare(source);
                Trace($"prepared:{source}");
            }
            catch (Exception ex)
            {
                Trace($"prepare_failed:{source}:{ex.GetType().Name}:{ex.Message}");
            }
        }

        public void Signal(string source)
        {
            Trace($"signal:{source}");
            if (Interlocked.Exchange(ref _signaled, 1) != 0)
                return;
            try
            {
                _finalize(source);
                Trace($"finalized:{source}");
            }
            catch (Exception ex)
            {
                Trace($"finalize_failed:{source}:{ex.GetType().Name}:{ex.Message}");
            }
        }

        public void ObserveCancelable(string source, CancelEventArgs args)
        {
            Trace($"observed:{source}:cancel={args.Cancel}");
            if (!args.Cancel)
                Prepare(source);
        }

        private void OnMainWindowSourceInitialized(object? sender, EventArgs args)
        {
            TryAttachNativeHook();
            Trace($"main_window:source_initialized:handle={GetHandle()}");
        }

        private void OnMainWindowClosing(object? sender, CancelEventArgs args) =>
            ObserveCancelable("MainWindowClosing", args);

        private void OnMainWindowClosed(object? sender, EventArgs args) =>
            Signal("MainWindowClosed");

        private void OnProcessExit(object? sender, EventArgs args) =>
            Signal("ProcessExit");

        private IntPtr NativeWindowHook(
            IntPtr hwnd,
            int message,
            IntPtr wParam,
            IntPtr lParam,
            ref bool handled)
        {
            switch (message)
            {
                case WmClose:
                    Prepare("WM_CLOSE");
                    break;
                case WmQueryEndSession:
                    Prepare("WM_QUERYENDSESSION");
                    break;
                case WmEndSession when wParam != IntPtr.Zero:
                    Signal("WM_ENDSESSION");
                    break;
            }
            return IntPtr.Zero;
        }

        private void TryAttachNativeHook()
        {
            if (_mainWindow == null || _windowSource != null)
                return;
            IntPtr handle = new WindowInteropHelper(_mainWindow).Handle;
            if (handle == IntPtr.Zero)
                return;
            _windowSource = HwndSource.FromHwnd(handle);
            _windowSource?.AddHook(NativeWindowHook);
        }

        private string GetHandle()
        {
            try
            {
                return _mainWindow == null
                    ? "none"
                    : new WindowInteropHelper(_mainWindow).Handle.ToString();
            }
            catch
            {
                return "error";
            }
        }

        private void DetachHostWindow()
        {
            if (_windowSource != null)
            {
                try { _windowSource.RemoveHook(NativeWindowHook); }
                catch { }
                _windowSource = null;
            }
            if (_mainWindow != null)
            {
                _mainWindow.SourceInitialized -= OnMainWindowSourceInitialized;
                _mainWindow.Closing -= OnMainWindowClosing;
                _mainWindow.Closed -= OnMainWindowClosed;
                _mainWindow = null;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _processHookAttached, 0) != 0)
                AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
            DetachHostWindow();
            Trace("disposed");
        }

        private void Trace(string message)
        {
            try
            {
                File.AppendAllText(
                    _diagnosticPath,
                    $"[{DateTime.Now:O}] pid={Environment.ProcessId} {message}{Environment.NewLine}");
            }
            catch
            {
            }
        }
    }
}
