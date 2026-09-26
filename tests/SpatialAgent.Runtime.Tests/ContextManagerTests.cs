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

        /// <summary>
        /// 每轮强制全量刷新——不再依赖消息计数或 VAV 指纹判断。
        /// 连续 3 次调用 = 3 次 GetAllLayerSummariesAsync。
        /// </summary>
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

            var skillProvider = Substitute.For<ISkillProvider>();
            skillProvider.GetSkillsSummary().Returns("");
            skillProvider.MatchSkillsAsync(Arg.Any<string>()).Returns(Task.FromResult(new List<MatchedSkill>()));

            var ctx = new ContextManager(projectReader, mapReader, skillProvider, knowledge: null);

            // First call: initial full refresh
            await ctx.GatherLightAsync("hello");
            // Second call: GIS keyword "图层" triggers needFull → VAV check → fingerprint changed → full refresh
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
                FullContextRefreshInterval = 99 // very high, won't trigger by count
            };
            ctx.SetEventBus(eventBus);

            // First call: initial full refresh
            await ctx.GatherLightAsync("hello");

            // Fire Destructive event → cache should be invalidated
            eventBus.Publish(new AgentEvent
            {
                EventType = "tool.completed",
                Metadata = new() { ["risk"] = "Destructive", ["tool"] = "DeleteField" }
            });

            // Next call: FullContextRefreshInterval not met, but cache invalidated → count=interval → full refresh
            var result = await ctx.GatherLightAsync("hello again");
            await mapReader.Received(2).GetAllLayerSummariesAsync();
        }

        /// <summary>
        /// 5B-4：Modify+新输出使缓存失效。
        /// Buffer 创建新图层 → CreatesNewOutput=true → 缓存失效。
        /// </summary>
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
            await ctx.GatherLightAsync("hello");

            // Fire Modify event with Buffer (creates new output) → cache invalidated
            eventBus.Publish(new AgentEvent
            {
                EventType = "tool.completed",
                Metadata = new() { ["risk"] = "Modify", ["tool"] = "analysis_Buffer" }
            });

            // Next call triggers full refresh
            await ctx.GatherLightAsync("hello again");
            await mapReader.Received(2).GetAllLayerSummariesAsync();
        }

        /// <summary>
        /// 每轮强制全量刷新——即使普通 Modify 不创建新图层也刷新。
        /// 工具执行后的字段值变化在下轮计算中必须可见。
        /// </summary>
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
        [Fact]
        public async Task Probe_failure_forces_conservative_full_refresh()
        {
            var mapReader = Substitute.For<IMapReader>();
            // First call succeeds, second fails
            mapReader.GetMapInfoAsync().Returns("map-v1", (string)null!);
            // When GetMapInfoAsync fails at the await point, it's the TASK that faults.
            // Simulate: first success, then throw
            var callCount = 0;
            mapReader.GetMapInfoAsync().Returns(_ =>
            {
                callCount++;
                if (callCount >= 3) // third+ calls (after initial) throw
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
                FullContextRefreshInterval = 1 // trigger needFull on second call
            };

            // First call: initial full refresh
            await ctx.GatherLightAsync("hello");
            // Second call: needFull → probe fails → conservative full refresh
            var result = await ctx.GatherLightAsync("hello");

            // Both calls should have triggered GetAllLayerSummaries
            await mapReader.Received(2).GetAllLayerSummariesAsync();
        }
    }
}
