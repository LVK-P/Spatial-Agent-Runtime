using FluentAssertions;
using NSubstitute;
using SpatialAgent.Runtime;
using SpatialAgent.Runtime.Contracts;
using SpatialAgent.Core;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    public class ContextManagerTests
    {
        [Fact]
        public async Task GatherLightAsync_always_does_full_refresh_every_turn()
        {
            var projectReader = Substitute.For<IProjectReader>();
            projectReader.GetContextForAIAsync().Returns("项目：测试项目\n家目录：G:\\test");

            var mapReader = Substitute.For<IMapReader>();
            mapReader.GetMapInfoAsync().Returns("{}");
            mapReader.GetAllLayerSummariesAsync().Returns("图层摘要v1", "图层摘要v2");
            mapReader.GetProjectExtentAsync().Returns("");
            mapReader.GetSpatialRefContextAsync().Returns(new SpatialRefContext());

            var skillProvider = Substitute.For<ISkillProvider>();
            skillProvider.GetSkillsSummary().Returns("");
            skillProvider.MatchSkillsAsync(Arg.Any<string>()).Returns(Task.FromResult(new List<MatchedSkill>()));

            var ctx = new ContextManager(projectReader, mapReader, skillProvider, knowledge: null);

            var first = await ctx.GatherLightAsync("图层情况");
            var second = await ctx.GatherLightAsync("再看看图层情况");

            // 每轮强制全量刷新——即使宿主状态未变，也重新读取图层信息
            // English：Each round is forced to refresh the entire layer—even if the host's state remains unchanged, layer information is reread
            first.MapInfo.Should().Be("图层摘要v1");
            second.MapInfo.Should().Be("图层摘要v2");
            await mapReader.Received(2).GetAllLayerSummariesAsync();
        }

        [Fact]
        public async Task GatherLightAsync_refreshes_full_context_when_live_fingerprint_changes()
        {
            var projectReader = Substitute.For<IProjectReader>();
            projectReader.GetContextForAIAsync()
                .Returns(
                    "项目：测试项目\n家目录：G:\\test",
                    "项目：测试项目\n家目录：G:\\test",
                    "项目：测试项目\n家目录：G:\\test2",
                    "项目：测试项目\n家目录：G:\\test2");

            var mapReader = Substitute.For<IMapReader>();
            mapReader.GetMapInfoAsync().Returns("map-a", "map-a", "map-b", "map-b");
            mapReader.GetAllLayerSummariesAsync().Returns("初始图层摘要", "刷新后图层摘要");
            mapReader.GetProjectExtentAsync().Returns("");
            mapReader.GetSpatialRefContextAsync().Returns(new SpatialRefContext());

            var skillProvider = Substitute.For<ISkillProvider>();
            skillProvider.GetSkillsSummary().Returns("");
            skillProvider.MatchSkillsAsync(Arg.Any<string>()).Returns(Task.FromResult(new List<MatchedSkill>()));

            var ctx = new ContextManager(projectReader, mapReader, skillProvider, knowledge: null)
            {
                FullContextRefreshInterval = 1
            };

            var first = await ctx.GatherLightAsync("图层情况");
            var second = await ctx.GatherLightAsync("再看看图层情况");

            first.MapInfo.Should().Be("初始图层摘要");
            second.MapInfo.Should().Be("刷新后图层摘要");
            await mapReader.Received(2).GetAllLayerSummariesAsync();
        }

        [Fact]
        public async Task GatherAsync_preserves_windows_drive_letter_in_english_project_context()
        {
            var projectReader = Substitute.For<IProjectReader>();
            projectReader.GetContextForAIAsync().Returns(
                "Project: TestProject\nHomeFolder: G:\\workspace\nDefaultGeodatabase: G:\\workspace\\default.gdb");

            var mapReader = Substitute.For<IMapReader>();
            mapReader.GetMapInfoAsync().Returns("Active Map: MainMap");
            mapReader.GetAllLayerSummariesAsync().Returns("Active Map: MainMap");
            mapReader.GetProjectExtentAsync().Returns("");
            mapReader.GetSpatialRefContextAsync().Returns(new SpatialRefContext());

            var skillProvider = Substitute.For<ISkillProvider>();
            skillProvider.GetSkillsSummary().Returns("");
            skillProvider.MatchSkillsAsync(Arg.Any<string>()).Returns(Task.FromResult(new List<MatchedSkill>()));

            var ctx = new ContextManager(projectReader, mapReader, skillProvider, knowledge: null);

            var result = await ctx.GatherAsync("project info");

            result.ProjectName.Should().Be("TestProject");
            result.ProjectPath.Should().Be("G:\\workspace");
            result.GdbPath.Should().Be("G:\\workspace\\default.gdb");
            result.MapName.Should().Be("MainMap");
        }

        // ════════════════════════════════════════════════════════════════
        // Phase 5B — P0 6 测试：VAV 全分支 + 缓存失效 + 技能合并
        // ════════════════════════════════════════════════════════════════
        // English：Phase 5B — P0 6 Test: VAV full branch + cache failure + skill merge

        /// <summary>
        /// 每轮强制全量刷新——不再依赖消息计数或 VAV 指纹判断。
        /// 连续 3 次调用 = 3 次 GetAllLayerSummariesAsync。
        /// </summary>
        /// <remarks>English：Each round of forced full refresh — no longer relying on message counting or VAV fingerprint judgment. 3 consecutive calls = 3 GetAllLayerSummariesAsync.</remarks>
        [Fact]
        public async Task Every_turn_triggers_full_refresh()
        {
            var mapReader = Substitute.For<IMapReader>();
            mapReader.GetMapInfoAsync().Returns("{}");
            mapReader.GetAllLayerSummariesAsync().Returns("layers-v1", "layers-v2", "layers-v3");
            mapReader.GetProjectExtentAsync().Returns("");
            mapReader.GetSpatialRefContextAsync().Returns(new SpatialRefContext());

            var projectReader = Substitute.For<IProjectReader>();
            projectReader.GetContextForAIAsync().Returns("proj");

            var skillProvider = Substitute.For<ISkillProvider>();
            skillProvider.GetSkillsSummary().Returns("");
            skillProvider.MatchSkillsAsync(Arg.Any<string>()).Returns(Task.FromResult(new List<MatchedSkill>()));

            var ctx = new ContextManager(projectReader, mapReader, skillProvider, knowledge: null);

            await ctx.GatherLightAsync("msg1");
            await ctx.GatherLightAsync("msg2");
            await ctx.GatherLightAsync("msg3");

            await mapReader.Received(3).GetAllLayerSummariesAsync();
        }

        /// <summary>
        /// 5B-2：GIS 关键词触发全量刷新路径。
        /// msg="缓冲区分析" 触发 needFull（含"分析"但"分析"不在关键词列表…实际用"图层"）。
        /// </summary>
        /// <remarks>English：5B-2: GIS keyword triggers the full refresh path. msg="Buffer Analysis" triggers needFull(including "Analysis" but "Analysis" is not in the keyword list...) In practice, use "layers").</remarks>
        [Fact]
        public async Task Gis_keyword_triggers_full_refresh_path()
        {
            var mapReader = Substitute.For<IMapReader>();
            mapReader.GetMapInfoAsync().Returns("map-v1", "map-v2");
            mapReader.GetAllLayerSummariesAsync().Returns("layers-v1", "layers-v2");
            mapReader.GetProjectExtentAsync().Returns("");
            mapReader.GetSpatialRefContextAsync().Returns(new SpatialRefContext());

            var projectReader = Substitute.For<IProjectReader>();
            projectReader.GetContextForAIAsync().Returns("proj-v1", "proj-v1", "proj-v2", "proj-v2");
            // Note: ComputeLiveFingerprintAsync calls both GetMapInfoAsync and GetContextForAIAsync
            // 中文：注意：ComputeLiveFingerprintAsync 调用 GetMapInfoAsync 和 GetContextForAIAsync

            var skillProvider = Substitute.For<ISkillProvider>();
            skillProvider.GetSkillsSummary().Returns("");
            skillProvider.MatchSkillsAsync(Arg.Any<string>()).Returns(Task.FromResult(new List<MatchedSkill>()));

            var ctx = new ContextManager(projectReader, mapReader, skillProvider, knowledge: null);

            // First call: initial full refresh
            // 中文：第一次通话：初步全面刷新
            await ctx.GatherLightAsync("hello");
            // Second call: GIS keyword "图层" triggers needFull → VAV check → fingerprint changed → full refresh
            // 中文：第二次通话：触发GIS关键词“图层”，需要全面→VAV检查，→指纹更改→完全刷新
            await ctx.GatherLightAsync("图层分析");

            await mapReader.Received(2).GetAllLayerSummariesAsync();
        }

        /// <summary>
        /// 5B-3：Destructive 事件使缓存失效。
        /// 发布 tool.completed { risk: Destructive } → 下次 GatherLight 强制全量刷新。
        /// </summary>
        [Fact]
        public async Task Destructive_event_invalidates_cache()
        {
            var eventBus = new EventBus();
            var mapReader = Substitute.For<IMapReader>();
            mapReader.GetMapInfoAsync().Returns("{}");
            mapReader.GetAllLayerSummariesAsync().Returns("layers-v1", "layers-v2");
            mapReader.GetProjectExtentAsync().Returns("");
            mapReader.GetSpatialRefContextAsync().Returns(new SpatialRefContext());

            var projectReader = Substitute.For<IProjectReader>();
            projectReader.GetContextForAIAsync().Returns("proj");

            var skillProvider = Substitute.For<ISkillProvider>();
            skillProvider.GetSkillsSummary().Returns("");
            skillProvider.MatchSkillsAsync(Arg.Any<string>()).Returns(Task.FromResult(new List<MatchedSkill>()));

            var ctx = new ContextManager(projectReader, mapReader, skillProvider, knowledge: null)
            {
                FullContextRefreshInterval = 99 // 很高，不会因计数触发。 / Very high, so the count will not trigger it.
            };
            ctx.SetEventBus(eventBus);

            // First call: initial full refresh
            // 中文：第一次通话：初步全面刷新
            await ctx.GatherLightAsync("hello");

            // Fire Destructive event → cache should be invalidated
            // 中文：火灾破坏事件→缓存应被取消
            eventBus.Publish(new AgentEvent
            {
                EventType = "tool.completed",
                Metadata = new() { ["risk"] = "Destructive", ["tool"] = "DeleteField" }
            });

            // Next call: FullContextRefreshInterval not met, but cache invalidated → count=interval → full refresh
            // 中文：下一次调用：FullContextRefreshInterval 未达标，但缓存失效 → count=interval → full refresh
            var result = await ctx.GatherLightAsync("hello again");
            await mapReader.Received(2).GetAllLayerSummariesAsync();
        }

        /// <summary>
        /// 5B-4：Modify+新输出使缓存失效。
        /// Buffer 创建新图层 → CreatesNewOutput=true → 缓存失效。
        /// </summary>
        /// <remarks>English：5B-4: Modify+ new output causes cache to break down. Buffer creates a new layer → CreatesNewOutput=true → cache expired.</remarks>
        [Fact]
        public async Task Modify_with_new_output_invalidates_cache()
        {
            var eventBus = new EventBus();
            var mapReader = Substitute.For<IMapReader>();
            mapReader.GetMapInfoAsync().Returns("{}");
            mapReader.GetAllLayerSummariesAsync().Returns("layers-v1", "layers-v2");
            mapReader.GetProjectExtentAsync().Returns("");
            mapReader.GetSpatialRefContextAsync().Returns(new SpatialRefContext());

            var projectReader = Substitute.For<IProjectReader>();
            projectReader.GetContextForAIAsync().Returns("proj");

            var skillProvider = Substitute.For<ISkillProvider>();
            skillProvider.GetSkillsSummary().Returns("");
            skillProvider.MatchSkillsAsync(Arg.Any<string>()).Returns(Task.FromResult(new List<MatchedSkill>()));

            var ctx = new ContextManager(projectReader, mapReader, skillProvider, knowledge: null)
            {
                FullContextRefreshInterval = 99
            };
            ctx.SetEventBus(eventBus);

            // First call: initial full refresh
            // 中文：第一次通话：初步全面刷新
            await ctx.GatherLightAsync("hello");

            // Fire Modify event with Buffer (creates new output) → cache invalidated
            // 中文：带缓冲区的触发修改事件（创建新输出）→缓存失效
            eventBus.Publish(new AgentEvent
            {
                EventType = "tool.completed",
                Metadata = new() { ["risk"] = "Modify", ["tool"] = "analysis_Buffer" }
            });

            // Next call triggers full refresh
            // 中文：下一次通话触发完全刷新
            await ctx.GatherLightAsync("hello again");
            await mapReader.Received(2).GetAllLayerSummariesAsync();
        }

        /// <summary>
        /// 每轮强制全量刷新——即使普通 Modify 不创建新图层也刷新。
        /// 工具执行后的字段值变化在下轮计算中必须可见。
        /// </summary>
        /// <remarks>English：Forced full refresh every round—even if regular Modify doesn't create a new layer, it refreshes. The changes in field values after tool execution must be visible in the next round of calculation.</remarks>
        [Fact]
        public async Task Every_turn_refreshes_even_after_normal_modify()
        {
            var eventBus = new EventBus();
            var mapReader = Substitute.For<IMapReader>();
            mapReader.GetMapInfoAsync().Returns("{}");
            mapReader.GetAllLayerSummariesAsync().Returns("layers-v1", "layers-v2");
            mapReader.GetProjectExtentAsync().Returns("");
            mapReader.GetSpatialRefContextAsync().Returns(new SpatialRefContext());

            var projectReader = Substitute.For<IProjectReader>();
            projectReader.GetContextForAIAsync().Returns("proj");

            var skillProvider = Substitute.For<ISkillProvider>();
            skillProvider.GetSkillsSummary().Returns("");
            skillProvider.MatchSkillsAsync(Arg.Any<string>()).Returns(Task.FromResult(new List<MatchedSkill>()));

            var ctx = new ContextManager(projectReader, mapReader, skillProvider, knowledge: null);
            ctx.SetEventBus(eventBus);

            await ctx.GatherLightAsync("hello");

            eventBus.Publish(new AgentEvent
            {
                EventType = "tool.completed",
                Metadata = new() { ["risk"] = "Modify", ["tool"] = "management_CalculateField" }
            });

            await ctx.GatherLightAsync("hello again");
            await mapReader.Received(2).GetAllLayerSummariesAsync();
        }

        /// <summary>
        /// 5B-6：探针失败保守全量刷新。
        /// IMapReader.GetMapInfoAsync 抛异常 → ComputeLiveFingerprint 返回随机 GUID
        /// → 与 _lastLiveFingerprint 不同 → 强制 full refresh（不静默跳过）。
        /// </summary>
        /// <remarks>English：5B-6: Probe failure conservatively refreshes all doses. IMapReader.GetMapInfoAsync throws exceptions → ComputeLiveFingerprint returns random GUIDs→ Different from _lastLiveFingerprint → Forces full refresh (skips without silent).</remarks>
        [Fact]
        public async Task Probe_failure_forces_conservative_full_refresh()
        {
            var mapReader = Substitute.For<IMapReader>();
            // First call succeeds, second fails
            // 中文：第一次呼叫成功，第二次失败
            mapReader.GetMapInfoAsync().Returns("map-v1", (string)null!);
            // When GetMapInfoAsync fails at the await point, it's the TASK that faults.
            // Simulate: first success, then throw
            // 中文：当 GetMapInfoAsync 在等待点失败时，是任务出错了。模拟：先成功，然后投掷
            var callCount = 0;
            mapReader.GetMapInfoAsync().Returns(_ =>
            {
                callCount++;
                if (callCount >= 3) // 初始调用后的第三次及以后调用抛出异常。 / Third and later calls after the initial call throw.
                    throw new InvalidOperationException("Probe failed");
                return Task.FromResult("map-ok");
            });
            mapReader.GetAllLayerSummariesAsync().Returns("layers-v1", "layers-v2");
            mapReader.GetProjectExtentAsync().Returns("");
            mapReader.GetSpatialRefContextAsync().Returns(new SpatialRefContext());

            var projectReader = Substitute.For<IProjectReader>();
            projectReader.GetContextForAIAsync().Returns("proj");

            var skillProvider = Substitute.For<ISkillProvider>();
            skillProvider.GetSkillsSummary().Returns("");
            skillProvider.MatchSkillsAsync(Arg.Any<string>()).Returns(Task.FromResult(new List<MatchedSkill>()));

            var ctx = new ContextManager(projectReader, mapReader, skillProvider, knowledge: null)
            {
                FullContextRefreshInterval = 1 // 第二次调用时触发 needFull。 / Trigger needFull on the second call.
            };

            // First call: initial full refresh
            // 中文：第一次通话：初步全面刷新
            await ctx.GatherLightAsync("hello");
            // Second call: needFull → probe fails → conservative full refresh
            // 中文：第二次调用需要 Full；探测在保守的完整刷新中失败
            var result = await ctx.GatherLightAsync("hello");

            // Both calls should have triggered GetAllLayerSummaries
            // 中文：这两次调用都应该触发GetAllLayerSummaries
            await mapReader.Received(2).GetAllLayerSummariesAsync();
        }
    }
}
