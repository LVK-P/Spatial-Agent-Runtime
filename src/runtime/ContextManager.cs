using System;
using SpatialAgent.Runtime.Contracts;
using SpatialAgent.Core;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SpatialAgent.Runtime
{
    /// <summary>
    /// Collects project, map, knowledge, skills and memory context for a turn.
    /// </summary>
    /// <remarks>中文：收集项目、地图、知识、技能和记忆上下文，持续一回合。</remarks>
    public class ContextManager : IHostStateGuard
    {
        private readonly IProjectReader _projectReader;
        private readonly IMapReader _mapReader;
        private readonly ISkillProvider _skillManager;
        private readonly ISearchProvider? _knowledge;
        private ToolIndex? _toolIndex;
        private IToolSemanticScorer? _toolSemanticScorer;

        private IProjectMemoryService? _projectMemory;
        private IEventBus? _eventBus;
        private IDisposable? _eventSubscription;
        private int _msgCountSinceLastFullContext;
        private string _lastProjectContext = "";
        private ProjectContext _lastProjectSnapshot = new();
        private string _lastMapContext = "";
        private MapContextSnapshot _lastMapSnapshot = new();
        private string _lastContextHash = "";
        private string _lastLiveFingerprint = "";

        /// <summary>Full context refresh interval in message count.</summary>
        /// <remarks>中文：消息数量的完整刷新间隔。</remarks>
        public int FullContextRefreshInterval { get; set; } = 3;

        /// <summary>Keywords that force a full refresh.</summary>
        /// <remarks>中文：关键词会强制全面刷新。</remarks>
        public static readonly string[] GisKeywords = {
            "图层", "字段", "数据", "要素", "项目", "地图",
            "坐标系", "属性表", "选择", "符号"
        };

        public ContextManager(
            IProjectReader projectReader,
            IMapReader mapReader,
            ISkillProvider skillManager,
            ISearchProvider? knowledge = null)
        {
            _projectReader = projectReader;
            _mapReader = mapReader;
            _skillManager = skillManager;
            _knowledge = knowledge;
        }

        public void SetProjectMemory(IProjectMemoryService memory) => _projectMemory = memory;

        /// <summary>
        /// Subscribe to tool execution events for automatic cache invalidation.
        /// After any tool modifies the project/map state, the next context gather
        /// will force a full refresh instead of relying on stale cached data.
        /// </summary>
        /// <remarks>中文：订阅工具执行事件以实现自动缓存失效。任何工具修改项目/地图状态后，下一次上下文采集会强制完全刷新，而不是依赖陈旧的缓存数据。</remarks>
        public void SetEventBus(IEventBus? bus)
        {
            _eventSubscription?.Dispose();
            _eventBus = bus;
            if (_eventBus != null)
            {
                _eventSubscription = _eventBus.Subscribe("tool.completed", e =>
                {
                    string risk = e.Metadata?.GetValueOrDefault("risk", "Modify") ?? "Modify";
                    string tool = e.Metadata?.GetValueOrDefault("tool", "") ?? "";

                    // Destructive（Delete/Truncate）→ 全量刷新：图层可能消失或 Schema 变更
                    // English：Destructive(Delete/Truncate) → Full refresh: layers may disappear or schema changes
                    if (risk == "Destructive")
                    {
                        InvalidateCache();
                        return;
                    }

                    // Modify → 仅当工具可能创建了新图层/新输出时才刷新。
                    // CalculateField / AddField 只改字段内容，上下文无需重载——
                    // 字段变更已在工具输出中可见，全量 re-gather 浪费 I/O 和 token。
                    if (risk == "Modify")
                        InvalidateCache();
                });
            }
        }

        public void SetToolIndex(ToolIndex index) => _toolIndex = index;

        public void SetToolSemanticScorer(IToolSemanticScorer scorer) => _toolSemanticScorer = scorer;

        /// <summary>
        /// Collect a full turn context.
        /// </summary>
        /// <remarks>中文：收集完整的回合背景。</remarks>
        public async Task<AgentContext> GatherAsync(string userMessage)
        {
            _msgCountSinceLastFullContext = 0;
            return await GatherInternalAsync(userMessage, fullRefresh: true);
        }

        /// <summary>
        /// Collect a light turn context.
        /// </summary>
        /// <remarks>中文：收集一轮轻量上下文。</remarks>
        public async Task<AgentContext> GatherLightAsync(string userMessage)
        {
            // ★ 2026-07-23：每轮强制全量刷新图层信息。
            // 工具执行（Project/Buffer/Clip 等）可能创建新图层或修改已有图层，
            // 指纹/VAV 省掉的 200ms 刷新成本远小于 LLM 看不到新图层导致的计算遗漏。
            // 不做 interval/关键词/fingerprint 判断——每轮都刷新。
            // English：2026-07-23: Each round of mandatory full layer information refresh. Tool execution (Project/Buffer/Clip, etc.) may create new layers or modify existing ones; the 200ms refresh cost saved by fingerprint/VAV is much lower than the computational omission caused by the LLM not seeing the new layer. No interval/keyword/fingerprint checks—refresh every round.
            return await GatherInternalAsync(userMessage, fullRefresh: true);
        }

        /// <summary>
        /// Captures hashes of the live project, map, layer/schema/count and
        /// selection state. The snapshot deliberately excludes raw user data
        /// and is used only to invalidate an approval whose target state changed
        /// while execution was paused.
        /// </summary>
        /// <remarks>中文：捕捉当前项目、地图、图层/模式/计数和选择状态的哈希值。快照故意排除原始用户数据，仅用于使目标状态在暂停执行期间发生变化的批准无效。</remarks>
        public async Task<DataRevisionSnapshot> CaptureDataRevisionAsync(
            CancellationToken cancellation = default)
        {
            try
            {
                cancellation.ThrowIfCancellationRequested();
                var projectTask = _projectReader.GetContextForAIAsync();
                var mapTask = _mapReader.GetMapInfoAsync();
                var layerTask = _mapReader.GetAllLayerSummariesAsync();
                var selectionTask = _mapReader.GetSelectionAsync();
                var contentTask = _mapReader.GetContentRevisionAsync();
                await Task.WhenAll(
                    projectTask,
                    mapTask,
                    layerTask,
                    selectionTask,
                    contentTask);
                cancellation.ThrowIfCancellationRequested();

                string project = NormalizeRevisionComponent(projectTask.Result);
                string map = NormalizeRevisionComponent(mapTask.Result);
                string layers = NormalizeRevisionComponent(layerTask.Result);
                string selection = NormalizeRevisionComponent(selectionTask.Result);
                string content = NormalizeRevisionComponent(contentTask.Result);
                bool contentRevisionRequired =
                    !string.IsNullOrWhiteSpace(content);
                bool contentRevisionReliable =
                    contentRevisionRequired
                    && !content.StartsWith(
                        "unreliable:",
                        StringComparison.OrdinalIgnoreCase);
                bool reliable = !LooksLikeProbeError(project)
                    && !LooksLikeProbeError(map)
                    && !LooksLikeProbeError(layers)
                    && !LooksLikeProbeError(selection);
                string projectHash = HashRevisionComponent(project);
                string mapHash = HashRevisionComponent(map);
                string layerHash = HashRevisionComponent(layers);
                string selectionHash = HashRevisionComponent(selection);
                string contentHash = string.IsNullOrWhiteSpace(content)
                    ? ""
                    : HashRevisionComponent(content);
                string fingerprint = HashRevisionComponent(
                    $"{projectHash}|{mapHash}|{layerHash}|{selectionHash}|{contentHash}");

                return new DataRevisionSnapshot
                {
                    Fingerprint = fingerprint,
                    ProjectFingerprint = projectHash,
                    MapFingerprint = mapHash,
                    LayerFingerprint = layerHash,
                    SelectionFingerprint = selectionHash,
                    ContentFingerprint = contentHash,
                    ContentRevisionRequired = contentRevisionRequired,
                    ContentRevisionReliable = contentRevisionReliable,
                    IsReliable = reliable,
                    ReliabilityReason = !reliable
                        ? "one or more ArcGIS state probes returned an error"
                        : contentRevisionRequired
                            && !contentRevisionReliable
                            ? content
                            : contentRevisionReliable
                                ? "project+map+layer-schema-count+selection+content"
                                : "project+map+layer-schema-count+selection",
                    CapturedAtUtc = DateTime.UtcNow
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AgentDiagnostics.Warn(
                    "ContextManager.DataRevision",
                    "Live ArcGIS data revision capture failed",
                    ex,
                    _eventBus);
                return new DataRevisionSnapshot
                {
                    Fingerprint = "",
                    IsReliable = false,
                    ReliabilityReason = $"{ex.GetType().Name}: {ex.Message}",
                    CapturedAtUtc = DateTime.UtcNow
                };
            }
        }

        public Task<DataRevisionSnapshot> CaptureAsync(
            CancellationToken cancellation = default) =>
            CaptureDataRevisionAsync(cancellation);

        public async Task<HostStateGuardResult> ValidateAsync(
            DataRevisionSnapshot expected,
            CancellationToken cancellation = default)
        {
            // Approval/wait paths remain strict: no mutation budget is assumed.
            // 中文：批准/等待路径仍保持严格，不假定存在变更预算。
            return await ValidateForExecutionAsync(
                expected,
                expectedMutations: null,
                planHasCommittedMutation: false,
                cancellation);
        }

        public async Task<HostStateGuardResult> ValidateForExecutionAsync(
            DataRevisionSnapshot expected,
            ExpectedMutationSet? expectedMutations,
            bool planHasCommittedMutation,
            CancellationToken cancellation = default)
        {
            DataRevisionSnapshot current =
                await CaptureDataRevisionAsync(cancellation);
            return HostRevisionPolicy.Evaluate(
                expected,
                current,
                expectedMutations,
                planHasCommittedMutation);
        }

        private static string NormalizeRevisionComponent(string? value) =>
            (value ?? "")
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Trim();

        private static string HashRevisionComponent(string value)
        {
            byte[] hash = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(value));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static bool LooksLikeProbeError(string value) =>
            value.Contains("\"error\"", StringComparison.OrdinalIgnoreCase)
            || value.Contains("读取失败", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// ★ VAV（ADR 0049）：基于宿主实时 API 的上下文指纹——不再用旧缓存自比较。
        /// 调用 IMapReader + IProjectReader 的轻量方法（非全量图层枚举），
        /// 成本约 200ms，用于判断宿主状态是否发生了实质性变化。
        /// 探针失败 → 返回 Guid 随机值（强制 full refresh——保守策略）。
        /// </summary>
        /// <remarks>English：VAV (ADR 0049): Contextual fingerprinting based on the host real-time API—no longer relies on old caches for self-comparison. A lightweight method called IMapReader + IProjectReader (non-full layer enumeration) costs about 200ms and is used to determine whether the host state has undergone a substantial change. Probe failure → returns a GUID random value (forced full refresh—conservative policy).</remarks>
        private async Task<string> ComputeLiveFingerprintAsync()
        {
            try
            {
                // 轻量探针：地图名+坐标系 + 项目路径 + 图层名列表。
                // ListLayersAsync() 仅返回图层名和类型（不含字段详情），
                // 成本 ≪ GetAllLayerSummariesAsync()，但能检测 Project/Buffer 等
                // 工具创建新图层后宿主状态变化——此前仅靠地图名+项目路径做指纹，
                // 新图层创建后指纹不变，导致后续 turn 跳过全量刷新、LLM 看不到新图层。
                // English：Lightweight Probe: Map Name + Coordinate System + Project Path + List of Layer Names. ListLayersAsync() only returns layer name and type (excluding field details), costs ≪ GetAllLayerSummariesAsync(), but can detect host state changes after Project/Buffer tools create new layers—previously, fingerprinting was done only by map name + project path, but fingerprinting remains unchanged after new layer creation, causing subsequent turns to skip full refresh and LLMs not to see new layers.
                var mapTask = _mapReader.GetMapInfoAsync();
                var projTask = _projectReader.GetContextForAIAsync();
                var layersTask = _mapReader.ListLayersAsync();
                await Task.WhenAll(mapTask, projTask, layersTask);

                string live = (mapTask.Result ?? "") + "||"
                    + (projTask.Result ?? "") + "||"
                    + (layersTask.Result ?? "");
                byte[] hash = System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(live));
                return Convert.ToHexString(hash)[..16];
            }
            catch
            {
                // 探针失败 → 保守：返回随机值，强制后续 full refresh
                // English：Probe failure → Conservative: Returns a random value and forces a full refresh for subsequent events
                AgentDiagnostics.Warn("ContextManager.ComputeLiveFingerprint",
                    "Live host probe failed — forcing conservative full context refresh.");
                return Guid.NewGuid().ToString("N")[..16];
            }
        }

        /// <summary>已废弃（P1-A 删除使用点，VAV 用 ComputeLiveFingerprintAsync 替代）。保留以兼容旧测试。</summary>
        /// <remarks>English：Deprecated (P1-A deletes usage points, VAV replaced with ComputeLiveFingerprintAsync). Reserved to be compatible with older tests.</remarks>
        private string ComputeContextFingerprint()
        {
            string input = (_lastProjectContext ?? "") + "||" + (_lastMapContext ?? "");
            byte[] hash = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(hash)[..16];
        }

        private async Task<AgentContext> GatherInternalAsync(string userMessage, bool fullRefresh)
        {
            var ctx = new AgentContext();

            if (fullRefresh || string.IsNullOrEmpty(_lastProjectContext))
            {
                try { _lastProjectContext = await _projectReader.GetContextForAIAsync(); }
                catch (Exception ex)
                {
                    _lastProjectContext = "";
                    AgentDiagnostics.Warn("ContextManager.Project", "Project context refresh failed", ex);
                }
                try
                {
                    _lastProjectSnapshot =
                        await _projectReader.GetProjectContextAsync()
                        ?? new ProjectContext();
                }
                catch (Exception ex)
                {
                    _lastProjectSnapshot = new ProjectContext();
                    AgentDiagnostics.Warn(
                        "ContextManager.Project",
                        "Structured project context refresh failed",
                        ex);
                }
            }
            ctx.ProjectInfo = _lastProjectContext;
            if (_lastProjectSnapshot.IsAvailable)
            {
                ctx.ProjectName = _lastProjectSnapshot.ProjectName;
                ctx.ProjectPath = _lastProjectSnapshot.HomeFolder
                    ?? _lastProjectSnapshot.TempFolder
                    ?? "";
                ctx.GdbPath = _lastProjectSnapshot.DefaultGdbPath ?? "";
            }

            // Extract structured fields from the ProjectInfo text blob
            // (format: "项目：{name}\n家目录：{path}\n默认数据库名：{name}\n默认数据库路径：{path}")
            // 中文：从ProjectInfo文本块中提取结构化字段（格式：“项目：{name}\n家目录：{path}\n默认数据库名：{name}\n默认数据库路径：{path}”）
            foreach (var line in _lastProjectContext.Split('\n'))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(ctx.ProjectName)
                    && (trimmed.StartsWith("项目：") || trimmed.StartsWith("Project:")))
                    ctx.ProjectName = ExtractTaggedValue(trimmed, "项目：", "Project:");
                else if (string.IsNullOrWhiteSpace(ctx.ProjectPath)
                    && (trimmed.Contains("家目录：") || trimmed.Contains("HomeFolder:")))
                    ctx.ProjectPath = ExtractTaggedValue(trimmed, "家目录：", "HomeFolder:");
                else if (string.IsNullOrWhiteSpace(ctx.GdbPath)
                    && (line.Contains("默认数据库：") || line.Contains("Default Geodatabase:")))
                    ctx.GdbPath = ExtractTaggedValue(line.Trim(), "默认数据库：", "Default Geodatabase:");
                else if (string.IsNullOrWhiteSpace(ctx.GdbPath)
                    && (line.Contains("默认数据库路径：") || line.Contains("DefaultGeodatabase:")))
                    ctx.GdbPath = ExtractTaggedValue(line.Trim(), "默认数据库路径：", "DefaultGeodatabase:");
            }

            // Fallback: GdbPath from path line if not found by name
            // 中文：备份：如果找不到路径线，则用GdbPath查找
            if (string.IsNullOrWhiteSpace(ctx.GdbPath) && !string.IsNullOrWhiteSpace(ctx.ProjectPath))
                ctx.GdbPath = _lastProjectContext.Split('\n')
                    .FirstOrDefault(l => l.Contains("默认数据库路径：") || l.Contains("DefaultGeodatabase:"))
                    is string gdbLine
                        ? ExtractTaggedValue(gdbLine.Trim(), "默认数据库路径：", "DefaultGeodatabase:")
                        : "";

            if (fullRefresh || string.IsNullOrEmpty(_lastMapContext))
            {
                try
                {
                    var summaryTask = _mapReader.GetAllLayerSummariesAsync();
                    var snapshotTask = _mapReader.GetMapContextAsync();
                    await Task.WhenAll(summaryTask, snapshotTask);
                    _lastMapContext = summaryTask.Result ?? "";
                    _lastMapSnapshot = snapshotTask.Result ?? new MapContextSnapshot();
                }
                catch (Exception ex)
                {
                    _lastMapContext = "";
                    _lastMapSnapshot = new MapContextSnapshot();
                    AgentDiagnostics.Warn("ContextManager.Map", "Map context refresh failed", ex);
                }
            }
            ctx.MapInfo = _lastMapContext;
            ctx.MapName = _lastMapSnapshot.MapName;
            ctx.Layers = _lastMapSnapshot.Layers.Select(layer => new LayerInfo
            {
                Name = layer.Name,
                MapMemberId = layer.MapMemberId,
                DatasetId = layer.DatasetId,
                DatasetName = layer.DatasetName,
                GeometryType = layer.GeometryType,
                FeatureCount = layer.FeatureCount,
                Fields = layer.Fields.ToList(),
                SchemaFingerprint = layer.SchemaFingerprint,
                StateFingerprint = layer.StateFingerprint,
                StateFingerprintReliable = layer.StateFingerprintReliable
            }).ToList();

            // Extract structured map name from the first line of MapInfo
            // 中文：从MapInfo的第一行提取结构化地图名称
            if (string.IsNullOrWhiteSpace(ctx.MapName) && !string.IsNullOrWhiteSpace(_lastMapContext))
            {
                var firstLine = _lastMapContext.Split('\n').FirstOrDefault()?.Trim() ?? "";
                if (firstLine.Contains("活动地图：") || firstLine.Contains("Active Map:"))
                    ctx.MapName = firstLine.Split('：', ':').Last().Trim();
            }

            try
            {
                string extentJson = await _mapReader.GetProjectExtentAsync();
                if (!string.IsNullOrEmpty(extentJson) && !extentJson.Contains("\"error\""))
                    ctx.MapInfo += "\n\n【项目空间范围】请根据坐标推断真实地理位置。\n" + extentJson;
            }
            catch (Exception ex)
            {
                AgentDiagnostics.Warn("ContextManager.Extent", "Project extent lookup failed", ex);
            }

            // ── P0-4: Spatial reference context collection ──
            // 中文：P0-4：空间参考上下文收集
            try
            {
                ctx.SpatialRef = await _mapReader.GetSpatialRefContextAsync();
            }
            catch (Exception ex)
            {
                AgentDiagnostics.Warn("ContextManager.SpatialRef",
                    "Spatial reference context collection failed", ex);
            }

            if (_knowledge != null && _knowledge.IsIndexed)
            {
                try
                {
                    var results = await _knowledge.SearchWithMetaAsync(userMessage, topK: 5);
                    ctx.KnowledgeChunks = results.Select(r =>
                    {
                        string head = !string.IsNullOrWhiteSpace(r.Source)
                            ? $"[{r.Source}] "
                            : "";
                        return head + r.Content;
                    }).ToList();
                }
                catch (Exception ex)
                {
                    AgentDiagnostics.Warn("ContextManager.Knowledge", "Knowledge search failed", ex);
                    ctx.KnowledgeChunks = new List<string>
                    {
                        "⚠️ 知识库检索失败：如果问题涉及中国国土空间规划标准，请如实说明不确定，不要编造规范或指标值。"
                    };
                }
            }

            ctx.SkillsSummary = _skillManager.GetSkillsSummary();
            ctx.MatchedSkills = await _skillManager.MatchSkillsAsync(userMessage);

            ctx.MemorySummary = _projectMemory?.GetSummary() ?? "";
            ctx.IsNewProject = _projectMemory?.HasHistory != true
                && string.IsNullOrEmpty(ctx.MemorySummary);
            ctx.W5H2Context = _projectMemory?.GetW5H2Context() ?? "";

            if (_toolIndex != null && _toolIndex.IsLoaded)
            {
                var geomTypes = ExtractGeometryTypes(_lastMapContext);
                var opHints = ExtractOperationHints(userMessage, ctx.MatchedSkills);
                ctx.RelevantTools = _toolIndex.FindRelevantWithFallback(
                    userMessage,
                    geomTypes,
                    opHints,
                    topN: RuntimePolicy.ToolIndexTopN);

                if (_toolSemanticScorer?.IsReady == true)
                {
                    try
                    {
                        var semantic = await _toolSemanticScorer.RankAsync(
                            userMessage,
                            topN: RuntimePolicy.ToolIndexTopN);
                        ctx.RelevantTools = MergeTools(
                            ctx.RelevantTools,
                            semantic,
                            RuntimePolicy.ToolIndexTopN);
                    }
                    catch (Exception ex)
                    {
                        AgentDiagnostics.Warn("ContextManager.ToolSemantic", "Semantic tool fallback failed", ex);
                    }
                }
            }

            // ★ 技能→工具暴露联动：技能命中即并入其声明的所需工具。
            // 拓扑链等低频工具从不进 top50，技能教了流程模型却调不到——
            // 技能声明是硬需求，绕过 index 评分直接暴露。
            // English：Skill → Tool Exposure Linkage: Skill hits are merged into the required tools for their declaration. Low-frequency tools like topologies never make it into the top 50, skills teach process models but can't be tuned — skill declarations are hard requirements, bypassing index scores and exposing them directly.
            var skillTools = ctx.MatchedSkills
                .SelectMany(s => s.RequiredTools)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (skillTools.Count > 0)
            {
                ctx.RelevantTools = (ctx.RelevantTools ?? new List<string>())
                    .Concat(skillTools)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            // Update context fingerprint after full refresh so dedup works next turn
            // 中文：在完全刷新后更新上下文指纹，让下回合去指纹生效
            if (fullRefresh)
            {
                _lastContextHash = ComputeContextFingerprint();
                _lastLiveFingerprint = await ComputeLiveFingerprintAsync();
            }

            return ctx;
        }

        public AgentContext GetCachedContext()
        {
            return new AgentContext
            {
                ProjectInfo = _lastProjectContext,
                MapInfo = _lastMapContext,
                MapName = _lastMapSnapshot.MapName,
                Layers = _lastMapSnapshot.Layers.Select(layer => new LayerInfo
                {
                    Name = layer.Name,
                    MapMemberId = layer.MapMemberId,
                    DatasetId = layer.DatasetId,
                    DatasetName = layer.DatasetName,
                    GeometryType = layer.GeometryType,
                    FeatureCount = layer.FeatureCount,
                    Fields = layer.Fields.ToList(),
                    SchemaFingerprint = layer.SchemaFingerprint,
                    StateFingerprint = layer.StateFingerprint,
                    StateFingerprintReliable = layer.StateFingerprintReliable
                }).ToList(),
                W5H2Context = _projectMemory?.GetW5H2Context() ?? "",
                MemorySummary = _projectMemory?.GetSummary() ?? ""
            };
        }

        public void InvalidateCache()
        {
            _msgCountSinceLastFullContext = FullContextRefreshInterval;
        }

        /// <summary>
        /// 判断工具是否"产出新数据集"（而非仅修改既有数据的字段内容）。
        /// 产出型工具触发上下文刷新以发现新图层/新表；字段型工具不触发。
        /// 判定基于工具名关键词匹配——保守设计：宁可多刷新，不可漏新图层。
        /// </summary>
        /// <remarks>English：Determine whether the tool is "generating a new dataset" (rather than just modifying the fields of existing data). Output-type tools trigger context refresh to discover new layers/tables; Field-type tools do not trigger. Determination is based on tool name keyword matching—conservative design: better to refresh more than to miss new layers.</remarks>
        private static bool CreatesNewOutput(string toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName)) return true;  // 未知时保守刷新。 / Refresh conservatively when unknown.
            string n = toolName.ToLowerInvariant();
            return n.Contains("clip") || n.Contains("buffer") || n.Contains("intersect")
                || n.Contains("erase") || n.Contains("dissolve") || n.Contains("merge")
                || n.Contains("project") || n.Contains("copy") || n.Contains("union")
                || n.Contains("export") || n.Contains("create") || n.Contains("grid")
                || n.Contains("near") || n.Contains("split") || n.Contains("append")
                || n.Contains("calculate_area") || n.Contains("calculate_length")
                || n.Contains("map_field_values");  // cc_map_field_values 可能创建中间表。 / cc_map_field_values may create an intermediate table.
        }

        private static HashSet<string> ExtractGeometryTypes(string? mapInfo)
        {
            var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(mapInfo)) return types;

            string lower = mapInfo.ToLowerInvariant();
            if (lower.Contains("polygon") || lower.Contains("面")) types.Add("polygon");
            if (lower.Contains("polyline") || lower.Contains("线")) types.Add("polyline");
            if (lower.Contains("point") || lower.Contains("点")) types.Add("point");
            if (lower.Contains("raster") || lower.Contains("栅格")) types.Add("raster");
            if (lower.Contains("table") || lower.Contains("表") || lower.Contains("standalone")) types.Add("table");

            if (types.Count == 0) types.Add("feature");
            return types;
        }

        private static HashSet<string> ExtractOperationHints(string userMessage, List<MatchedSkill> skills)
        {
            var hints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string lower = userMessage.ToLowerInvariant();

            if (lower.Contains("缓冲") || lower.Contains("buffer") || lower.Contains("距离")) hints.Add("proximity");
            if (lower.Contains("裁剪") || lower.Contains("clip") || lower.Contains("擦除") || lower.Contains("erase") || lower.Contains("相交") || lower.Contains("intersect")) hints.Add("overlay");
            if (lower.Contains("统计") || lower.Contains("汇总") || lower.Contains("sum") || lower.Contains("count")) hints.Add("statistics");
            if (lower.Contains("字段") || lower.Contains("计算") || lower.Contains("添加")) hints.Add("field");
            if (lower.Contains("选择") || lower.Contains("筛选") || lower.Contains("select") || lower.Contains("where")) hints.Add("select");
            if (lower.Contains("投影") || lower.Contains("坐标") || lower.Contains("project") || lower.Contains("crs") || lower.Contains("wkid")) hints.Add("crs");
            if (lower.Contains("转换") || lower.Contains("导出") || lower.Contains("convert") || lower.Contains("export")) hints.Add("convert");
            if (lower.Contains("合并") || lower.Contains("merge") || lower.Contains("连接") || lower.Contains("join")) hints.Add("merge");
            if (lower.Contains("融合") || lower.Contains("dissolve") || lower.Contains("简化")) hints.Add("generalize");
            if (lower.Contains("删除") || lower.Contains("delete") || lower.Contains("移除")) hints.Add("delete");
            if (lower.Contains("生成") || lower.Contains("创建") || lower.Contains("create")) hints.Add("create");
            if (lower.Contains("拓扑") || lower.Contains("检查") || lower.Contains("验证") || lower.Contains("validate")) hints.Add("validate");
            if (lower.Contains("网络") || lower.Contains("路径") || lower.Contains("network") || lower.Contains("route")) hints.Add("network");
            if (lower.Contains("坡度") || lower.Contains("坡向") || lower.Contains("等高线") || lower.Contains("地形")) hints.Add("surface");
            if (lower.Contains("密度") || lower.Contains("插值") || lower.Contains("density") || lower.Contains("interpolate")) hints.Add("interpolate");
            if (lower.Contains("分类") || lower.Contains("重分类") || lower.Contains("classify") || lower.Contains("reclass")) hints.Add("classify");

            foreach (var skill in skills)
            {
                string sn = skill.Name.ToLowerInvariant();
                if (sn.Contains("land") || sn.Contains("用地")) { hints.Add("statistics"); hints.Add("classify"); }
                if (sn.Contains("topolog") || sn.Contains("拓扑")) hints.Add("validate");
                if (sn.Contains("data") || sn.Contains("数据")) hints.Add("convert");
                if (sn.Contains("buffer") || sn.Contains("缓冲")) hints.Add("proximity");
            }


            if (hints.Count == 0) hints.Add("create");
            return hints;
        }

        private static List<string> MergeTools(IEnumerable<string> primary, IEnumerable<string> secondary, int topN)
        {
            var merged = new List<string>();
            foreach (var name in primary.Concat(secondary))
            {
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                if (!merged.Contains(name, StringComparer.OrdinalIgnoreCase))
                    merged.Add(name);

                if (merged.Count >= topN)
                    break;
            }

            return merged;
        }

        private static string ExtractTaggedValue(string line, params string[] prefixes)
        {
            foreach (var prefix in prefixes)
            {
                int index = line.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
                if (index >= 0)
                    return line[(index + prefix.Length)..].Trim();
            }

            return "";
        }
    }
}
