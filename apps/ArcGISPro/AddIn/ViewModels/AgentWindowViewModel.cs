using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using SpatialAgent.Adapters.ArcGIS;
using SpatialAgent.Adapters.LLM;
using SpatialAgent.Adapters.Mcp;
using SpatialAgent.Knowledge;
using SpatialAgent.Runtime;
using SpatialAgent.Runtime.Contracts;
using SpatialAgent.Services;
using SpatialAgent.Tools.Core;
using SpatialAgent.Tools.Core.HighLevel;
using SpatialAgent.Core;

namespace ArcGISProAIAgent.ViewModels
{
    internal class AgentWindowViewModel : INotifyPropertyChanged
    {
        // ── 核心依赖 / Core dependencies ──
        private readonly DeepSeekProvider _llm;
        private readonly IEventBus _eventBus = new EventBus();
        private readonly ToolRegistry _tools = new();
        private readonly MapReader _mapReader = new();
        private readonly ProjectReader _projectReader = new();
        private readonly ContextManager _contextManager;
        private readonly AgentRuntime _runtime;
        private readonly IAgentSession _session;

        // ── 日志子系统（两阶段初始化）/ Logging subsystem (two-phase initialization) ──
        private readonly TranscriptWriter _transcript = new();
        private readonly ContextSnapshotter _snapshotter;
        private VerificationLedger? _ledger;
        private AuditLogger? _audit;
        private readonly LoggingHealthMonitor _healthMonitor = new();
        private WorkspaceTool? _workspace;
        private ProjectMemoryStore? _memoryStore;
        private FileRunCheckpointStore? _checkpointStore;
        private FileSessionLeaseStore? _sessionLeaseStore;
        private SessionLease? _activeSessionLease;
        private Timer? _sessionLeaseHeartbeatTimer;
        private readonly SemaphoreSlim _sessionLeaseLifecycleGate = new(1, 1);
        private readonly object _sessionLeaseStateLock = new();
        private readonly DurableSessionFinalizer _sessionFinalizer = new();
        private readonly string _sessionLeaseOwnerId = Guid.NewGuid().ToString("N");
        private string _sessionLeaseProjectFolder = "";
        private int _sessionLeaseHeartbeatInFlight;
        private static readonly TimeSpan SessionLeaseTtl = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan SessionLeaseHeartbeatInterval = TimeSpan.FromSeconds(10);
        private ToolIndex? _toolIndex;
        private KnowledgeRetriever? _ragRetriever;
        private SessionIndex? _sessionIndex;

        // ── 项目路径（延迟获取）/ Project path (resolved lazily) ──
        private string _projFolder = "";

        // ── UI 绑定属性 / UI binding properties ──
        private ObservableCollection<TimelineItem> _timelineItems = new();
        private readonly Dictionary<int, TimelineItem> _traceGroups = new();
        private readonly Dictionary<(int Turn, int Step), ToolTraceItem> _traceIndex = new();
        private CancellationTokenSource? _currentRunCts;
        private readonly CancellationTokenSource _hostLifetimeCts = new();
        private int _hostShutdownFinalized;
        private int _activeTurn;
        private string _userInput = "";
        private bool _isLoading;
        private string _apiKeyInput = "";
        private bool _showApiKeyInput = true;
        private string _statusBarText = "";
        private bool _hasPendingApproval;
        private string _pendingApprovalText = "";
        private string _pendingApprovalReason = "";
        private string _pendingRequestText = "";
        private ApprovalTicket? _pendingApprovalTicket;
        private string _projectSummaryTitle = "未识别项目";
        private string _projectSummaryDetail = "打开已保存的 ArcGIS Pro 项目后，系统会自动关联工作区、日志与会话历史。";
        private string _taskTitle = "等待任务";
        private string _taskSubtitle = "输入业务目标后，Agent 会先分析，再生成执行计划并进入工具执行与验证。";
        private string _taskStage = "空闲";
        private string _taskStageDetail = "尚未开始";
        private string _taskOutcome = "";
        private string _taskMetrics = "0 轮对话 · 0 条执行轨迹";
        private string _sessionOverview = "未连接项目";
        private string _currentFocus = "暂无焦点";
        private string _taskStageBrush = "#4B6484";
        private int _currentTaskTurn;
        private bool _isTimelinePanelExpanded;
        /// <summary>
        /// ViewModel 请求宿主关闭窗口（如检测到未保存项目）。
        /// Raised when the ViewModel asks the host to close the window, such as after detecting an unsaved project.
        /// </summary>
        public event Action? CloseRequested;

        // ── 会话浏览器 / Session browser ──
        public ObservableCollection<SessionInfo> SessionInfos { get; } = new();
        private SessionInfo? _selectedSessionInfo;
        public SessionInfo? SelectedSessionInfo
        {
            get => _selectedSessionInfo;
            set { if (Set(ref _selectedSessionInfo, value) && value != null) _ = ContinueFromSessionAsync(value); }
        }
        private bool _hasSessionHistory;
        public bool HasSessionHistory { get => _hasSessionHistory; set => Set(ref _hasSessionHistory, value); }
        private string _sessionPickerLabel = "会话历史";
        public string SessionPickerLabel { get => _sessionPickerLabel; set => Set(ref _sessionPickerLabel, value); }

        // ── 模型与思考档位切换 / Model and reasoning-level selection ──
        public List<string> AvailableModels { get; } = new() { "DeepSeek V4 Pro", "DeepSeek V4 Flash" };
        private string _selectedModel = "DeepSeek V4 Pro";
        public string SelectedModel
        {
            get => _selectedModel;
            set { if (Set(ref _selectedModel, value)) _ = SwitchModelAsync(value); }
        }

        public List<string> AvailableThinkingLevels { get; } = new() { "low", "medium", "high", "max" };
        private string _selectedThinking = "high";
        public string SelectedThinkingLevel
        {
            get => _selectedThinking;
            set
            {
                if (Set(ref _selectedThinking, value))
                {
                    _llm.SetThinkingEffort(value);
                    LogModelSwitch("thinking_changed");
                    UpdateStatusBar();
                }
            }
        }

        public List<string> AvailablePermissionLevels { get; } = new() { "只读", "请求批准", "完全访问" };
        private string _selectedPermissionLevel = "请求批准";
        public string SelectedPermissionLevel
        {
            get => _selectedPermissionLevel;
            set
            {
                if (Set(ref _selectedPermissionLevel, value))
                {
                    _session.PermissionLevel = PermissionLevelFromLabel(value);
                    UpdateStatusBar();
                }
            }
        }

        // ── 设置路径 / Settings path ──
        private static readonly string SETTINGS_FILE = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArcGISProAIAgent", "settings.json");

        // ── 资源目录 / Resource directories ──
        // 插件程序集部署在 AssemblyCache，BaseDirectory 指向 Pro 安装目录。
        // 因此必须从程序集所在目录探测资源。
        // The add-in assembly is deployed in AssemblyCache while BaseDirectory points to the Pro installation.
        // Resources must therefore be discovered relative to the assembly location.
        private static string GetSkillsDir() =>
            ResourcePaths.Resolve("Skills");
        private static string GetKnowledgeDir() =>
            ResourcePaths.Resolve(Path.Combine("Knowledge", "Documents"));

        private static string CreateThreadId(string prefix = "pro") =>
            $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss-fff}";

        // 阶段 1 日志路径：项目初始化前先写入 AppData。
        // Phase-1 log path: write to AppData until project initialization completes.
        private static string GetFallbackLogDir() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArcGISProAIAgent", "logs");

        // ================================================================
        // 构造函数：两阶段初始化的阶段 1。
        // Constructor: phase 1 of the two-phase initialization.
        // ================================================================
        public AgentWindowViewModel()
        {
            _snapshotter = new ContextSnapshotter(_mapReader, _projectReader);
            // GIS Dispatcher 必须先于任何工具执行完成设置。
            // The GIS Dispatcher must be configured before any tool executes.
            GPToolRunner.DefaultDispatcher = new ProGisDispatcher();

            // LLM 提供程序 / LLM provider
            _llm = new DeepSeekProvider("deepseek-v4-pro");
            LoadApiKey();

            // 日志组件先写 AppData，Runtime 构造后由 InitializeLogging 统一激活。
            // Logging components initially write to AppData and are activated together by InitializeLogging after Runtime construction.
            _ledger = new VerificationLedger();
            _healthMonitor.BindStats(
                ledgerTotalChecks: () => _ledger.TotalChecks,
                ledgerPassedChecks: () => _ledger.PassedChecks,
                ledgerFailedChecks: () => _ledger.FailedChecks,
                snapshotCount: () => _snapshotter.SnapshotCount,
                auditEntryCount: () => _audit?.EntryCount ?? 0);

            // diagnostics.warning → UI toast；呈现职责保留在 ViewModel。
            // diagnostics.warning → UI toast; presentation remains the ViewModel's responsibility.
            _eventBus.Subscribe("diagnostics.warning", e => OnUi(() =>
            {
                var area = e.Metadata?.GetValueOrDefault("area", "runtime") ?? "runtime";
                var message = e.Metadata?.GetValueOrDefault("message", "") ?? "";
                var error = e.Metadata?.GetValueOrDefault("error", "") ?? "";
                AddSystem(string.IsNullOrWhiteSpace(error)
                    ? $"⚠ {area}: {message}"
                    : $"⚠ {area}: {message} ({error})");
            }));

            // EventBus → 调试输出 / debug output
            _eventBus.Subscribe("*", e => Debug.WriteLine(
                $"[Agent] {e.EventType} turn={e.Turn} {string.Join(",", e.Metadata?.Select(kv => $"{kv.Key}={kv.Value}") ?? Array.Empty<string>())}"));
            SubscribeTraceEvents();

            // 注册工具 / Register tools
            RegisterAllTools();

            // RAG + Skills：同步加载文件，后台构建向量和索引。
            // RAG + Skills: load files synchronously, then build vectors and indexes in the background.
            var tfidf = new TfidfEmbeddingService();
            _ragRetriever = null;
            ISearchProvider? knowledge = null;
            SkillManager? skills = null;

            try
            {
                var api = new ApiEmbeddingService(_llm);
                var hybrid = new HybridEmbeddingService(tfidf, api);

                // Phase 1: Load skill files immediately (sync I/O, no API needed).
                // L1/L2 keyword matching works right away; L3 embedding builds async.
                // 阶段 1：立即加载技能文件（同步 I/O，无需 API）。
                // L1/L2 关键词匹配可立即使用；L3 嵌入在后台异步构建。
                skills = new SkillManager(GetSkillsDir(), async text => await api.EmbedAsync(text));
                skills.LoadAll();
                Debug.WriteLine($"[ViewModel] Skills loaded: {skills.Count} skills");

                // Phase 2: Build knowledge index in background (may use embedding API).
                // Fire-and-forget with error channel; the first query may get no results
                // if index isn't ready, but subsequent queries will benefit.
                // 阶段 2：在后台构建知识索引（可能使用嵌入 API）。
                // 采用带错误通道的即发即弃方式；若索引尚未就绪，首次查询可能无结果，
                // 但后续查询将受益于已完成的索引。
                _ragRetriever = new KnowledgeRetriever(hybrid, GetKnowledgeDir());
                knowledge = new SearchProviderAdapter(_ragRetriever);
                _ = _ragRetriever.BuildIndexAsync().ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        Debug.WriteLine($"[ViewModel] RAG index build failed: {t.Exception?.InnerException?.Message}");
                    else
                        Debug.WriteLine($"[ViewModel] RAG index ready: {_ragRetriever.ChunkCount} chunks");
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ViewModel] RAG init failed: {ex.GetType().Name}: {ex.Message}");
                // Fallback: skills with local file loading only, no embedding.
                // 回退方案：仅从本地文件加载技能，不使用嵌入。
                skills ??= new SkillManager(GetSkillsDir());
                try { skills.LoadAll(); } catch (Exception ex2) {
                    Debug.WriteLine($"[ViewModel] Skill fallback load failed: {ex2.Message}");
                }
            }

            // ContextManager（包含 RAG 与 ToolIndex）/ ContextManager (including RAG and ToolIndex)
            _contextManager = new ContextManager(_projectReader, _mapReader, skills, knowledge);

            // Load tool index for context-based tool filtering.
            // 加载工具索引，用于基于上下文筛选工具。
            string toolIndexPath = ResourcePaths.Resolve("tools_index.json");
            _toolIndex = new ToolIndex();
            _toolIndex.Load(toolIndexPath);
            _contextManager.SetToolIndex(_toolIndex);
            Debug.WriteLine($"[ViewModel] ToolIndex: {_toolIndex.Count} tools indexed");
            var toolSemanticTfidf = new TfidfEmbeddingService();
            var toolSemanticScorer = new ToolSemanticScorer(toolSemanticTfidf);
            _ = toolSemanticScorer.BuildAsync(_tools.GetAllDescriptors());
            _contextManager.SetToolSemanticScorer(toolSemanticScorer);

            // Agent 会话与运行时 / AgentSession + AgentRuntime
            _session = new AgentSession(CreateThreadId(), _eventBus);
            _session.PermissionLevel = PermissionLevelFromLabel(_selectedPermissionLevel);
            _runtime = new AgentRuntime(_session, _llm, _tools, _contextManager, _eventBus);

            // MCP server：Kestrel Streamable HTTP，替代旧 HttpListener。
            // MCP server: Kestrel Streamable HTTP replaces the legacy HttpListener.
            try
            {
                var mcpServer = new McpStreamableHttpServer(_tools, knowledge, port: 8765);
                _runtime.SetMcpService(mcpServer);
                mcpServer.Start();  // Start immediately at host init so MCP clients can connect before first user message
                Debug.WriteLine($"[ViewModel] MCP server started: {mcpServer.Url}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ViewModel] MCP server failed to start: {ex.Message}");
                // MCP 启动失败不阻断 Agent 主流程。
                // An MCP startup failure must not block the main Agent workflow.
            }

            // 注入安全、审计和日志模块。
            // Inject the safety, audit, and logging modules.
            var safety = new SafetyGate();
            safety.SetToolRegistry(_tools);
            _runtime.SetSafety(safety);
            _runtime.SetTranscriptWriter(_transcript, _transcript, _transcript, _transcript);

            // 日志初始化统一通过 AgentRuntime，必须在 Runtime 构造后调用。
            // Logging is initialized centrally through AgentRuntime and must run after Runtime construction.
            _runtime.SetLedger(_ledger);
            _runtime.SetSnapshotter(_snapshotter);
            _runtime.SetLoggingHealthMonitor(_healthMonitor);
            _runtime.InitializeLogging(new LoggingConfig { LogDir = GetFallbackLogDir() });
            _transcript.WriteSessionStart(_session.ThreadId, _selectedModel, _selectedThinking, null);

            // PostCondition 验证器（注入 transcript 与 ledger）。
            // Post-condition validator with transcript and ledger injection.
            var postConditionRule = new PostConditionValidationRule(
                new ArcGISPostConditionProbe(), _eventBus, _transcript, _ledger);
            var validators = new List<IValidationRule> { postConditionRule };
            _runtime.SetValidators(validators);
            _runtime.SetParameterValidator(
                new SpatialAgent.Runtime.Validation.ParameterValidator(_tools.GetAllDescriptors()));

            // 数据影响安全门（ADR 0051）。
            // Data-influence safety gate (ADR 0051).
            _runtime.SetDataInfluenceEstimator(new DataInfluenceEstimator(_mapReader));

            // 订阅进度事件 / Subscribe to progress events
            _runtime.OnProgress += (_, detail) =>
            {
                UpdateStatusBar();
            };

            // UI 初始状态 / Initial UI state
            ShowApiKeyInput = !_llm.IsConfigured;
            UpdateStatusBar();

            // API 已配置时进入阶段 2。
            // Enter phase 2 when the API is configured.
            if (_llm.IsConfigured)
                _ = InitializeProjectContextAsync();
        }

        // ================================================================
        // RAG 索引延迟重建：embedding API 就绪后重试。
        // Deferred RAG index rebuild: retry after the embedding API becomes available.
        // ================================================================
        private async Task RebuildRagIndexIfNeededAsync()
        {
            if (_ragRetriever == null || _ragRetriever.IsIndexed)
                return;

            try
            {
                Debug.WriteLine("[ViewModel] Retrying RAG index build...");
                await _ragRetriever.BuildIndexAsync();
                Debug.WriteLine($"[ViewModel] RAG index rebuilt: {_ragRetriever.ChunkCount} chunks, indexed={_ragRetriever.IsIndexed}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ViewModel] RAG index rebuild failed: {ex.Message}");
            }
        }

        // ================================================================
        // 阶段 2：获取项目路径后的项目初始化。
        // Phase 2: initialize the project after resolving its path.
        // ================================================================
        private async Task InitializeProjectContextAsync()
        {
            try
            {
                var projCtx = await _projectReader.GetProjectContextAsync();

                // ★ 未保存项目（Pro 临时目录）→ 立即阻断，不落盘任何文件。
                // Pro 退出后会删除临时目录，agent 在上面创建的任何 workspace/logs
                // 都会丢失且无法排查问题。判定依据：project.URI 为空 = 未保存。
                // 这是语义级 API 判断，不是路径硬编码。
                // ★ Unsaved project (Pro temporary directory) → block immediately and write nothing to disk.
                // Pro deletes the temporary directory on exit, so any workspace/logs created there would be lost
                // and unavailable for diagnosis. The semantic API check is project.URI being empty, not a hard-coded path.
                if (projCtx.IsAvailable && !projCtx.IsSaved)
                {
                    Debug.WriteLine($"[ViewModel] Unsaved project detected — IsAvailable={projCtx.IsAvailable}, IsSaved={projCtx.IsSaved}, TempFolder={projCtx.TempFolder}");
                    // ★ MessageBox 和 CloseRequested 必须在同一 UI 调度帧内顺序执行。
                    // OnUi 在非 UI 线程上走 BeginInvoke（异步），如果 CloseRequested 放在
                    // OnUi 外面，窗口可能在 MessageBox 显示前就被关闭。
                    // ★ MessageBox and CloseRequested must run in order within the same UI dispatch frame.
                    // OnUi uses BeginInvoke asynchronously off the UI thread; placing CloseRequested outside OnUi
                    // could close the window before the MessageBox appears.
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        MessageBox.Show(
                            "当前项目尚未保存。\n\nArcGIS Pro 为未保存项目分配的是临时目录，退出即删除。" +
                            "Agent 不能在此路径下创建工作空间或日志。\n\n" +
                            "请先保存项目（Ctrl+S），然后重新打开本面板。",
                            "Spatial Agent",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                        CloseRequested?.Invoke();
                    });
                    return;
                }

                if (projCtx.IsAvailable && projCtx.IsSaved && !string.IsNullOrWhiteSpace(projCtx.HomeFolder))
                {
                    _projFolder = projCtx.HomeFolder;
                }
                else
                {
                    _projFolder = "";
                }

                if (!string.IsNullOrEmpty(_projFolder))
                {
                    // 将所有阶段 1 日志作为一个生命周期迁移到项目目录。
                    // Migrate all phase-1 logs to the project directory as one lifecycle.
                    string projectLogDir = Path.Combine(_projFolder, "agent_logs");
                    _transcript.ProjectRoot = _projFolder;
                    _transcript.HomeFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    _runtime.MigrateLogging(projectLogDir);
                    _healthMonitor.Reset();

                    // 工作区工具 / WorkspaceTool
                    int wkid = 0;
                    try
                    {
                        var mapInfoJson = await _mapReader.GetMapInfoAsync();
                        var mapInfo = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(mapInfoJson);
                        wkid = (int?)mapInfo?.wkid ?? 0;
                    }
                    catch { /* WKID optional for workspace init */ }

                    _workspace = new WorkspaceTool();
                    await _workspace.InitializeAsync(_projFolder, wkid);
                    _runtime.SetWorkspace(_workspace);
                    RefreshBackupSize();
                    _checkpointStore = new FileRunCheckpointStore(
                        _projFolder,
                        eventBus: _eventBus);
                    _runtime.SetCheckpointStore(_checkpointStore);

                    // InitializeAsync 已创建 agentworkspace.gdb。
                    // 把 Workspace 回填到构造期已注册的工具，避免裸名输出落入
                    // 项目默认 GDB。
                    // InitializeAsync has created agentworkspace.gdb.
                    // Backfill the Workspace into tools registered during construction so bare output names
                    // do not fall into the project's default GDB.
                    int routedTools = _tools.AttachWorkspace(_workspace);
                    Debug.WriteLine($"[ViewModel] Workspace routed into {routedTools} tools");

                    _memoryStore = new ProjectMemoryStore(_projFolder);
                    _contextManager.SetProjectMemory(_memoryStore);
                    _runtime.SetProjectLineageStore(_memoryStore);
                    await InitializeSessionLeaseAsync(
                        _checkpointStore,
                        _memoryStore);

                    // Session index for history browser.
                    // 用于历史浏览器的会话索引。
                    _sessionIndex = new SessionIndex(_projFolder);
                    _sessionIndex.Load();
                    RefreshSessionList();

                    // AuditLogger 延迟到阶段 2 创建，因为它需要项目目录。
                    // Create AuditLogger in phase 2 because it requires the project directory.
                    _audit = new AuditLogger(_projFolder);
                    _audit.SetLoggingHealthMonitor(_healthMonitor);
                    _healthMonitor.BindStats(auditEntryCount: () => _audit.EntryCount);
                    _runtime.SetAudit(_audit);
                    SetProjectSummary(
                        $"项目：{Path.GetFileName(_projFolder)}",
                        "已接入工作区、日志、会话历史与项目记忆。后续审批、备份和恢复都将落在这个项目上下文中。");
                    AddMessage("system", $"项目已识别：{Path.GetFileName(_projFolder)}。工作区、日志、会话历史已关联至此项目目录。");

                    // Start ArcGIS GP cold initialization while the user is
                    // reading the project summary. This is a read-only,
                    // process-wide single-flight probe; a real GP call joins
                    // it rather than racing another engine initialization.
                    // 在用户阅读项目摘要时启动 ArcGIS GP 冷初始化。这是进程级、只读的
                    // 单次并发探测；真实 GP 调用会加入该任务，而不是与另一次引擎初始化竞争。
                    _ = WarmUpGeoprocessingAsync(_hostLifetimeCts.Token);
                }
                else
                {
                    // ★ F1：工程文件尚未保存，没有目录就无法创建 agent 工作区。
                    // 此时不降级——workspace/日志/会话/审计全部依赖于项目目录。
                    // ★ F1: an unsaved project has no directory in which to create the agent workspace.
                    // Do not degrade here—workspace, logs, sessions, and audit data all depend on the project directory.
                    SetProjectSummary(
                        "项目未保存",
                        "请先保存 ArcGIS Pro 项目。未保存状态下不会创建工作区、日志或审计目录。");
                    AddMessage("system", "当前项目尚未保存。请先保存项目（Ctrl+S），然后输入 /project 手动识别，或重新打开本面板。");
                }

                // RAG index deferred rebuild: if embedding API wasn't ready at construction time,
                // the index build may have silently failed. Retry now that LLM is confirmed working.
                // RAG 索引延迟重建：若构造时嵌入 API 尚未就绪，索引构建可能静默失败；
                // 现已确认 LLM 可用，因此再次尝试。
                _ = RebuildRagIndexIfNeededAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ViewModel] InitializeProjectContextAsync failed: {ex.GetType().Name}: {ex.Message}");
                SetProjectSummary("项目信息不可用", "未能读取 ArcGIS Pro 项目上下文，可先继续对话，但工作区与会话治理能力不会完整启用。");
                AddMessage("system", "未能读取项目信息。打开项目后可输入 /project 手动识别。");
            }
        }

        private async Task WarmUpGeoprocessingAsync(CancellationToken cancellation)
        {
            try
            {
                var map = await _mapReader.GetMapContextAsync();
                string? probeDataset = map.Layers
                    .FirstOrDefault(layer =>
                        !string.IsNullOrWhiteSpace(layer.Name)
                        && !string.Equals(
                            layer.GeometryType,
                            "Raster",
                            StringComparison.OrdinalIgnoreCase))
                    ?.Name;
                if (string.IsNullOrWhiteSpace(probeDataset))
                    return;

                var runner = new GPToolRunner();
                await runner.WarmUpAsync(
                    probeDataset,
                    update => _eventBus.Publish(new AgentEvent
                    {
                        EventType = "runtime.gp_warmup",
                        Metadata = new()
                        {
                            ["phase"] = update.Phase,
                            ["progress_pct"] = update.Percent?.ToString() ?? ""
                        },
                        Data = new()
                        {
                            ["message"] = update.Message
                        }
                    }),
                    cancellation);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // Host shutdown owns cancellation; no user-facing warning.
                // 取消由宿主关闭流程负责，不显示面向用户的警告。
            }
            catch (Exception ex)
            {
                _eventBus.Publish(new AgentEvent
                {
                    EventType = "runtime.gp_warmup",
                    Metadata = new() { ["phase"] = "failed" },
                    Data = new()
                    {
                        ["message"] = "ArcGIS 地理处理预热失败；首次任务将按正常路径启动",
                        ["error"] = ex.Message
                    }
                });
            }
        }

        // ================================================================
        // 工具注册 / Tool registration
        // ================================================================
        private void RegisterAllTools()
        {
            // 核心只读工具 / Core read-only tools
            _tools.Register(new GetMapInfoTool());
            _tools.Register(new ListLayersTool());
            _tools.Register(new DescribeLayerTool());
            _tools.Register(new GetSelectionTool());
            _tools.Register(new GetProjectInfoTool());
            _tools.Register(new ReadRowsTool());   // 小样本读行，受行数与字段数硬门槛约束。

            // 核心模式 GP 工具（用于契约/验证）/ Core schema GP tools (for contracts/verification)
            var schemaReg = new ToolSchemaRegistry();
            schemaReg.RegisterAllDefaults();
            foreach (var s in schemaReg.GetAllSchemas())
                _tools.Register(new SchemaGPToolAdapter(s.Name, s, schemaReg));

            // 高级工作流工具 / High-level workflow tools
            _tools.Register(new CalculateAreaTool());
            _tools.Register(new CalculateLengthTool());

            // 行业标准对照查询，为用地用海/三调转换提供权威映射。
            // Industry-standard lookup that provides authoritative mappings for land/sea use and third-survey conversion.
            _tools.Register(new LandCodeLookupTool());

            // 字段映射赋值：唯一值 → 查表 → code block 保值写入。
            // Field-mapping assignment: unique values → lookup → value-preserving code-block write.
            _tools.Register(new MapFieldValuesTool());

            // Optional runtime registration from metadata generated locally by
            // a user with a licensed ArcGIS Pro installation.
            // 可选：根据持有 ArcGIS Pro 许可的用户在本地生成的元数据注册运行时工具。
            string manifestPath = ResourcePaths.Resolve("tools_manifest.json");
            int loaded = DynamicToolLoader.LoadAndRegister(manifestPath, _tools, _workspace);
            Debug.WriteLine($"[ViewModel] DynamicToolLoader: {loaded} tools registered from manifest");
        }

        // ================================================================
        // UI 绑定属性 / UI binding properties
        // ================================================================
        public ObservableCollection<TimelineItem> TimelineItems { get => _timelineItems; set => Set(ref _timelineItems, value); }
        public bool HasTimelineItems => TimelineItems.Count > 0;
        public string UserInput { get => _userInput; set => Set(ref _userInput, value); }
        public bool IsLoading { get => _isLoading; set => Set(ref _isLoading, value); }
        public string ApiKeyInput { get => _apiKeyInput; set => Set(ref _apiKeyInput, value); }
        public bool ShowApiKeyInput { get => _showApiKeyInput; set => Set(ref _showApiKeyInput, value); }
        public string StatusBarText { get => _statusBarText; set => Set(ref _statusBarText, value); }
        public string ProjectSummaryTitle { get => _projectSummaryTitle; set => Set(ref _projectSummaryTitle, value); }
        public string ProjectSummaryDetail { get => _projectSummaryDetail; set => Set(ref _projectSummaryDetail, value); }
        public string TaskTitle { get => _taskTitle; set => Set(ref _taskTitle, value); }
        public string TaskSubtitle { get => _taskSubtitle; set => Set(ref _taskSubtitle, value); }
        public string TaskStage { get => _taskStage; set => Set(ref _taskStage, value); }
        public string TaskStageDetail { get => _taskStageDetail; set => Set(ref _taskStageDetail, value); }
        public string TaskOutcome
        {
            get => _taskOutcome;
            set
            {
                if (Set(ref _taskOutcome, value))
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasTaskOutcome)));
            }
        }
        public bool HasTaskOutcome => !string.IsNullOrWhiteSpace(_taskOutcome);
        public string TaskMetrics { get => _taskMetrics; set => Set(ref _taskMetrics, value); }
        public string SessionOverview { get => _sessionOverview; set => Set(ref _sessionOverview, value); }
        public string CurrentFocus { get => _currentFocus; set => Set(ref _currentFocus, value); }
        public string TaskStageBrush { get => _taskStageBrush; set => Set(ref _taskStageBrush, value); }
        public bool IsTimelinePanelExpanded { get => _isTimelinePanelExpanded; set => Set(ref _isTimelinePanelExpanded, value); }
        /// <summary>
        /// 所有时间线项（消息+计划+轨迹），按时间顺序排列。系统消息不出现在聊天区。
        /// All timeline items (messages, plans, and traces) in chronological order; system messages are excluded from chat.
        /// </summary>
        public IReadOnlyList<TimelineItem> ExecutionTimelineItems =>
            TimelineItems.Where(item => item.Chat?.Role != "system").ToList();
        public bool HasExecutionTimelineItems => ExecutionTimelineItems.Count > 0;

        /// <summary>
        /// 会话累计 Token；每次 CmdSendMessage 完成后刷新。
        /// Cumulative session tokens, refreshed after each CmdSendMessage completion.
        /// </summary>
        public TokenUsage SessionTokens => _runtime.Session.TokenUsage;
        public ObservableCollection<string> PendingTools { get; } = new();
        public bool HasPendingApproval
        {
            get => _hasPendingApproval;
            set { if (Set(ref _hasPendingApproval, value)) UpdateStatusBar(); }
        }
        public string PendingApprovalText { get => _pendingApprovalText; set => Set(ref _pendingApprovalText, value); }
        public string PendingApprovalReason { get => _pendingApprovalReason; set => Set(ref _pendingApprovalReason, value); }

        private void UpdateStatusBar()
        {
            string model = _selectedModel.Replace("DeepSeek ", "").Replace("V4 ", "V4-");
            string permission = $"权限：{_selectedPermissionLevel}";

            // 累计 Token / Accumulate tokens
            string tokenInfo = "";
            if (_runtime.Session.TokenUsage.TotalTokens > 0)
            {
                tokenInfo = $" | 本次会话累计 Token：{FormatTokens(_runtime.Session.TokenUsage.PromptTokens)} 入 / {FormatTokens(_runtime.Session.TokenUsage.CompletionTokens)} 出";
            }

            StatusBarText = HasPendingApproval
                ? $"等待授权：{PendingApprovalText} | {permission}{tokenInfo}"
                : $"{model} | 思考：{_selectedThinking} | {permission}{tokenInfo}";
            // 通知 UI Token 已变化。
            // Notify the UI that token counts changed.
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(SessionTokens)));
            RefreshDashboardMetrics();
        }

        /// <summary>
        /// 组装本轮 token 信息，格式示例：2026/07/19 19:05:31 token: 1.2K in / 0.4K out。
        /// Builds the current turn's token summary, for example: 2026/07/19 19:05:31 token: 1.2K in / 0.4K out.
        /// </summary>
        private string? BuildPerTurnTokenInfo()
        {
            var lt = _runtime.LastTurnTokens;
            if (lt.TotalTokens == 0) return null;
            return $"{DateTime.Now:yyyy/MM/dd HH:mm:ss} token: {FormatTokens(lt.PromptTokens)} in / {FormatTokens(lt.CompletionTokens)} out";
        }

        private static string FormatTokens(int count) =>
            count >= 1_000_000 ? $"{count / 1_000_000.0:F1}M" :
            count >= 10_000   ? $"{count / 1000.0:F0}K" :
            count >= 1_000    ? $"{count / 1000.0:F1}K" :
                                count.ToString();

        private void SetProjectSummary(string title, string detail)
        {
            ProjectSummaryTitle = title;
            ProjectSummaryDetail = detail;
            RefreshDashboardMetrics();
        }

        private void BeginTaskRun(string requestText, bool isApprovalResumption = false)
        {
            _currentTaskTurn = Math.Max(_runtime.TurnCount + (isApprovalResumption ? 0 : 1), 1);
            TaskTitle = Truncate(requestText, 96);
            TaskSubtitle = isApprovalResumption
                ? "审批通过后恢复执行，继续沿原计划推进。"
                : "已接收任务，正在分析上下文并生成可执行计划。";
            TaskOutcome = "";
            SetTaskStage(
                isApprovalResumption ? "恢复执行" : "分析中",
                isApprovalResumption ? "正在恢复被暂停的步骤与状态。" : "正在收集项目上下文、图层状态和可用工具。",
                isApprovalResumption ? "#0B69A3" : "#4B6484");
            CurrentFocus = isApprovalResumption ? "审批恢复" : "等待执行计划";
        }

        private void SetTaskStage(string stage, string detail, string brush)
        {
            TaskStage = stage;
            TaskStageDetail = detail;
            TaskStageBrush = brush;
        }

        private void RefreshDashboardMetrics()
        {
            int traceCount = _traceGroups.Values.Sum(g => g.TraceItems.Count);
            int completed = _traceGroups.Values.Sum(g => g.TraceItems.Count(i => i.Status == ToolTraceStatus.Completed));
            int approvals = _traceGroups.Values.Sum(g => g.TraceItems.Count(i => i.Status == ToolTraceStatus.AwaitingApproval));
            int failed = _traceGroups.Values.Sum(g => g.TraceItems.Count(i => i.Status == ToolTraceStatus.Failed || i.Status == ToolTraceStatus.Blocked));

            TaskMetrics = $"{_runtime.TurnCount} 轮对话 · {traceCount} 条执行轨迹";
            SessionOverview = string.IsNullOrWhiteSpace(_projFolder)
                ? "项目未接入 · 仅保留会话级能力"
                : $"{Path.GetFileName(_projFolder)} · 已接入工作区/日志/会话历史";

            if (HasPendingApproval)
                CurrentFocus = $"等待决策 · {PendingApprovalText}";
            else if (failed > 0)
                CurrentFocus = $"存在风险项 · {failed} 条待复核";
            else if (approvals > 0)
                CurrentFocus = $"待授权步骤 · {approvals} 条";
            else if (IsLoading)
                CurrentFocus = "执行进行中";
            else if (completed > 0)
                CurrentFocus = $"已完成步骤 · {completed} 条";
            else
                CurrentFocus = "暂无焦点";
        }

        private void UpdateTaskOutcomeFromResponse(AgentResponse resp)
        {
            bool hasFailures = resp.ToolResults.Any(r => !r.Success);
            bool blocked = string.Equals(resp.StopReason, "SafetyBlocked", StringComparison.OrdinalIgnoreCase);

            if (resp.RequiresApproval)
            {
                SetTaskStage("等待决策", "运行时已暂停，请先确认影响范围后再继续。", "#A15C00");
                TaskOutcome = "";
                return;
            }

            if (blocked)
            {
                SetTaskStage("已阻止", "安全策略阻止了本次执行，请调整目标或切换为更保守的方案。", "#B03A2E");
            }
            else if (hasFailures)
            {
                SetTaskStage("完成但有问题", "部分步骤失败或未通过验证，请优先查看轨迹中的红色/黄色项。", "#B03A2E");
            }
            else
            {
                SetTaskStage("已完成", "本轮任务已结束，结果已写入会话与轨迹。", "#2E7D32");
            }

            string summary = !string.IsNullOrWhiteSpace(resp.UserReply)
                ? resp.UserReply
                : !string.IsNullOrWhiteSpace(resp.ExecutionSummary)
                    ? resp.ExecutionSummary
                    : "操作已完成。";
            TaskOutcome = Truncate(summary, 220);
        }

        // ================================================================
        // 命令 / Commands
        // ================================================================
        public ICommand CmdSaveApiKey => new RelayCommand(async () =>
        {
            if (string.IsNullOrWhiteSpace(ApiKeyInput)) return;
            _llm.SetApiKey(ApiKeyInput.Trim());
            SaveApiKey(ApiKeyInput.Trim());
            ShowApiKeyInput = false;
            UpdateStatusBar();
            try
            {
                await _llm.ChatAsync(new List<Message>(), "ok");
                AddSystem("连接成功。");
                _ = InitializeProjectContextAsync();
            }
            catch { AddSystem("连接失败。"); ShowApiKeyInput = true; }
        });

        public ICommand CmdSendMessage => new RelayCommand(async () => await SendAsync(),
            () => !string.IsNullOrWhiteSpace(UserInput) && !IsLoading && !HasPendingApproval);


        public ICommand CmdApprovePending => new RelayCommand(async () => await ApprovePendingAsync(),
            () => HasPendingApproval && !IsLoading &&
                  _pendingApprovalTicket != null &&
                  !string.IsNullOrWhiteSpace(_pendingRequestText));

        public ICommand CmdRejectPending => new RelayCommand(async () =>
        {
            RejectPending();
            await Task.CompletedTask;
        }, () => HasPendingApproval && !IsLoading);

        public ICommand CmdStopRun => new RelayCommand(async () =>
        {
            _currentRunCts?.Cancel();
            AddSystem("已请求停止当前任务。");
            await Task.CompletedTask;
        }, () => IsLoading);

        public ICommand CmdUsePrompt => new RelayCommand(async param =>
        {
            string prompt = param?.ToString() ?? "";
            if (!string.IsNullOrWhiteSpace(prompt))
                UserInput = prompt;
            await Task.CompletedTask;
        }, _ => !IsLoading);

        public ICommand CmdOpenTranscript => new RelayCommand(async () =>
        {
            string? path = _transcript.FilePath;
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            else
            {
                AddSystem("暂无可打开的日志文件。");
            }
            await Task.CompletedTask;
        });

        public ICommand CmdNewSession => new RelayCommand(async () =>
        {
            FinalizeCurrentSessionForSwitch("NewSession");

            string newThreadId = CreateThreadId();
            _runtime.ResetConversation(newThreadId);
            _contextManager.InvalidateCache();
            ResetConversationUi();

            _transcript.WriteSessionStart(
                newThreadId,
                _selectedModel,
                _selectedThinking,
                string.IsNullOrWhiteSpace(_projFolder) ? null : _projFolder);
            await ActivateSessionLeaseAsync(newThreadId);

            // The just-ended session's checkpoints are on disk; rescan so it
            // appears in the history dropdown immediately (not only after restart).
            // 刚结束会话的检查点已经落盘；重新扫描后，它会立即出现在历史下拉列表中，
            // 而不必等到重启后才显示。
            RefreshSessionList();

            UpdateStatusBar();
        }, () => !IsLoading && !HasPendingApproval);

        public ICommand CmdReloadMemory => new RelayCommand(async () =>
        {
            if (_memoryStore == null)
            {
                AddSystem("项目记忆尚未初始化。请先打开项目。");
                return;
            }

            _contextManager.InvalidateCache();
            var summary = _memoryStore.GetSummary();
            if (string.IsNullOrWhiteSpace(summary))
            {
                AddSystem("项目记忆为空。完成一些操作后，会话摘要会自动记录。");
            }
            else
            {
                AddSystem("项目记忆已重新加载到上下文。");
            }
            await Task.CompletedTask;
        }, () => !IsLoading);

        // ── 备份位置与空间指示器 / Backup location and size indicator ──

        private string _backupSizeText = "";
        /// <summary>
        /// 当前项目备份占用空间，例如“12.5 MB”。
        /// Disk space used by the current project's backups, for example, "12.5 MB".
        /// </summary>
        public string BackupSizeText { get => _backupSizeText; set => Set(ref _backupSizeText, value); }

        /// <summary>
        /// 刷新备份 GDB 的磁盘占用；项目初始化后调用。
        /// 遍历 agentbackup.gdb 下的文件并计算总大小。
        /// Refreshes backup GDB disk usage after project initialization by summing files under agentbackup.gdb.
        /// </summary>
        private void RefreshBackupSize()
        {
            try
            {
                string? gdbPath = _workspace?.BackupGdb;
                if (string.IsNullOrEmpty(gdbPath) || !Directory.Exists(gdbPath))
                {
                    BackupSizeText = "";
                    return;
                }

                long totalBytes = 0;
                var dirInfo = new DirectoryInfo(gdbPath);
                foreach (var file in dirInfo.GetFiles("*", SearchOption.AllDirectories))
                    totalBytes += file.Length;

                if (totalBytes >= 1024 * 1024 * 1024)
                    BackupSizeText = $"{(double)totalBytes / (1024 * 1024 * 1024):F1} GB";
                else if (totalBytes >= 1024 * 1024)
                    BackupSizeText = $"{(double)totalBytes / (1024 * 1024):F1} MB";
                else if (totalBytes >= 1024)
                    BackupSizeText = $"{(double)totalBytes / 1024:F0} KB";
                else if (totalBytes > 0)
                    BackupSizeText = $"{totalBytes} B";
                else
                    BackupSizeText = "0 B";
            }
            catch
            {
                BackupSizeText = "--";  // 无法访问时显示占位符
            }
        }

        public ICommand CmdOpenBackupLocation => new RelayCommand(async () =>
        {
            try
            {
                string? gdbPath = _workspace?.BackupGdb;
                if (string.IsNullOrEmpty(gdbPath))
                {
                    AddSystem("备份工作空间尚未初始化。请先打开一个项目。");
                    return;
                }

                // 当前先在资源管理器中打开 GDB 的父目录。
                // 后续确认 Pro SDK API 后，可改为在 Catalog 窗格直接定位 GDB。
                // For now, open the GDB's parent directory in File Explorer.
                // After confirming the Pro SDK API, this can navigate directly to the GDB in the Catalog pane.
                string? parentDir = Path.GetDirectoryName(gdbPath);
                if (!string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir))
                {
                    Process.Start(new ProcessStartInfo(parentDir) { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                AddSystem($"无法打开备份位置：{ex.Message}");
            }
            await Task.CompletedTask;
        }, () => !string.IsNullOrEmpty(_workspace?.BackupGdb));

        // ── 会话历史浏览器 / Session history browser ──

        private void RefreshSessionList()
        {
            SessionInfos.Clear();
            if (_sessionIndex == null) return;

            _sessionIndex.Load();
            string currentThreadId = _runtime.Session.ThreadId;
            foreach (var s in _sessionIndex.ListSessions())
            {
                // The active thread is not history; listing it would offer
                // restoring into the conversation the user is already in.
                // 活动线程不属于历史记录；列出它会让用户把当前对话恢复到其自身。
                if (s.ThreadId.Equals(currentThreadId, StringComparison.OrdinalIgnoreCase))
                    continue;
                SessionInfos.Add(s);
            }

            HasSessionHistory = SessionInfos.Count > 0;
            SessionPickerLabel = HasSessionHistory
                ? $"会话历史 ({SessionInfos.Count})"
                : "会话历史";
        }

        private async Task ContinueFromSessionAsync(SessionInfo session)
        {
            if (session == null) return;
            // Stale dropdown entry pointing at the conversation we're already in.
            // 过期的下拉列表项指向当前已经打开的对话。
            if (session.ThreadId.Equals(_runtime.Session.ThreadId, StringComparison.OrdinalIgnoreCase))
                return;

            IsLoading = true;
            CommandManager.InvalidateRequerySuggested();

            try
            {
                // 0. Flush the session we're leaving without blocking on optional LLM summarization.
                // 0. 刷新即将离开的会话，但不等待可选的 LLM 摘要。
                FinalizeCurrentSessionForSwitch("ResumeAnotherSession");

                // 1. Capture current map info before invalidating the cache.
                //    the reconciler needs it, and after invalidation it's empty.
                // 1. 缓存失效前捕获当前地图信息；协调器需要该信息，而失效后缓存将为空。
                string currentMapInfo = _contextManager.GetCachedContext().MapInfo ?? "";

                // 2. Restore the checkpoint: messages, plan, permission, turn count.
                //    This resumes the same thread; new checkpoints continue under it.
                // 2. 恢复检查点中的消息、计划、权限和轮次计数；继续同一线程，
                //    新检查点仍归入该线程。
                var restored = await _runtime.RestoreCheckpointAsync(session.ThreadId);
                if (restored == null)
                {
                    AddSystem($"未找到会话（{session.DateLabel}）的检查点，无法恢复。");
                    await ActivateSessionLeaseAsync(_session.ThreadId);
                    return;
                }

                _contextManager.InvalidateCache();

                // 3. Rebuild the chat timeline from the restored messages.
                // 3. 根据恢复的消息重建聊天时间线。
                RestoreConversationFromCheckpoint(restored);

                _transcript.WriteSessionResume(restored.ThreadId, _selectedModel, _selectedThinking,
                    string.IsNullOrWhiteSpace(_projFolder) ? null : _projFolder);
                await ActivateSessionLeaseAsync(restored.ThreadId);

                AddSystem($"已恢复历史会话：{session.DateLabel} | 第 {restored.Turn} 轮");

                // 4. State reconciliation: past layers/tools vs current project.
                //    Tools come from the checkpoint (exact); layers from the
                //    transcript slice of this thread, if the file can be located.
                // 4. 状态协调：比较历史图层/工具与当前项目。工具精确取自检查点；
                //    若能找到文件，图层取自该线程对应的 transcript 片段。
                var pastTools = restored.Messages
                    .Where(m => m.ToolCalls != null)
                    .SelectMany(m => m.ToolCalls!)
                    .Select(c => c.Name)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .ToList();

                string? transcriptPath = string.IsNullOrWhiteSpace(_projFolder)
                    ? null
                    : StateReconciler.FindTranscriptForThread(
                        Path.Combine(_projFolder, "agent_logs"), session.ThreadId);

                var reconciliation = StateReconciler.Reconcile(
                    transcriptPath, currentMapInfo, session.ThreadId, pastTools: pastTools);

                // Only surface + inject the delta when the project actually changed.
                // Project memory is not injected here; ContextManager already puts
                // it into the system prompt every turn.
                // 仅在项目实际变化时显示并注入差异。此处不注入项目记忆，
                // 因为 ContextManager 每轮都会把它加入系统提示词。
                if (reconciliation.NeedsAttention)
                {
                    string delta = reconciliation.ToPromptInjection();
                    AddSystem(delta);
                    _session.AddMessage(new Message { Role = "system", Content = delta });
                }

                RefreshSessionList();
                UpdateStatusBar();
            }
            catch (Exception ex)
            {
                AddMessage("system", $"会话恢复失败：{ex.Message}");
                Debug.WriteLine($"[ViewModel] ContinueFromSessionAsync 异常：{ex}");
            }
            finally
            {
                IsLoading = false;
                CommandManager.InvalidateRequerySuggested();
                // Reset dropdown selection so the user can select it again.
                // 重置下拉列表选择，以便用户再次选择。
                SelectedSessionInfo = null;
            }
        }

        private void ResetConversationUi()
        {
            UserInput = "";
            IsTimelinePanelExpanded = false;
            TimelineItems.Clear();
            NotifyTimelineStateChanged();
            _traceGroups.Clear();
            _traceIndex.Clear();
            PendingTools.Clear();
            ClearApprovalState();
            _activeTurn = 0;
            _currentTaskTurn = 0;
            TaskTitle = "等待任务";
            TaskSubtitle = "输入业务目标后，Agent 会先分析，再生成执行计划并进入工具执行与验证。";
            SetTaskStage("空闲", "尚未开始", "#4B6484");
            TaskOutcome = "";
            RefreshDashboardMetrics();
        }

        private void RestoreConversationFromCheckpoint(AgentRunState restored)
        {
            ResetConversationUi();

            SelectedPermissionLevel = PermissionLabelFromLevel(restored.PermissionLevel);
            UserInput = "";
            _pendingRequestText = restored.UserMessage ?? "";
            TaskTitle = string.IsNullOrWhiteSpace(restored.UserMessage)
                ? "已恢复历史会话"
                : Truncate(restored.UserMessage, 96);
            TaskSubtitle = $"已恢复到第 {restored.Turn} 轮，可继续沿原上下文工作。";
            SetTaskStage(
                restored.Status == SessionStatus.WaitingForApproval ? "等待决策" : "已恢复",
                restored.Status == SessionStatus.WaitingForApproval
                    ? "该会话恢复后仍停在审批节点，需要先做决策。"
                    : "会话与轨迹已恢复，可继续执行或追加新要求。",
                restored.Status == SessionStatus.WaitingForApproval ? "#A15C00" : "#0B69A3");

            foreach (var message in _runtime.Session.Messages)
            {
                // Skip tool results and tool-call-only assistant turns.
                // they have no displayable text and would render empty bubbles.
                // 跳过工具结果和仅含工具调用的助手轮次；它们没有可显示文本，
                // 否则会渲染为空气泡。
                if (message.Role == "tool" || string.IsNullOrWhiteSpace(message.Content))
                    continue;

                OnUi(() =>
                {
                    var chat = new ChatMessage(message.Role, message.Content)
                    {
                        Timestamp = message.Timestamp
                    };
                    var item = TimelineItem.Message(chat);
                    TimelineItems.Add(item);
                    NotifyTimelineStateChanged();
                });
            }

            if (restored.Status == SessionStatus.WaitingForApproval)
                ApplyApprovalState(ApprovalUiStateMachine.FromCheckpoint(restored));
            else
                ClearApprovalState();

            UpdateStatusBar();
        }

        private Task SendAsync()
        {
            string text = UserInput?.Trim() ?? "";
            return SendAsync(text, appendUserMessage: true, showUserBubble: true, allowQuickCommands: true);
        }

        private async Task SendAsync(
            string text,
            bool appendUserMessage,
            bool showUserBubble,
            bool allowQuickCommands,
            AgentPermissionLevel? restorePermissionAfterRun = null)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (allowQuickCommands && await HandleQuickCommand(text)) return;
            if (!_llm.IsConfigured) { AddSystem("请先输入 API Key"); return; }

            if (showUserBubble)
            {
                AddMessage("user", text);
                UserInput = "";
            }
            BeginTaskRun(text);
            IsLoading = true;
            CommandManager.InvalidateRequerySuggested();
            _currentRunCts?.Dispose();
            _currentRunCts = new CancellationTokenSource();

            try
            {
                var resp = await _runtime.ProcessAsync(new TaskRequest
                {
                    UserMessage = text,
                    AppendUserMessage = appendUserMessage,
                    Cancellation = _currentRunCts.Token
                });

                HandleAgentResponse(resp, text);
            }
            catch (OperationCanceledException)
            {
                AddSystem("当前任务已停止。");
                SetTaskStage("已停止", "用户手动停止了当前任务，本轮不会继续执行。", "#A15C00");
                TaskOutcome = "当前任务已停止。你可以补充约束后重新发起，或继续查看已生成的轨迹。";
            }
            catch (Exception ex)
            {
                AddMessage("system", $"错误：{ex.Message}");
                Debug.WriteLine($"[ViewModel] SendAsync 异常：{ex}");
                SetTaskStage("运行异常", ex.Message, "#B03A2E");
                TaskOutcome = Truncate(ex.Message, 220);
            }
            finally
            {
                _currentRunCts?.Dispose();
                _currentRunCts = null;
                if (restorePermissionAfterRun.HasValue)
                    SelectedPermissionLevel = PermissionLabelFromLevel(restorePermissionAfterRun.Value);
                IsLoading = false;
                UpdateStatusBar();
                CommandManager.InvalidateRequerySuggested();
                OnNewMessageAdded?.Invoke();
            }
        }

        private async Task ApprovePendingAsync()
        {
            if (!HasPendingApproval || string.IsNullOrWhiteSpace(_pendingRequestText))
                return;

            string requestText = _pendingRequestText;
            string toolsText = PendingTools.Count > 0 ? string.Join("、", PendingTools) : "待审批工具";
            var approvalDecision = new ApprovalDecision
            {
                TicketId = _pendingApprovalTicket?.Id ?? "",
                Decision = ApprovalDecisionKind.Approve,
                Scope = _pendingApprovalTicket?.Scope ?? ApprovalScope.ExactCall,
                Reason = "ApprovedFromProPanel",
                DecidedAtUtc = DateTime.UtcNow
            };

            UserInput = "";
            ClearApprovalState();
            IsTimelinePanelExpanded = false;
            BeginTaskRun(requestText, isApprovalResumption: true);

            _currentRunCts?.Dispose();
            _currentRunCts = new CancellationTokenSource();
            IsLoading = true;
            CommandManager.InvalidateRequerySuggested();

            try
            {
                var resp = await _runtime.ProcessAsync(new TaskRequest
                {
                    UserMessage = requestText,
                    AppendUserMessage = false,
                    IsApprovalResumption = true,
                    Approved = true,
                    ApprovalDecision = approvalDecision,
                    Cancellation = _currentRunCts.Token
                });

                HandleAgentResponse(resp, requestText);
            }
            catch (OperationCanceledException)
            {
                AddSystem("当前任务已停止。");
                SetTaskStage("已停止", "审批恢复后的执行被手动中止。", "#A15C00");
                TaskOutcome = "审批恢复后的执行已停止。可根据轨迹决定是否再次继续。";
            }
            catch (Exception ex)
            {
                AddMessage("system", $"错误：{ex.Message}");
                Debug.WriteLine($"[ViewModel] ApprovePendingAsync 异常：{ex}");
                SetTaskStage("恢复异常", ex.Message, "#B03A2E");
                TaskOutcome = Truncate(ex.Message, 220);
            }
            finally
            {
                _currentRunCts?.Dispose();
                _currentRunCts = null;
                IsLoading = false;
                UpdateStatusBar();
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private void RejectPending()
        {
            if (!HasPendingApproval)
                return;

            string toolsText = PendingTools.Count > 0 ? string.Join("、", PendingTools) : "待审批工具";
            _runtime.RejectPendingApproval(_pendingApprovalTicket);
            SetTaskStage("已拒绝", $"你已拒绝：{toolsText}", "#B03A2E");
            TaskOutcome = "本轮高风险步骤已被拒绝，相关调用会在当前轮次保持阻断。";
            ClearApprovalState();
            IsTimelinePanelExpanded = false;
            OnNewMessageAdded?.Invoke();
        }

        private void HandleAgentResponse(AgentResponse resp, string requestText)
        {
            // 本轮 Token（单轮展示，时间戳取当前时刻）。
            // Tokens for this turn only, displayed with the current timestamp.
            string? tokenInfo = BuildPerTurnTokenInfo();

            // ★ 审批中不展示 assistant 消息气泡——审批卡片 + 执行链内 trace 已传达全部信息。
            // LLM 在审批触发后生成的指引文本不应出现在聊天区。
            // ★ Do not show an assistant message bubble while approval is pending—the approval card and execution trace
            // already convey all information. Guidance generated after the approval trigger must stay out of chat.
            if (!resp.RequiresApproval)
            {
                if (!string.IsNullOrWhiteSpace(resp.UserReply))
                    AddMessage("assistant", resp.UserReply, tokenInfo);
                else if (!string.IsNullOrWhiteSpace(resp.ExecutionSummary))
                    AddMessage("assistant", $"操作完成：\n{resp.ExecutionSummary}", tokenInfo);
                else
                    AddMessage("assistant", "操作已完成。", tokenInfo);
            }

            if (resp.RequiresApproval)
                BeginApprovalWait(resp, requestText);
            else if (HasPendingApproval)
                ClearApprovalState();

            UpdateTaskOutcomeFromResponse(resp);
            RefreshBackupSize();

            // ★ Incremental memory persistence: save after every response so work
            // survives Pro crashes mid-session (complements checkpoint, which only
            // saves message/plan state, not cross-session memory).
            // ★ 增量记忆持久化：每次响应后保存，使工作在 Pro 会话中途崩溃时仍可恢复；
            // 它补充了只保存消息/计划状态而不保存跨会话记忆的检查点机制。
            SaveMemorySnapshot();
        }

        private void BeginApprovalWait(AgentResponse resp, string requestText)
        {
            ApplyApprovalState(ApprovalUiStateMachine.FromResponse(resp, requestText));
            SetTaskStage("等待决策", "运行时已暂停，请确认影响范围、目标对象和后续动作。", "#A15C00");
            TaskSubtitle = "已进入审批等待态，批准后将从当前步骤继续，而不是重跑整轮。";
            CommandManager.InvalidateRequerySuggested();
        }

        private void ClearApprovalState()
        {
            ApplyApprovalState(ApprovalUiStateMachine.Empty());
            RefreshDashboardMetrics();
        }

        private void ApplyApprovalState(ApprovalUiState state)
        {
            PendingTools.Clear();
            foreach (var tool in state.PendingTools)
                PendingTools.Add(tool);

            PendingApprovalText = state.PendingApprovalText;
            PendingApprovalReason = state.PendingApprovalReason;
            _pendingRequestText = state.PendingRequestText;
            _pendingApprovalTicket = state.PendingApprovalTicket;
            HasPendingApproval = state.HasPendingApproval;
            if (state.HasPendingApproval)
                IsTimelinePanelExpanded = true;
            if (state.HasPendingApproval)
                TaskOutcome = "";
            CommandManager.InvalidateRequerySuggested();
        }

        private void SubscribeTraceEvents()
        {
            // ================================================================
            // 阶段 1 操作流式传输：完整事件到时间线的映射。
            // Phase 1 operational streaming: complete event-to-timeline mapping.
            // ================================================================

            // ── 轮次生命周期 / Turn lifecycle ──
            _eventBus.Subscribe("turn.start", e => OnUi(() =>
            {
                _activeTurn = e.Turn ?? _activeTurn;
                _currentTaskTurn = _activeTurn;
                SetTaskStage("收集上下文", $"已开始第 {_activeTurn} 轮，正在准备上下文与执行边界。", "#4B6484");
                RefreshDashboardMetrics();
            }));

            // ── 大语言模型 / LLM ──
            _eventBus.Subscribe("llm.calling", e => OnUi(() =>
            {
                int turn = e.Turn ?? _activeTurn;
                var group = EnsureTraceGroup(turn);
                var round = e.Metadata?.GetValueOrDefault("round", "?");
                group.TraceHeader = round == "0"
                    ? "执行轨迹 | AI 分析中"
                    : $"执行轨迹 | 第 {round} 轮 | 继续执行中";
                SetTaskStage(
                    "分析中",
                    round == "0" ? "正在生成首版执行计划。" : $"正在根据前序结果修正计划（第 {round} 轮）。",
                    "#4B6484");
                TaskSubtitle = "系统会先给出可执行步骤，再进入工具调用与验证。";
            }));

            _eventBus.Subscribe("llm.progress", e => OnUi(() =>
            {
                string message = e.Data?.GetValueOrDefault("message")?.ToString()
                    ?? "模型仍在推理。";
                bool stalled = string.Equals(
                    e.Metadata?.GetValueOrDefault("stalled", "false"),
                    "true",
                    StringComparison.OrdinalIgnoreCase);
                SetTaskStage(
                    stalled ? "推理耗时较长" : "分析中",
                    message,
                    stalled ? "#A86A00" : "#4B6484");
                TaskSubtitle = message;
            }));

            _eventBus.Subscribe("plan.generated", e => OnUi(() =>
            {
                UpdatePlanCard(e);
                var lines = BuildPlanLines(e);
                if (lines.Count > 0)
                {
                    TaskSubtitle = lines.Count == 1
                        ? lines[0]
                        : $"{lines[0]} 等 {lines.Count} 个步骤";
                    SetTaskStage("计划已生成", "执行顺序已确定，接下来将按步骤推进并持续验证。", "#0B69A3");
                }
            }));

            // ── 工具生命周期 / Tool lifecycle ──
            _eventBus.Subscribe("tool.started", e => OnUi(() =>
            {
                var item = EnsureTraceItem(e);
                item.Status = ToolTraceStatus.Running;
                item.RiskLevel = e.Metadata?.GetValueOrDefault("risk", "") ?? "";
                item.ArgsPreview = Preview(e.Data?.GetValueOrDefault("args"));
                item.OperationDesc = DescribeOperation(
                    e.Metadata?.GetValueOrDefault("tool", "") ?? "",
                    e.Data?.GetValueOrDefault("args"));
                item.ProgressText = "正在执行";
                SetTaskStage("执行中", item.OperationDesc, "#0B69A3");
                UpdateTraceGroup(item.Turn);
            }));

            _eventBus.Subscribe("tool.completed", e => OnUi(() =>
            {
                var item = EnsureTraceItem(e);
                item.Status = item.Status == ToolTraceStatus.Warned
                    ? ToolTraceStatus.Warned
                    : ToolTraceStatus.Completed;
                item.ElapsedMs = ParseLong(e.Metadata?.GetValueOrDefault("elapsed_ms"));
                item.OutputPreview = Preview(e.Data?.GetValueOrDefault("output"));
                item.Error = e.Data?.GetValueOrDefault("error")?.ToString() ?? "";
                item.ProgressText = string.IsNullOrWhiteSpace(item.ValidationSummary)
                    ? "执行完成"
                    : "执行与验证完成";
                SetTaskStage("执行中", $"已完成：{item.OperationDesc}", "#0B69A3");
                UpdateTraceGroup(item.Turn);
                RefreshBackupSize();
            }));

            _eventBus.Subscribe("tool.failed", e => OnUi(() =>
            {
                var item = EnsureTraceItem(e);
                item.Status = ToolTraceStatus.Failed;
                item.ElapsedMs = ParseLong(e.Metadata?.GetValueOrDefault("elapsed_ms"));
                item.OutputPreview = Preview(e.Data?.GetValueOrDefault("output"));
                item.Error = e.Data?.GetValueOrDefault("error")?.ToString() ?? "";
                item.ProgressText = "执行失败";
                item.IsExpanded = true;
                SetTaskStage("需要处理", $"失败：{item.OperationDesc}", "#B03A2E");
                UpdateTraceGroup(item.Turn);
            }));

            _eventBus.Subscribe("tool.param_autocorrected", e => OnUi(() =>
            {
                var item = EnsureTraceItem(e);
                var corrections = e.Data?.GetValueOrDefault("corrections")?.ToString() ?? "";
                if (!string.IsNullOrWhiteSpace(corrections))
                    item.ArgsPreview = $"{item.ArgsPreview}（已纠正：{corrections}）";
            }));

            _eventBus.Subscribe("tool.progress", e => OnUi(() =>
            {
                var item = EnsureTraceItem(e);
                string phase = e.Metadata?.GetValueOrDefault("phase", "") ?? "";
                bool stalled = string.Equals(
                    e.Metadata?.GetValueOrDefault("stalled", "false"),
                    "true",
                    StringComparison.OrdinalIgnoreCase);
                item.Status = string.Equals(phase, "approval", StringComparison.OrdinalIgnoreCase)
                    ? ToolTraceStatus.AwaitingApproval
                    : item.Status == ToolTraceStatus.AwaitingApproval
                        ? ToolTraceStatus.AwaitingApproval
                        : ToolTraceStatus.Running;
                var pct = e.Metadata?.GetValueOrDefault("progress_pct", "") ?? "";
                var msg = e.Data?.GetValueOrDefault("message")?.ToString() ?? "";
                item.ProgressText = string.IsNullOrWhiteSpace(pct)
                    ? msg
                    : string.IsNullOrWhiteSpace(msg)
                        ? $"进度 {pct}%"
                        : $"进度 {pct}%：{msg}";
                if (!string.IsNullOrWhiteSpace(item.ProgressText))
                    item.IsExpanded = true;
                if (!string.IsNullOrWhiteSpace(item.ProgressText))
                    SetTaskStage(
                        string.Equals(phase, "approval", StringComparison.OrdinalIgnoreCase)
                            ? "等待决策"
                            : stalled
                                ? "宿主仍在执行"
                                : "执行中",
                        item.ProgressText,
                        string.Equals(phase, "approval", StringComparison.OrdinalIgnoreCase) ? "#A15C00" : "#0B69A3");
                UpdateTraceGroup(item.Turn);
            }));

            _eventBus.Subscribe("execution.host_state_changed", e => OnUi(() =>
            {
                var item = EnsureTraceItem(e);
                string reason = e.Data?.GetValueOrDefault("reason")?.ToString()
                    ?? "检测到 ArcGIS Pro 地图或数据状态已发生变化。";
                item.Status = ToolTraceStatus.Blocked;
                item.Error = reason;
                item.ProgressText = "状态已变化，本批次已停止";
                item.IsExpanded = true;
                IsTimelinePanelExpanded = true;
                SetTaskStage(
                    "状态已变化",
                    "已停止使用旧计划继续写入；后续将基于当前地图重新评估。",
                    "#A15C00");
                UpdateTraceGroup(item.Turn);
            }));

            _eventBus.Subscribe("execution.infrastructure_baseline_adopted", e => OnUi(() =>
            {
                var item = EnsureTraceItem(e);
                item.ProgressText = "安全备份完成，执行基线已刷新";
                UpdateTraceGroup(item.Turn);
            }));

            _eventBus.Subscribe("runtime.gp_warmup", e => OnUi(() =>
            {
                string phase = e.Metadata?.GetValueOrDefault("phase", "") ?? "";
                string message = e.Data?.GetValueOrDefault("message")?.ToString() ?? "";
                if (!IsLoading && !string.IsNullOrWhiteSpace(message))
                {
                    TaskSubtitle = message;
                    if (string.Equals(phase, "starting", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(phase, "executing", StringComparison.OrdinalIgnoreCase))
                    {
                        SetTaskStage("准备宿主", message, "#4B6484");
                    }
                    else if (string.Equals(phase, "ready", StringComparison.OrdinalIgnoreCase))
                    {
                        SetTaskStage("空闲", "宿主能力已就绪", "#4B6484");
                    }
                    else if (string.Equals(phase, "failed", StringComparison.OrdinalIgnoreCase))
                    {
                        SetTaskStage("空闲", message, "#A15C00");
                    }
                }
            }));

            // ── 安全 / Safety ──
            _eventBus.Subscribe("safety.blocked", e => OnUi(() =>
            {
                var item = EnsureTraceItem(e);
                item.Status = ToolTraceStatus.Blocked;
                item.RiskLevel = e.Metadata?.GetValueOrDefault("risk", "") ?? "";
                item.ArgsPreview = Preview(e.Data?.GetValueOrDefault("args"));
                item.Error = e.Data?.GetValueOrDefault("block_reason")?.ToString() ?? "";
                item.ProgressText = "已阻止执行";
                item.IsExpanded = true;
                SetTaskStage("已阻止", item.Error, "#B03A2E");
                UpdateTraceGroup(item.Turn);
            }));

            _eventBus.Subscribe("safety.approval_required", e => OnUi(() =>
            {
                var item = EnsureTraceItem(e);
                item.Status = ToolTraceStatus.AwaitingApproval;
                item.RiskLevel = e.Metadata?.GetValueOrDefault("risk", "") ?? "";
                item.ArgsPreview = Preview(e.Data?.GetValueOrDefault("args"));
                item.Error = BuildApprovalTraceText(e);
                item.ProgressText = "等待授权";
                item.IsExpanded = true;
                StampApprovalIdentity(item, e);
                SetTaskStage("等待决策", item.OperationDesc, "#A15C00");
                TaskSubtitle = "当前步骤需要人工确认，批准后将从该步骤继续执行。";
                UpdateTraceGroup(item.Turn);
            }));

            _eventBus.Subscribe("safety.influence_approval_required", e => OnUi(() =>
            {
                var item = EnsureTraceItem(e);
                item.Status = ToolTraceStatus.AwaitingApproval;
                item.RiskLevel = e.Metadata?.GetValueOrDefault("risk", "") ?? "";
                item.ArgsPreview = Preview(e.Data?.GetValueOrDefault("args"));
                item.Error = BuildApprovalTraceText(e);
                item.ProgressText = "等待授权";
                item.IsExpanded = true;
                StampApprovalIdentity(item, e);
                SetTaskStage("等待决策", item.OperationDesc, "#A15C00");
                TaskSubtitle = "系统检测到较大数据影响范围，需先确认再继续。";
                UpdateTraceGroup(item.Turn);
            }));

            _eventBus.Subscribe("safety.approval_resumed", e => OnUi(() =>
            {
                bool approved = e.Metadata?.GetValueOrDefault("approved", "false") == "true";
                string ticketId = e.Metadata?.GetValueOrDefault("ticket_id", "") ?? "";
                if (!approved)
                {
                    foreach (var item in FindTraceItemsByApproval(ticketId))
                    {
                        item.Status = ToolTraceStatus.Blocked;
                        item.Error = AppendSuffix(item.Error, "（用户已拒绝）");
                        item.ProgressText = "授权已拒绝";
                        item.IsExpanded = true;
                        UpdateTraceGroup(item.Turn);
                    }
                    SetTaskStage("已拒绝", "用户拒绝了当前审批步骤，运行不会继续。", "#B03A2E");
                }
                else
                {
                    SetTaskStage("恢复执行", "审批已通过，系统正在继续原步骤。", "#0B69A3");
                }
                ClearApprovalState();
            }));

            _eventBus.Subscribe("safety.approval_rejected", e => OnUi(() =>
            {
                string ticketId = e.Metadata?.GetValueOrDefault("ticket_id", "") ?? "";
                foreach (var item in FindTraceItemsByApproval(ticketId))
                {
                    item.Status = ToolTraceStatus.Blocked;
                    item.Error = AppendSuffix(item.Error, "（用户已拒绝）");
                    item.ProgressText = "授权已拒绝";
                    item.IsExpanded = true;
                    UpdateTraceGroup(item.Turn);
                }
                SetTaskStage("已拒绝", "当前审批已被拒绝，相关工具调用会在本轮保持阻断。", "#B03A2E");
                ClearApprovalState();
            }));

            _eventBus.Subscribe("safety.risk_upgraded", e => OnUi(() =>
            {
                string origTool = e.Metadata?.GetValueOrDefault("original_tool", "?") ?? "?";
                string resolvedTool = e.Metadata?.GetValueOrDefault("resolved_tool", "?") ?? "?";
                string upgradedRisk = e.Metadata?.GetValueOrDefault("upgraded_risk", "?") ?? "?";
                AddSystem($"工具名解析导致风险升级：{origTool} -> {resolvedTool}，风险级别升级至 {upgradedRisk}");
            }));

            // ── 核验 / Verification ──
            _eventBus.Subscribe("verification.passed", e => OnUi(() =>
            {
                var item = EnsureTraceItem(e);
                item.ValidationSummary = BuildVerificationSummary("PASS", e);
                item.ProgressText = "验证通过";
                SetTaskStage("验证中", $"已验证：{item.OperationDesc}", "#2E7D32");
                UpdateTraceGroup(item.Turn);
            }));

            _eventBus.Subscribe("verification.failed", e => OnUi(() =>
            {
                var item = EnsureTraceItem(e);
                item.ValidationSummary = BuildVerificationSummary("FAIL", e);
                item.ProgressText = "验证失败";
                item.IsExpanded = true;
                SetTaskStage("需要复核", $"验证失败：{item.OperationDesc}", "#B03A2E");
                UpdateTraceGroup(item.Turn);
            }));

            // ── 验证 / Validation ──
            _eventBus.Subscribe("validation.failed", e => OnUi(() =>
            {
                var item = EnsureTraceItem(e);
                item.Status = ToolTraceStatus.Failed;
                item.ValidationSummary = BuildValidationText("FAIL", e);
                item.IsExpanded = true;
                UpdateTraceGroup(item.Turn);
            }));

            _eventBus.Subscribe("validation.warned", e => OnUi(() =>
            {
                var item = EnsureTraceItem(e);
                if (item.Status != ToolTraceStatus.Failed)
                    item.Status = ToolTraceStatus.Warned;
                item.ValidationSummary = BuildValidationText("WARN", e);
                SetTaskStage("存在警告", $"请复核：{item.OperationDesc}", "#A15C00");
                UpdateTraceGroup(item.Turn);
            }));

            // ── 工作区 / Workspace ──
            _eventBus.Subscribe("workspace.backup_created", e => OnUi(() =>
            {
                var item = EnsureTraceItem(e);
                string path = e.Metadata?.GetValueOrDefault("path", "") ?? "";
                item.ArgsPreview = $"{item.ArgsPreview}（已备份：{path}）";
                RefreshBackupSize();
            }));

            // ── 检查点 / Checkpoint ──
            _eventBus.Subscribe("checkpoint.restored", e => OnUi(() =>
            {
                int turn = e.Turn ?? 0;
                string status = e.Metadata?.GetValueOrDefault("status", "?") ?? "?";
                AddSystem($"已恢复会话状态（Turn {turn}，状态：{status}）");
            }));

            // ── 诊断 / Diagnostics ──
            _eventBus.Subscribe("diagnostics.info", e =>
            {
                // 诊断信息仅写 Debug 和 transcript，不出现在聊天区。
                // AgentDiagnostics.Info 的调用点已在 Scheduler/TranscriptWriter 中
                // 通过独立事件落实审计，不需要在对话气泡中重复展示。
                // Diagnostic information is written only to Debug and the transcript, not to chat.
                // AgentDiagnostics.Info call sites already emit independent audit events in Scheduler/TranscriptWriter,
                // so the conversation bubble does not need to repeat them.
                var msg = e.Metadata?.GetValueOrDefault("message", "") ?? "";
                if (!string.IsNullOrWhiteSpace(msg))
                    Debug.WriteLine($"[Agent] {msg}");
            });

            _eventBus.Subscribe("diagnostics.error", e => OnUi(() =>
            {
                var area = e.Metadata?.GetValueOrDefault("area", "runtime") ?? "runtime";
                var msg = e.Metadata?.GetValueOrDefault("message", "") ?? "";
                var err = e.Metadata?.GetValueOrDefault("error", "") ?? "";
                AddSystem(string.IsNullOrWhiteSpace(err)
                    ? $"错误 {area}: {msg}"
                    : $"错误 {area}: {msg}（{err}）");
            }));
        }

        // ── 阶段 1 辅助方法 / Phase 1 helper methods ──

        /// <summary>
        /// 将审批身份写入 trace item，供恢复/拒绝时精确定位。
        /// Writes approval identity into the trace item for precise lookup during resume or rejection.
        /// </summary>
        private static void StampApprovalIdentity(ToolTraceItem item, AgentEvent e)
        {
            item.ApprovalTicketId = e.Metadata?.GetValueOrDefault("ticket_id", "") ?? "";
            item.ApprovalCallSignature = e.Metadata?.GetValueOrDefault("call_signature", "") ?? "";
        }

        private static string BuildApprovalTraceText(AgentEvent e)
        {
            if (e.Data?.TryGetValue("ticket", out var rawTicket) == true
                && rawTicket is ApprovalTicket ticket)
            {
                return ApprovalCopyBuilder.BuildReason(ticket);
            }

            var args = e.Data?.GetValueOrDefault("args") as IReadOnlyDictionary<string, object?>
                       ?? e.Data?.GetValueOrDefault("args") as Dictionary<string, object?>
                       ?? new Dictionary<string, object?>();

            var target = new ApprovalTarget
            {
                Layer = FirstApprovalValue(
                    e.Metadata?.GetValueOrDefault("layer"),
                    args.GetValueOrDefault("layer_name")?.ToString(),
                    args.GetValueOrDefault("in_table")?.ToString(),
                    args.GetValueOrDefault("in_dataset")?.ToString(),
                    args.GetValueOrDefault("in_features")?.ToString(),
                    args.GetValueOrDefault("input_features")?.ToString()),
                Dataset = FirstApprovalValue(
                    args.GetValueOrDefault("in_table")?.ToString(),
                    args.GetValueOrDefault("in_dataset")?.ToString(),
                    args.GetValueOrDefault("input_table")?.ToString()),
                Field = FirstApprovalValue(
                    e.Metadata?.GetValueOrDefault("field"),
                    args.GetValueOrDefault("field_name")?.ToString(),
                    args.GetValueOrDefault("field")?.ToString()),
                WhereClause = FirstApprovalValue(
                    args.GetValueOrDefault("where_clause")?.ToString(),
                    args.GetValueOrDefault("expression")?.ToString()),
                OutputTarget = FirstApprovalValue(
                    args.GetValueOrDefault("out_table")?.ToString(),
                    args.GetValueOrDefault("out_feature_class")?.ToString())
            };

            var evidence = new ApprovalEvidence
            {
                IsReliable = !string.Equals(e.Metadata?.GetValueOrDefault("reliable"), "false", StringComparison.OrdinalIgnoreCase),
                ReliabilityReason = e.Data?.GetValueOrDefault("reason")?.ToString() ?? "",
                InfluenceCount = ParseNullableInt(e.Metadata?.GetValueOrDefault("influence_count")),
                ScopeObjectCount = ParseNullableInt(e.Metadata?.GetValueOrDefault("scope_object_count")),
                ExistingNonEmptyTargetCount = ParseNullableInt(e.Metadata?.GetValueOrDefault("existing_nonempty_target_count")),
                NewValueFillCount = ParseNullableInt(e.Metadata?.GetValueOrDefault("new_value_fill_count")),
                SchemaMutationCount = ParseNullableInt(e.Metadata?.GetValueOrDefault("schema_mutation_count")),
                UsesSelectionScope = string.Equals(e.Metadata?.GetValueOrDefault("uses_selection_scope"), "true", StringComparison.OrdinalIgnoreCase),
                UsesFilteredScope = string.Equals(e.Metadata?.GetValueOrDefault("uses_filtered_scope"), "true", StringComparison.OrdinalIgnoreCase)
            };

            var syntheticTicket = new ApprovalTicket
            {
                Kind = string.Equals(e.Metadata?.GetValueOrDefault("approval_kind"), "Destructive", StringComparison.OrdinalIgnoreCase)
                    ? ApprovalKind.Destructive
                    : ApprovalKind.DataInfluence,
                ToolName = e.Metadata?.GetValueOrDefault("tool", "") ?? "",
                CanonicalToolName = e.Metadata?.GetValueOrDefault("tool", "") ?? "",
                Target = target,
                Evidence = evidence
            };

            return ApprovalCopyBuilder.BuildReason(syntheticTicket);
        }

        private static void AppendIfMeaningful(List<string> details, string label, string? rawValue)
        {
            if (string.IsNullOrWhiteSpace(rawValue) || rawValue == "?")
                return;

            details.Add($"{label} {rawValue}");
        }

        private static string FirstApprovalValue(params string?[] values) =>
            values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v) && v != "?")?.Trim() ?? "";

        private static int? ParseNullableInt(string? rawValue)
        {
            if (string.IsNullOrWhiteSpace(rawValue) || rawValue == "?")
                return null;

            return int.TryParse(rawValue, out var parsed) ? parsed : null;
        }

        /// <summary>工具名到领域操作描述的 UI 层映射；不修改 Runtime。</summary>
        private static string DescribeOperation(string toolName, object? argsObj)
        {
            var args = argsObj as Dictionary<string, object?>
                       ?? new Dictionary<string, object?>();

            string layer = (args.GetValueOrDefault("layer_name")
                            ?? args.GetValueOrDefault("in_table")
                            ?? args.GetValueOrDefault("in_dataset")
                            ?? args.GetValueOrDefault("in_features")
                            ?? args.GetValueOrDefault("input_features")
                            ?? "?")?.ToString() ?? "?";

            string field = (args.GetValueOrDefault("field_name")
                            ?? args.GetValueOrDefault("field")
                            ?? "")?.ToString() ?? "";

            string shortName = toolName.Contains('_')
                ? toolName[(toolName.LastIndexOf('_') + 1)..]
                : toolName;

            if (!string.IsNullOrWhiteSpace(field))
            {
                return shortName switch
                {
                    "CalculateField" => $"计算字段 {layer}.{field}",
                    "AddField" => $"添加字段 {layer}.{field}",
                    "CalculateArea" => $"计算面积 {layer} -> {field}",
                    "CalculateLength" => $"计算长度 {layer} -> {field}",
                    "MapFieldValues" => $"字段映射 {layer}.{field}",
                    "DeleteField" => $"删除字段 {layer}.{field}",
                    _ => $"{shortName} {layer}.{field}"
                };
            }

            return shortName switch
            {
                "Statistics" => $"统计汇总 {layer}",
                "Buffer" => $"缓冲区 {layer}",
                "Clip" => $"裁剪 {layer}",
                "Intersect" => $"相交 {layer}",
                "Erase" => $"擦除 {layer}",
                "Dissolve" => $"融合 {layer}",
                "Merge" => $"合并 {layer}",
                "Project" => $"投影转换 {layer}",
                "SelectLayerByAttribute" => $"按属性选择 {layer}",
                "CopyFeatures" => $"复制要素 {layer}",
                "GetMapInfo" => "获取地图信息",
                "ListLayers" => "列出图层",
                "DescribeLayer" => $"查看图层 {layer}",
                "GetProjectInfo" => "获取项目信息",
                "GetSelection" => "获取选择集",
                "ReadRows" => $"读取行 {layer}",
                "LookupLandCode" => "查询用地代码",
                _ => $"{shortName} {layer}"
            };
        }

        private static string BuildVerificationSummary(string verdict, AgentEvent e)
        {
            string rule = e.Metadata?.GetValueOrDefault("rule", "?") ?? "?";
            string target = e.Metadata?.GetValueOrDefault("target", "") ?? "";
            string detail = e.Metadata?.GetValueOrDefault("detail", "") ?? "";
            string elapsed = e.Data?.GetValueOrDefault("elapsed_ms")?.ToString() ?? "";
            string summary = verdict == "PASS"
                ? $"验证通过 | {detail}"
                : $"验证失败 | {detail}";
            if (!string.IsNullOrWhiteSpace(elapsed))
                summary += $" 路 {elapsed}ms";
            return summary;
        }

        private ToolTraceItem EnsureTraceItem(AgentEvent e)
        {
            int turn = e.Turn ?? _activeTurn;
            if (turn <= 0)
                turn = Math.Max(_runtime?.TurnCount ?? 0, 1);
            _activeTurn = turn;

            int step = ParseInt(e.Metadata?.GetValueOrDefault("step", "0"));
            if (step <= 0)
                step = 1;

            var key = (turn, step);
            if (_traceIndex.TryGetValue(key, out var existing))
                return existing;

            var group = EnsureTraceGroup(turn);
            var item = new ToolTraceItem
            {
                Turn = turn,
                Step = step,
                ToolName = e.Metadata?.GetValueOrDefault("tool", "?") ?? "?",
                Status = ToolTraceStatus.Running,
                Timestamp = DateTime.Now
            };

            _traceIndex[key] = item;
            group.TraceItems.Add(item);
            UpdateTraceGroup(turn);
            return item;
        }

        private IEnumerable<ToolTraceItem> FindTraceItemsByApproval(string ticketId)
        {
            if (string.IsNullOrWhiteSpace(ticketId))
                return Enumerable.Empty<ToolTraceItem>();

            return _traceGroups.Values
                .SelectMany(g => g.TraceItems)
                .Where(ti => string.Equals(ti.ApprovalTicketId, ticketId, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        private void UpdatePlanCard(AgentEvent e)
        {
            int turn = e.Turn ?? _activeTurn;
            if (turn <= 0)
                turn = Math.Max(_runtime?.TurnCount ?? 0, 1);
            _activeTurn = turn;

            string badge = e.Metadata?.GetValueOrDefault("risk_max", "") ?? "";
            var lines = BuildPlanLines(e);
            if (lines.Count == 0)
                return;

            // ★ 计划合并到轨迹组：找到或创建 trace group，把计划行写入 CardLines
            var group = EnsureTraceGroup(turn);
            group.CardTitle = $"执行计划 · 第 {turn} 轮";
            group.CardBadge = badge;
            group.CardLines.Clear();
            foreach (var line in lines)
                group.CardLines.Add(line);
        }

        private static List<string> BuildPlanLines(AgentEvent e)
        {
            var lines = new List<string>();
            if (e.Data?.TryGetValue("steps", out var rawSteps) != true || rawSteps == null)
                return lines;

            try
            {
                var token = Newtonsoft.Json.Linq.JToken.FromObject(rawSteps);
                if (token is not Newtonsoft.Json.Linq.JArray array)
                    return lines;

                foreach (var step in array)
                {
                    string stepNo = step["step"]?.ToString() ?? "?";
                    string tool = step["tool"]?.ToString() ?? "?";
                    string intent = step["intent"]?.ToString() ?? tool;
                    string risk = step["risk"]?.ToString() ?? "";
                    string line = $"{stepNo}. {intent}";
                    if (!string.IsNullOrWhiteSpace(risk))
                        line += $" [{risk}]";
                    if (!string.Equals(intent, tool, StringComparison.OrdinalIgnoreCase))
                        line += $" · {tool}";
                    lines.Add(line);
                }
            }
            catch
            {
                // Ignore malformed plan payloads — the rest of the timeline remains usable.
            }

            return lines;
        }

        private TimelineItem EnsureTraceGroup(int turn)
        {
            if (_traceGroups.TryGetValue(turn, out var group))
                return group;

            group = TimelineItem.TraceGroup(turn);
            _traceGroups[turn] = group;
            TimelineItems.Add(group);
            NotifyTimelineStateChanged();
            OnNewMessageAdded?.Invoke();
            return group;
        }

        private void UpdateTraceGroup(int turn)
        {
            if (!_traceGroups.TryGetValue(turn, out var group))
                return;

            int total = group.TraceItems.Count;
            int running = group.TraceItems.Count(i => i.Status == ToolTraceStatus.Running);
            int ok = group.TraceItems.Count(i => i.Status == ToolTraceStatus.Completed);
            int warn = group.TraceItems.Count(i => i.Status == ToolTraceStatus.Warned);
            int fail = group.TraceItems.Count(i => i.Status == ToolTraceStatus.Failed);
            int blocked = group.TraceItems.Count(i => i.Status == ToolTraceStatus.Blocked);
            int approval = group.TraceItems.Count(i => i.Status == ToolTraceStatus.AwaitingApproval);

            var parts = new List<string> { $"执行轨迹 | {total} 步" };
            if (running > 0) parts.Add($"执行中 {running}");
            if (ok > 0) parts.Add($"完成 {ok}");
            if (warn > 0) parts.Add($"警告 {warn}");
            if (fail > 0) parts.Add($"失败 {fail}");
            if (blocked > 0) parts.Add($"阻止 {blocked}");
            if (approval > 0) parts.Add($"待授权 {approval}");
            group.TraceHeader = string.Join(" · ", parts);
            group.RefreshHasRunningItem();

            // 轨迹组默认由用户手动展开；审批总面板会单独自动展开。
        }

        private void AddMessage(string role, string content, string? tokenInfo = null)
        {
            if (string.IsNullOrWhiteSpace(content))
                return;

            OnUi(() =>
            {
                var msg = new ChatMessage(role, content);
                var item = TimelineItem.Message(msg);
                if (tokenInfo != null)
                    item.TokenInfo = tokenInfo;
                TimelineItems.Add(item);
                NotifyTimelineStateChanged();
                OnNewMessageAdded?.Invoke();
            });
        }

        private void NotifyTimelineStateChanged()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasTimelineItems)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExecutionTimelineItems)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasExecutionTimelineItems)));
        }

        private static string AppendSuffix(string text, string suffix)
        {
            if (string.IsNullOrWhiteSpace(text))
                return suffix.Trim();

            return text.Contains(suffix, StringComparison.Ordinal)
                ? text
                : text + suffix;
        }

        private void OnUi(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
                dispatcher.BeginInvoke(action);
            else
                action();
        }

        private static string BuildValidationText(string verdict, AgentEvent e)
        {
            string rule = e.Metadata?.GetValueOrDefault("rule", "?") ?? "?";
            string detail = e.Metadata?.GetValueOrDefault("detail", "") ?? "";
            return $"{verdict} {rule}: {detail}".Trim();
        }

        private static int ParseInt(string? value) =>
            int.TryParse(value, out var parsed) ? parsed : 0;

        private static long ParseLong(string? value) =>
            long.TryParse(value, out var parsed) ? parsed : 0;

        private static string Preview(object? value, int maxLength = 420)
        {
            if (value == null)
                return "";

            string text;
            try
            {
                text = value is string s
                    ? s
                    : Newtonsoft.Json.JsonConvert.SerializeObject(value, Newtonsoft.Json.Formatting.None);
            }
            catch
            {
                text = value.ToString() ?? "";
            }

            text = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return text.Length > maxLength ? text[..maxLength] + "..." : text;
        }

        // ================================================================
        // 快速指令
        // ================================================================
        private async Task<bool> HandleQuickCommand(string text)
        {
            if (text.StartsWith("/key "))
            {
                string key = text[5..].Trim();
                if (string.IsNullOrWhiteSpace(key)) { AddSystem("/key sk-你的密钥"); UserInput = ""; return true; }
                _llm.SetApiKey(key); SaveApiKey(key); UserInput = "";
                AddSystem("正在测试连接..."); IsLoading = true;
                try
                {
                    var r = await _llm.ChatAsync(new List<Message>(), "回复“连接成功”");
                    IsLoading = false;
                    if (r.Content.Contains("错误")) { AddSystem("错误：" + r.Content); return true; }
                    AddSystem("连接成功。");
                    _ = InitializeProjectContextAsync();
                }
                catch (Exception ex) { IsLoading = false; AddSystem("错误：" + ex.Message); }
                return true;
            }
            if (text == "/project") { UserInput = ""; IsLoading = true; await InitializeProjectContextAsync(); IsLoading = false; return true; }
            if (text == "/map") { UserInput = ""; IsLoading = true; var i = await _mapReader.GetMapInfoAsync(); IsLoading = false; AddMessage("assistant", $"地图信息：{i}"); return true; }
            if (text == "/layers") { UserInput = ""; IsLoading = true; var i = await _mapReader.ListLayersAsync(); IsLoading = false; AddMessage("assistant", $"图层列表：{i}"); return true; }
            if (text.StartsWith("/layer ")) { string n = text[7..].Trim(); UserInput = ""; IsLoading = true; var i = await _mapReader.DescribeLayerAsync(n); IsLoading = false; AddMessage("assistant", $"图层详情：{i}"); return true; }
            if (text == "/sel") { UserInput = ""; IsLoading = true; var i = await _mapReader.GetSelectionAsync(); IsLoading = false; AddMessage("assistant", $"选择集：{i}"); return true; }
            if (text == "/tree") { UserInput = ""; IsLoading = true; var i = await _projectReader.GetContextForAIAsync(); IsLoading = false; AddMessage("assistant", $"项目上下文：{i}"); return true; }
            if (text.StartsWith("/search ")) { string kw = text[8..].Trim(); UserInput = ""; IsLoading = true; var results = _toolIndex != null ? _toolIndex.FindRelevantWithFallback(kw, topN: 20) : new List<string>(); IsLoading = false; AddMessage("assistant", $"找到 {results.Count} 个相关工具：{string.Join(", ", results.Take(10))}{(results.Count > 10 ? "..." : "")}"); return true; }
            if (text == "/memory") { UserInput = ""; AddMessage("assistant", _memoryStore?.GetSummary() ?? "项目记忆尚未初始化。打开项目后可用。"); return true; }
            if (text == "/status") { UserInput = ""; AddSystem(_llm.IsConfigured ? $"已配置 {_selectedModel} | 思考：{_selectedThinking}" : "尚未配置 API Key"); return true; }
            if (text == "/help") { UserInput = ""; AddSystem("/project /map /layers /layer <名称> /sel /tree /search <关键词> /key /status /memory /trace /w5h2 /help"); return true; }
            if (text == "/w5h2") { UserInput = ""; _ = ExtractW5H2Async(); return true; }
            if (text == "/trace") { UserInput = "";
                if (_transcript.IsEnabled)
                {
                    var health = _healthMonitor.Snapshot();
                    string summary = $"日志：{_transcript.FilePath}\n事件数：{_transcript.EventCount}\n对话轮次：{_runtime.TurnCount}";
                    if (_ledger != null)
                        summary += $"\n验证：{_ledger.TotalChecks} 次（通过 {_ledger.PassedChecks} / 失败 {_ledger.FailedChecks}）";
                    summary += $"\n快照：{_snapshotter.SnapshotCount} 次 | {_snapshotter.FilePath}";
                    summary += $"\n日志健康：{health.FaultSummary()}";
                    if (!health.AllHealthy)
                        summary += "\n部分日志层出现写入故障，详见 transcript/session_end。";
                    AddMessage("assistant", summary);
                }
                else AddSystem("日志未启用。");
                return true;
            }
            return false;
        }

        private Task SwitchModelAsync(string model)
        {
            string modelId = model switch
            {
                "DeepSeek V4 Pro" => "deepseek-v4-pro",
                "DeepSeek V4 Flash" => "deepseek-v4-flash",
                _ => "deepseek-v4-pro"
            };
            _llm.SetModel(modelId);
            LogModelSwitch("model_changed");
            UpdateStatusBar();
            return Task.CompletedTask;
        }

        /// <summary>
        /// 把模型或思考档位切换写入 transcript，记录 Provider 的实际参数
        ///（llm.ModelName/ThinkingEffort），并与每轮 llm_request 的 model 字段互证。
        /// 通过 ITranscriptTurnWriter 契约写入。
        /// </summary>
        private void LogModelSwitch(string reason)
        {
            try
            {
                _transcript.WriteModelSwitch(reason, _llm.ModelName, _llm.ThinkingEffort);
            }
            catch { /* 日志失败不影响模型切换。 */ }
        }

        private static AgentPermissionLevel PermissionLevelFromLabel(string label) => label switch
        {
            "只读" => AgentPermissionLevel.ReadOnly,
            "完全访问" => AgentPermissionLevel.FullAccess,
            _ => AgentPermissionLevel.RequestApproval
        };

        private static string PermissionLabelFromLevel(AgentPermissionLevel level) => level switch
        {
            AgentPermissionLevel.ReadOnly => "只读",
            AgentPermissionLevel.FullAccess => "完全访问",
            _ => "请求批准"
        };

        // ================================================================
        // 鎸佷箙鍖?
        // ================================================================
        private void LoadApiKey()
        {
            try
            {
                if (File.Exists(SETTINGS_FILE))
                {
                    var d = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(File.ReadAllText(SETTINGS_FILE));
                    var k = (string?)d?.apiKey;
                    var encrypted = (string?)d?.encrypted;
                    var encKey = (string?)d?.encKey;
                    if (!string.IsNullOrWhiteSpace(k))
                    {
                        // Plaintext (legacy or first-run): use as-is, re-save encrypted
                        _llm.SetApiKey(k);
                        SaveApiKey(k);
                    }
                    else if (!string.IsNullOrWhiteSpace(encKey) && !string.IsNullOrWhiteSpace(encrypted))
                    {
                        // Decrypt with DPAPI (CurrentUser scope)
                        byte[] keyBytes = Convert.FromBase64String(encKey);
                        byte[] dataBytes = Convert.FromBase64String(encrypted);
                        byte[] decrypted = System.Security.Cryptography.ProtectedData.Unprotect(
                            dataBytes, keyBytes, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                        _llm.SetApiKey(System.Text.Encoding.UTF8.GetString(decrypted));
                    }
                }
            }
            catch (Exception ex)
            {
                AgentDiagnostics.Warn("ApiKey.Load", "API Key 加载/解密失败——将提示用户重新输入", ex);
            }
        }

        private void SaveApiKey(string k)
        {
            try
            {
                // Encrypt with DPAPI (Windows Data Protection API, CurrentUser scope).
                // The key is only decryptable by the same Windows user on the same machine.
                byte[] entropy = new byte[16];
                using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
                    rng.GetBytes(entropy);
                byte[] data = System.Text.Encoding.UTF8.GetBytes(k);
                byte[] encrypted = System.Security.Cryptography.ProtectedData.Protect(
                    data, entropy, System.Security.Cryptography.DataProtectionScope.CurrentUser);

                Directory.CreateDirectory(Path.GetDirectoryName(SETTINGS_FILE)!);
                File.WriteAllText(SETTINGS_FILE,
                    Newtonsoft.Json.JsonConvert.SerializeObject(
                        new
                        {
                            encrypted = Convert.ToBase64String(encrypted),
                            encKey = Convert.ToBase64String(entropy),
                            at = DateTime.Now.ToString("yyyy-MM-dd HH:mm")
                        },
                        Newtonsoft.Json.Formatting.Indented));
            }
            catch (Exception ex)
            {
                AgentDiagnostics.Warn("ApiKey.Save", "API Key 加密/保存失败", ex);
            }
        }

        private void AddSystem(string s) => AddMessage("system", s);

        public async Task SaveSessionAsync()
        {
            var summary = BuildSessionSummary();
            PersistSessionArtifacts(summary, "ExplicitSessionSave");
            await Task.CompletedTask;
        }

        /// <summary>
        /// 同步 flush 待落盘日志缓冲。关闭窗口时调用，防止缓冲日志丢失。
        /// 正常会话期间的落盘由各日志层的实时写入策略保证，不依赖此方法。
        /// </summary>
        public void FlushPendingWrites()
        {
            try { _transcript.Flush(); } catch { }
            try { _ledger?.Flush(); } catch { }
            try { _snapshotter.Flush(); } catch { }
        }

        /// <summary>
        /// Reversible close observation. It creates a synchronous recovery
        /// barrier without closing log streams or terminalizing the session,
        /// because ArcGIS Pro may still cancel its close sequence.
        /// </summary>
        public void PrepareForHostShutdown(string reason = "ArcGISProClosing")
        {
            if (Volatile.Read(ref _hostShutdownFinalized) != 0)
                return;

            bool hadActiveRun = _currentRunCts != null || IsLoading;
            try
            {
                _runtime.RecordHostShutdownObserved(reason, hadActiveRun);
            }
            catch { }
            try
            {
                _runtime.SaveHostShutdownCheckpointCritical(
                    terminal: false,
                    reason);
            }
            catch { }
            FlushPendingWrites();
        }

        /// <summary>
        /// Synchronous, bounded and idempotent host-close barrier. ArcGIS Pro
        /// calls this even when the chat window was never closed explicitly.
        /// </summary>
        public void FinalizeForHostShutdown(string reason = "ArcGISProClosing")
        {
            PrepareForHostShutdown(reason);
            if (Interlocked.Exchange(ref _hostShutdownFinalized, 1) != 0)
                return;

            bool hadActiveRun = _currentRunCts != null || IsLoading;
            try { _hostLifetimeCts.Cancel(); } catch { }
            try { _currentRunCts?.Cancel(); } catch { }
            try { _runtime.RecordHostShutdown(reason, hadActiveRun); } catch { }

            try
            {
                var summary = BuildSessionSummary();
                if (hadActiveRun)
                    summary.PendingIssues.Insert(0, "ArcGIS Pro 关闭时任务仍在执行；会话已标记为 Interrupted，可从检查点复盘。");
                PersistSessionArtifacts(summary, reason);
            }
            catch
            {
                FlushPendingWrites();
            }
            finally
            {
                try { _runtime.CloseLogging(); } catch { }
            }
        }

        private void FinalizeCurrentSessionForSwitch(string reason)
        {
            var summary = BuildSessionSummary();
            var messageSnapshot = CaptureProjectCognitionMessages();

            PersistSessionArtifacts(summary, reason);
            QueueDeferredProjectCognition(messageSnapshot);
        }

        private void PersistSessionArtifacts(
            SessionSummary summary,
            string terminationReason)
        {
            summary.TerminalStatus = _session.Status.ToString();
            summary.TerminationReason = terminationReason;
            summary.SessionLeaseId = GetActiveSessionLease()?.LeaseId ?? "";
            bool finalized = _sessionFinalizer.TryFinalize(
                summary.ThreadId,
                persistTerminalCheckpoint: () =>
                    _runtime.SaveHostShutdownCheckpointCritical(
                        terminal: true,
                        reason: terminationReason),
                persistTerminalSummary: () =>
                    _memoryStore?.RecordSessionSummary(summary),
                writeSessionEnd: () =>
                {
                    _runtime.WriteSessionEnd();
                    _transcript.Flush();
                    _ledger?.Flush();
                    _snapshotter.Flush();
                },
                // The lease is the commit marker and is released last.
                releaseLease: () => ReleaseSessionLease(terminationReason));
            if (!finalized)
                FlushPendingWrites();
        }

        private List<string> CaptureProjectCognitionMessages()
        {
            return _session.Messages
                .Where(m => m.Role == "user" || m.Role == "assistant")
                .Select(m => $"{m.Role}: {m.Content}")
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .TakeLast(8)
                .ToList();
        }

        private void QueueDeferredProjectCognition(IReadOnlyList<string> summaryInput)
        {
            if (_memoryStore == null
                || summaryInput.Count == 0
                || !_llm.IsConfigured
                || !string.IsNullOrWhiteSpace(_memoryStore.GetW5H2Context()))
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    string? extracted = await ExtractW5H2FromMessagesAsync(summaryInput);
                    if (!string.IsNullOrWhiteSpace(extracted))
                        _memoryStore.SetW5H2Context(extracted);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ViewModel] Deferred W5H2 extraction failed: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Ask LLM to extract 5W2H project facts from the conversation and persist.
        /// Called explicitly via /w5h2 or automatically at session end for new projects.
        /// </summary>
        private async Task ExtractW5H2Async()
        {
            if (!_llm.IsConfigured || _memoryStore == null)
                return;

            var summaryInput = CaptureProjectCognitionMessages();
            string? result = await ExtractW5H2FromMessagesAsync(summaryInput);
            if (!string.IsNullOrWhiteSpace(result))
            {
                _memoryStore.SetW5H2Context(result);
                Debug.WriteLine($"[ViewModel] W5H2 extracted: {Truncate(result, 120)}");
            }
        }

        private async Task<string?> ExtractW5H2FromMessagesAsync(IReadOnlyList<string> summaryInput)
        {
            if (!_llm.IsConfigured || summaryInput.Count == 0)
                return null;

            string prompt = "从以下对话中提取项目 5W2H 信息，用简洁中文记录。\n" +
                "Why（为什么做这个项目）、What（做什么）、Where（省市位置）、When（基准年/目标年）、" +
                "Who（行政层级/组织）、How much（人口/指标）。\n" +
                "不确定的内容写 未知。纯文本，2 段以内。\n\n" +
                string.Join("\n", summaryInput);

            try
            {
                var r = await _llm.ChatAsync(
                    new List<Message> { new() { Role = "user", Content = prompt } },
                    "你是 GIS 项目分析师。提取项目元信息。", temperature: 0.3, maxTokens: 512);

                return r.Content?.Trim();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ViewModel] W5H2 extraction failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Mid-session memory snapshot; saves lightweight session progress.
        /// Called after each agent response so work is not lost if Pro crashes.
        /// </summary>
        private void SaveMemorySnapshot()
        {
            if (_memoryStore == null) return;

            try
            {
                var snapshot = new SessionSummary
                {
                    ThreadId = _session.ThreadId,
                    Revision = _runtime.TurnCount,
                    IsTerminal = false,
                    TerminalStatus = "",
                    TerminationReason = "",
                    SessionLeaseId = GetActiveSessionLease()?.LeaseId ?? "",
                    Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                    User = "ArcGISPro",
                    WhatWasDone = $"进行中：{_runtime.TurnCount} 轮交互，" +
                        $"{_session.Observations.Count} 条工具观察",
                    KeyDecisions = _session.Observations
                        .Select(o => o.ToolName)
                        .Distinct()
                        .Take(8)
                        .ToList(),
                    PendingIssues = _session.Validations
                        .Where(v => v.Verdict == ValidationVerdict.FAIL)
                        .Select(v => v.Detail)
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Take(3)
                        .ToList()
                };
                _memoryStore.RecordSessionSummary(snapshot);
            }
            catch { /* non-blocking */ }
        }

        private SessionSummary BuildSessionSummary()
        {
            // 鈽?Structured tool chain: capture per-observation decisions, not just the last message
            var toolChain = _session.Observations
                .Select(o => o.ToolName)
                .Distinct()
                .ToList();

            var toolDetails = _session.Observations
                .GroupBy(o => o.ToolName)
                .Select(g => $"{g.Key}×{g.Count()}")
                .ToList();

            var lastAssistant = _session.Messages.LastOrDefault(m => m.Role == "assistant")?.Content ?? "";
            var lastUser = _session.Messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";

            var failedValidations = _session.Validations
                .Where(v => v.Verdict == ValidationVerdict.FAIL)
                .Select(v => v.Detail)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .TakeLast(3)
                .ToList();
            var warnings = _session.Validations
                .Where(v => v.Verdict == ValidationVerdict.WARN)
                .Select(v => v.Detail)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .TakeLast(3)
                .ToList();

            var decisions = new List<string>();
            if (toolChain.Count > 0)
                decisions.Add($"工具链：{string.Join(" -> ", toolChain)}");
            if (toolDetails.Count > 0)
                decisions.Add($"统计：{string.Join("，", toolDetails)}");
            if (_session.Validations.Any(v => v.Verdict == ValidationVerdict.FAIL))
                decisions.Add($"验证失败 {_session.Validations.Count(v => v.Verdict == ValidationVerdict.FAIL)} 项");
            if (!string.IsNullOrWhiteSpace(lastUser))
                decisions.Add($"用户：{Truncate(lastUser, 120)}");

            var pendingIssues = new List<string>();
            pendingIssues.AddRange(failedValidations);
            pendingIssues.AddRange(warnings);

            return new SessionSummary
            {
                ThreadId = _session.ThreadId,
                Revision = _runtime.TurnCount,
                IsTerminal = true,
                TerminalStatus = _session.Status.ToString(),
                SessionLeaseId = GetActiveSessionLease()?.LeaseId ?? "",
                Date = DateTime.Now.ToString("yyyy-MM-dd"),
                User = "ArcGISPro",
                WhatWasDone = Truncate(string.IsNullOrWhiteSpace(lastAssistant)
                    ? $"完成 {_runtime.TurnCount} 轮交互"
                    : lastAssistant, 220),
                KeyDecisions = decisions,
                PendingIssues = pendingIssues.Distinct().Take(5).ToList()
            };
        }

        private async Task InitializeSessionLeaseAsync(
            FileRunCheckpointStore checkpointStore,
            ProjectMemoryStore memoryStore)
        {
            await _sessionLeaseLifecycleGate.WaitAsync();
            try
            {
                bool projectChanged = !_sessionLeaseProjectFolder.Equals(
                    _projFolder,
                    StringComparison.OrdinalIgnoreCase);
                if (projectChanged)
                {
                    ReleaseSessionLease("ProjectContextChanged");
                    _sessionLeaseStore = new FileSessionLeaseStore(_projFolder);
                    _sessionLeaseProjectFolder = _projFolder;
                }

                if (_sessionLeaseStore == null)
                    return;

                DateTime now = DateTime.UtcNow;
                IReadOnlyList<SessionLease> stale =
                    await _sessionLeaseStore.ClaimStaleAsync(
                        _sessionLeaseOwnerId,
                        now);
                if (stale.Count > 0)
                {
                    var recovery = new StaleSessionRecoveryCoordinator(
                        checkpointStore,
                        memoryStore,
                        _transcript,
                        _sessionLeaseStore);
                    StaleSessionRecoveryResult recovered =
                        await recovery.RecoverAsync(stale, now);
                    Debug.WriteLine(
                        $"[ViewModel] Recovered {recovered.RecoveredSessionIds.Count} stale session lease(s).");
                }

                await ActivateSessionLeaseCoreAsync(_session.ThreadId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[ViewModel] Session lease initialization failed: {ex.GetType().Name}: {ex.Message}");
                _runtime.SetSessionLease(null);
            }
            finally
            {
                _sessionLeaseLifecycleGate.Release();
            }
        }

        private async Task ActivateSessionLeaseAsync(string sessionId)
        {
            await _sessionLeaseLifecycleGate.WaitAsync();
            try
            {
                await ActivateSessionLeaseCoreAsync(sessionId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[ViewModel] Session lease acquire failed: {ex.GetType().Name}: {ex.Message}");
                _runtime.SetSessionLease(null);
            }
            finally
            {
                _sessionLeaseLifecycleGate.Release();
            }
        }

        private async Task ActivateSessionLeaseCoreAsync(string sessionId)
        {
            if (_sessionLeaseStore == null
                || string.IsNullOrWhiteSpace(sessionId))
                return;

            SessionLease? active = GetActiveSessionLease();
            if (active != null
                && active.SessionId.Equals(
                    sessionId,
                    StringComparison.OrdinalIgnoreCase))
                return;
            if (active != null)
                ReleaseSessionLease("SessionChanged");

            using Process process = Process.GetCurrentProcess();
            DateTime now = DateTime.UtcNow;
            var requested = new SessionLease
            {
                LeaseId = Guid.NewGuid().ToString("N"),
                SessionId = sessionId,
                OwnerId = _sessionLeaseOwnerId,
                ProcessId = process.Id,
                ProcessStartedAtUtc = process.StartTime.ToUniversalTime(),
                AcquiredAtUtc = now,
                HeartbeatAtUtc = now,
                ExpiresAtUtc = now + SessionLeaseTtl
            };
            SessionLease acquired = await _sessionLeaseStore.AcquireAsync(requested);
            lock (_sessionLeaseStateLock)
            {
                _activeSessionLease = acquired;
                _sessionLeaseHeartbeatTimer?.Dispose();
                _sessionLeaseHeartbeatTimer = new Timer(
                    _ => _ = HeartbeatSessionLeaseAsync(),
                    null,
                    SessionLeaseHeartbeatInterval,
                    SessionLeaseHeartbeatInterval);
            }
            _sessionFinalizer.Reopen(sessionId);
            _runtime.SetSessionLease(acquired);
        }

        private async Task HeartbeatSessionLeaseAsync()
        {
            if (Interlocked.Exchange(
                    ref _sessionLeaseHeartbeatInFlight,
                    1) != 0)
                return;
            try
            {
                SessionLease? lease = GetActiveSessionLease();
                ISessionLeaseStore? store = _sessionLeaseStore;
                if (lease == null || store == null)
                    return;
                SessionLease? updated = await store.HeartbeatAsync(
                    lease.LeaseId,
                    lease.OwnerId,
                    DateTime.UtcNow,
                    SessionLeaseTtl);
                if (updated == null)
                    return;
                bool stillActive;
                lock (_sessionLeaseStateLock)
                {
                    stillActive = _activeSessionLease?.LeaseId == updated.LeaseId;
                    if (stillActive)
                        _activeSessionLease = updated;
                }
                if (stillActive)
                    _runtime.SetSessionLease(updated);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[ViewModel] Session lease heartbeat failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                Volatile.Write(ref _sessionLeaseHeartbeatInFlight, 0);
            }
        }

        private SessionLease? GetActiveSessionLease()
        {
            lock (_sessionLeaseStateLock)
                return _activeSessionLease;
        }

        private void ReleaseSessionLease(string reason)
        {
            SessionLease? lease;
            Timer? timer;
            lock (_sessionLeaseStateLock)
            {
                lease = _activeSessionLease;
                _activeSessionLease = null;
                timer = _sessionLeaseHeartbeatTimer;
                _sessionLeaseHeartbeatTimer = null;
            }
            try { timer?.Dispose(); } catch { }
            _runtime.SetSessionLease(null);
            if (lease == null || _sessionLeaseStore == null)
                return;
            try
            {
                _sessionLeaseStore.ReleaseAsync(
                    lease.LeaseId,
                    lease.OwnerId,
                    reason,
                    DateTime.UtcNow).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[ViewModel] Session lease release failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";

            string text = value.Trim().Replace("\r", " ").Replace("\n", " ");
            return text.Length <= maxLength ? text : text[..maxLength] + "...";
        }

        // ================================================================
        // INotifyPropertyChanged
        // ================================================================
        public event PropertyChangedEventHandler? PropertyChanged;
        public event Action? OnNewMessageAdded;

        private bool Set<T>(ref T f, T v, [CallerMemberName] string? n = null)
        {
            if (EqualityComparer<T>.Default.Equals(f, v)) return false;
            f = v;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
            return true;
        }
    }

    internal class RelayCommand : ICommand
    {
        private readonly Func<object?, Task> _execute;
        private readonly Func<object?, bool> _canExecute;

        public RelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
            : this(_ => execute(), canExecute == null ? null : new Func<object?, bool>(_ => canExecute()))
        {
        }

        public RelayCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute ?? (_ => true);
        }

        public bool CanExecute(object? parameter) => _canExecute(parameter);
        public async void Execute(object? parameter) => await _execute(parameter);
        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }
    }
}
