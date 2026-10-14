using System;
using System.IO;
using System.Windows;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;
using ArcGISProAIAgent.ViewModels;
using ArcGISProAIAgent.Views;

namespace ArcGISProAIAgent.Buttons
{
    internal class ShowAgentButton : Button
    {
        private AgentWindow? _window;
        // ★ 静态 ViewModel —— 窗口关闭后不销毁，下次打开恢复历史消息。
        // ★ Static ViewModel — preserve it after the window closes so the next opening restores message history.
        private static AgentWindowViewModel? _sharedViewModel;

        internal static void PrepareSharedSessionForHostShutdown(
            string reason = "ArcGISProClosing")
        {
            _sharedViewModel?.PrepareForHostShutdown(reason);
        }

        internal static void FinalizeSharedSessionForHostShutdown(
            string reason = "ArcGISProClosing")
        {
            _sharedViewModel?.FinalizeForHostShutdown(reason);
        }

        private static readonly string LOG_FILE = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "ai_agent_log.txt");

        private void Log(string msg)
        {
            try
            {
                File.AppendAllText(LOG_FILE,
                    $"[{DateTime.Now:HH:mm:ss}] {msg}\n");
            }
            catch { }
        }

        protected override void OnClick()
        {
            Log("=== OnClick START ===");
            try
            {
                if (_window != null)
                {
                    // ★ 窗口可能处于异步关闭周期（Closing→cancel→SaveSession→Close），
                    // IsVisible=false 表示窗口已销毁但引用尚未被 Closed 事件清空。
                    // 此时应丢弃旧引用并创建新窗口，而不是调用 Activate() 导致崩溃。
                    // ★ The window may be in its asynchronous closing cycle (Closing→cancel→SaveSession→Close).
                    // IsVisible=false means the window is gone but the Closed event has not cleared its reference yet.
                    // Discard the stale reference and create a new window instead of calling Activate(), which would crash.
                    if (_window.IsVisible)
                    {
                        Log("Window already exists, activating");
                        _window.Activate();
                        return;
                    }
                    Log("Window reference exists but IsVisible=false — clearing stale reference");
                    _window = null;
                }

                Log("Creating AgentWindow...");

                // ★ 复用 ViewModel：首次创建，后续窗口关闭后保留。
                // ★ Reuse the ViewModel: create it on first use and retain it after later window closures.
                if (_sharedViewModel == null)
                {
                    Log("First launch — creating shared ViewModel");
                    _sharedViewModel = new AgentWindowViewModel();
                }
                else
                {
                    Log($"Reusing shared ViewModel ({_sharedViewModel.TimelineItems.Count} items)");
                }

                var viewModel = _sharedViewModel!;
                _window = new AgentWindow(viewModel);
                Log($"AgentWindow created");

                _window.Owner = FrameworkApplication.Current.MainWindow;
                // Module initialization can precede creation of Pro's WPF main
                // window. The first panel open is a reliable late-attach point.
                // 模块初始化可能早于 Pro 的 WPF 主窗口创建；首次打开面板是可靠的延迟挂接时机。
                Module1.Current.RefreshShutdownHooks("AgentWindowOpened");

                // 窗口关闭时只清除窗口引用，不销毁 ViewModel。
                // Clear only the window reference when it closes; do not destroy the ViewModel.
                _window.Closed += (_, _) =>
                {
                    Log($"Window closed — ViewModel preserved ({viewModel.TimelineItems.Count} items)");
                    _window = null;
                };

                Log("Calling Show()...");
                _window.Show();
                Log($"Show() completed. IsVisible={_window.IsVisible}");
            }
            catch (Exception ex)
            {
                Log($"EXCEPTION: {ex.GetType().Name}: {ex.Message}");
                Log($"Stack: {ex.StackTrace}");
                try
                {
                    MessageBox.Show($"AI Agent 启动失败：\n{ex.Message}", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch (Exception ex2)
                {
                    Log($"MessageBox also failed: {ex2.Message}");
                }
            }
            Log("=== OnClick END ===");
        }
    }
}
