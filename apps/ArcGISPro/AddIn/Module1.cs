using ArcGIS.Core.CIM;
using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Catalog;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Editing;
using ArcGIS.Desktop.Extensions;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;
using ArcGIS.Desktop.Framework.Dialogs;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;
using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using ArcGISProAIAgent.Buttons;
using ArcGIS.Desktop.Framework.Events;

namespace ArcGISProAIAgent
{
    internal class Module1 : Module
    {
        private static Module1? _this;
        private Func<CancelEventArgs, Task>? _applicationClosingHandler;
        private HostShutdownCoordinator? _shutdownCoordinator;

        public static Module1 Current => _this ??= (Module1)FrameworkApplication.FindModule("ArcGISProAIAgent_Module");

        #region Overrides
        protected override bool Initialize()
        {
            // CanUnload is not a reliable shutdown notification for every Pro
            // lifecycle. Subscribe to the host's explicit cancellable closing
            // event and keep a strong handler reference for the module lifetime.
            // CanUnload 不能可靠覆盖 Pro 的所有关闭生命周期，因此订阅宿主明确且可取消的
            // 关闭事件，并在模块整个生命周期内保留处理程序的强引用。
            _applicationClosingHandler = OnApplicationClosingAsync;
            ApplicationClosingEvent.Subscribe(_applicationClosingHandler, keepSubscriberAlive: true);
            _shutdownCoordinator = new HostShutdownCoordinator(
                ShowAgentButton.PrepareSharedSessionForHostShutdown,
                ShowAgentButton.FinalizeSharedSessionForHostShutdown);
            _shutdownCoordinator.Attach();
            return true;
        }

        internal void RefreshShutdownHooks(string source) =>
            _shutdownCoordinator?.RefreshHostWindow(source);

        private Task OnApplicationClosingAsync(CancelEventArgs args)
        {
            _shutdownCoordinator?.ObserveCancelable(
                "ApplicationClosingEvent",
                args);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Called by Framework when ArcGIS Pro is closing
        /// ArcGIS Pro 关闭时由 Framework 调用。
        /// </summary>
        /// <returns>False to prevent Pro from closing, otherwise True. / 返回 False 阻止 Pro 关闭，否则返回 True。</returns>
        protected override bool CanUnload()
        {
            // The standalone chat window can remain open until Pro exits, so
            // host shutdown—not Window.Closing—is the durable session boundary.
            // 独立聊天窗口可能一直保持打开直到 Pro 退出，因此持久会话边界是宿主关闭，
            // 而不是 Window.Closing。
            _shutdownCoordinator?.Signal("ModuleCanUnload");
            return true;
        }

        protected override void Uninitialize()
        {
            _shutdownCoordinator?.Signal("ModuleUninitialize");
            if (_applicationClosingHandler != null)
                ApplicationClosingEvent.Unsubscribe(_applicationClosingHandler);
            _applicationClosingHandler = null;
            _shutdownCoordinator?.Dispose();
            _shutdownCoordinator = null;
        }

        #endregion Overrides

    }
}
