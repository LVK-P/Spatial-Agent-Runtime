using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ArcGISProAIAgent.ViewModels;

namespace ArcGISProAIAgent.Views
{
    public partial class AgentWindow : Window
    {
        private readonly AgentWindowViewModel _vm;

        public AgentWindow() : this(new AgentWindowViewModel()) { }

        internal AgentWindow(AgentWindowViewModel viewModel)
        {
            InitializeComponent();

            _vm = viewModel;
            DataContext = _vm;

            Action scrollToBottom = () =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    ChatScrollViewer.ScrollToBottom();
                }), System.Windows.Threading.DispatcherPriority.Background);
            };
            _vm.OnNewMessageAdded += scrollToBottom;

            Loaded += (_, _) => { InputTextBox.Focus(); };

            Closing += (_, _) =>
            {
                // 直接关闭，不做异步保存。会话持久化由用户显式操作或定时检查点负责。
                // 仅执行同步 flush 防止缓冲日志丢失。
                // Close directly without an asynchronous save; explicit user actions or periodic checkpoints persist the session.
                // Perform only a synchronous flush to prevent buffered log loss.
                try { _vm.FlushPendingWrites(); }
                catch { /* 关闭时日志 flush 失败不影响窗口关闭 */ }
            };

            _vm.CloseRequested += () => Dispatcher.BeginInvoke(new Action(() => Close()));

            Closed += (_, _) =>
            {
                _vm.OnNewMessageAdded -= scrollToBottom;
            };
        }

        // ── API Key PasswordBox → ViewModel 同步 / synchronization ──
        private void ApiKeyPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (sender is PasswordBox pb)
                _vm.ApiKeyInput = pb.Password;
        }

        // ── API Key 显示/隐藏切换 / visibility toggle ──
        private void ToggleKeyVisibility_Click(object sender, RoutedEventArgs e)
        {
            if (ApiKeyPasswordBox.Visibility == Visibility.Visible)
            {
                // 切换到明文显示。
                // Switch to plain-text display.
                ApiKeyTextBoxVisible.Visibility = Visibility.Visible;
                ApiKeyPasswordBox.Visibility = Visibility.Collapsed;
                ToggleKeyVisibility.Content = "🔒";
                ToggleKeyVisibility.ToolTip = "隐藏 API Key";
            }
            else
            {
                // 切换回遮罩输入。
                // Switch back to masked input.
                ApiKeyPasswordBox.Password = _vm.ApiKeyInput ?? "";
                ApiKeyPasswordBox.Visibility = Visibility.Visible;
                ApiKeyTextBoxVisible.Visibility = Visibility.Collapsed;
                ToggleKeyVisibility.Content = "👁";
                ToggleKeyVisibility.ToolTip = "显示 API Key";
            }
        }

        // ── 转换器 / Converters ──
        public static readonly IValueConverter RoleToDisplayNameConverter =
            new RoleConverter(r => r switch
            {
                "user" => "YOU",
                "assistant" => "SPATIAL AGENT",
                _ => ""
            });

        // 用户气泡与助手气泡统一左对齐（2026-07-19 用户裁定）。
        // Align user and assistant bubbles to the left (user decision on 2026-07-19).
        public static readonly IValueConverter RoleToAlignmentConverter =
            new RoleConverter(_ => HorizontalAlignment.Left);

        public static readonly IValueConverter RoleLabelVisibilityConverter =
            new RoleConverter(r => (r == "user" || r == "assistant") ? Visibility.Visible : Visibility.Collapsed);

        public static readonly IValueConverter RoleToColorConverter =
            new RoleConverter(r => r switch
            {
                // Light-theme label colors (dark variants of the VS blue/teal pair)
                // 浅色主题标签颜色（VS 蓝色/青色配色的深色变体）。
                "user" => "#0B69A3",
                "assistant" => "#0F7B6C",
                _ => "#888888"
            });

        public static readonly IValueConverter BoolToVisibilityConverter =
            new BoolToVisConverter();
        /// <summary>
        /// 字符串非空 → Visible，空/null → Collapsed。用于条件显示标签文本。
        /// Maps a non-empty string to Visible and an empty/null string to Collapsed for conditional label display.
        /// </summary>
        public static readonly IValueConverter StringToVisibilityConverter =
            new StringToVisConverter();
    }

    internal class RoleConverter : IValueConverter
    {
        private readonly Func<string, object> _f;
        public RoleConverter(Func<string, object> f) => _f = f;
        public object Convert(object v, Type t, object p, CultureInfo c)
            => _f(v?.ToString() ?? "");
        public object ConvertBack(object v, Type t, object p, CultureInfo c)
            => DependencyProperty.UnsetValue;
    }

    internal class BoolToVisConverter : IValueConverter
    {
        public object Convert(object v, Type t, object p, CultureInfo c)
            => (v is bool b && b) ? Visibility.Visible : Visibility.Collapsed;
        public object ConvertBack(object v, Type t, object p, CultureInfo c)
            => v is Visibility vis && vis == Visibility.Visible;
    }

    internal class StringToVisConverter : IValueConverter
    {
        public object Convert(object v, Type t, object p, CultureInfo c)
            => v is string s && !string.IsNullOrEmpty(s) ? Visibility.Visible : Visibility.Collapsed;
        public object ConvertBack(object v, Type t, object p, CultureInfo c)
            => DependencyProperty.UnsetValue;
    }
}
