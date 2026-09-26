using FluentAssertions;
using Newtonsoft.Json;
using SpatialAgent.Runtime;
using SpatialAgent.Runtime.Contracts;
using SpatialAgent.Services;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    public class CheckpointStoreTests
    {
        [Fact]
        public async Task Save_and_load_latest_roundtrips_agent_state()
        {
            string root = Path.Combine(Path.GetTempPath(), "spatial-agent-checkpoints-" + Guid.NewGuid().ToString("N"));
            var store = new FileRunCheckpointStore(root);

            var state = new AgentRunState
            {
                ThreadId = "thread-1",
                Turn = 4,
                Round = 2,
                UserMessage = "hello",
                Status = SessionStatus.WaitingForApproval,
                PermissionLevel = AgentPermissionLevel.RequestApproval,
                Messages = new List<Message>
                {
                    new() { Role = "user", Content = "hello" }
                },
                Lineage = new List<DataLineageRecord>
                {
                    new() { Output = "normalized_assets", Parent = "raw_assets" }
                },
                Artifacts = new List<DomainArtifactRecord>
                {
                    new()
                    {
                        OwnerLayer = "roads",
                        FieldName = "DXCD",
                        SemanticType = GisCapabilityIds.MeasureLength
                    }
                }
            };

            await store.SaveAsync(state);

            var loaded = await store.LoadLatestAsync("thread-1");
            loaded.Should().NotBeNull();
            loaded!.ThreadId.Should().Be("thread-1");
            loaded.Turn.Should().Be(4);
            loaded.Round.Should().Be(2);
            loaded.Messages.Should().HaveCount(1);
            loaded.Lineage.Should().ContainSingle()
                .Which.Parent.Should().Be("raw_assets");
            loaded.Artifacts.Should().ContainSingle()
                .Which.FieldName.Should().Be("DXCD");

            await store.DeleteAsync("thread-1");
            Directory.Exists(Path.Combine(root, ".agent", "checkpoints", "thread-1")).Should().BeFalse();
        }

        [Fact]
        public async Task Legacy_checkpoint_is_migrated_in_memory()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "spatial-agent-checkpoints-" + Guid.NewGuid().ToString("N"));
            string threadDir = Path.Combine(root, ".agent", "checkpoints", "legacy");
            Directory.CreateDirectory(threadDir);
            await File.WriteAllTextAsync(
                Path.Combine(threadDir, "turn_0001_round_01.json"),
                """{"ThreadId":"legacy","Turn":1,"Messages":[]}""");
            var events = new List<AgentEvent>();
            var bus = new EventBus();
            bus.Subscribe("checkpoint.migrated", events.Add);
            var store = new FileRunCheckpointStore(root, eventBus: bus);

            var state = await store.LoadLatestAsync("legacy");

            state.Should().NotBeNull();
            state!.SchemaVersion.Should().Be(AgentRunState.CurrentSchemaVersion);
            state.RuntimeVersion.Should().Be("legacy");
            events.Should().ContainSingle();
            Directory.Delete(root, true);
        }

        [Fact]
        public async Task Current_checkpoint_with_null_plan_collections_is_normalized()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "spatial-agent-checkpoints-" + Guid.NewGuid().ToString("N"));
            string threadDir = Path.Combine(root, ".agent", "checkpoints", "normalized");
            Directory.CreateDirectory(threadDir);
            await File.WriteAllTextAsync(
                Path.Combine(threadDir, "turn_0001_round_01.json"),
                JsonConvert.SerializeObject(new
                {
                    SchemaVersion = AgentRunState.CurrentSchemaVersion,
                    ThreadId = "normalized",
                    Turn = 1,
                    Plan = new
                    {
                        Steps = new[]
                        {
                            new
                            {
                                Id = 1,
                                DependsOn = (object?)null,
                                RequiresSuccessfulSteps = (object?)null,
                                ReadSet = (object?)null,
                                WriteSet = (object?)null,
                                Preconditions = (object?)null
                            }
                        }
                    }
                }));
            var store = new FileRunCheckpointStore(root);

            var state = await store.LoadLatestAsync("normalized");

            PlanStep step = state!.Plan!.Steps.Should().ContainSingle().Subject;
            step.DependsOn.Should().NotBeNull().And.BeEmpty();
            step.RequiresSuccessfulSteps.Should().NotBeNull().And.BeEmpty();
            step.ReadSet.Should().NotBeNull().And.BeEmpty();
            step.WriteSet.Should().NotBeNull().And.BeEmpty();
            step.Preconditions.Should().NotBeNull().And.BeEmpty();
            Directory.Delete(root, true);
        }

        [Fact]
        public async Task Corrupt_latest_checkpoint_falls_back_and_emits_diagnostic()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "spatial-agent-checkpoints-" + Guid.NewGuid().ToString("N"));
            var events = new List<AgentEvent>();
            var bus = new EventBus();
            bus.Subscribe("checkpoint.load_failed", events.Add);
            var store = new FileRunCheckpointStore(root, eventBus: bus);
            await store.SaveAsync(new AgentRunState
            {
                ThreadId = "fallback",
                Turn = 1,
                Round = 1
            });
            await store.SaveAsync(new AgentRunState
            {
                ThreadId = "fallback",
                Turn = 2,
                Round = 1
            });
            string latest = Path.Combine(
                root,
                ".agent",
                "checkpoints",
                "fallback",
                "turn_0002_round_01.json");
            await File.WriteAllTextAsync(latest, "{not-json");

            var state = await store.LoadLatestAsync("fallback");

            state.Should().NotBeNull();
            state!.Turn.Should().Be(1);
            events.Should().ContainSingle();
            Directory.Delete(root, true);
        }

        [Fact]
        public async Task Future_checkpoint_schema_is_rejected()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "spatial-agent-checkpoints-" + Guid.NewGuid().ToString("N"));
            string threadDir = Path.Combine(root, ".agent", "checkpoints", "future");
            Directory.CreateDirectory(threadDir);
            await File.WriteAllTextAsync(
                Path.Combine(threadDir, "turn_0001_round_01.json"),
                JsonConvert.SerializeObject(new
                {
                    SchemaVersion = AgentRunState.CurrentSchemaVersion + 1,
                    ThreadId = "future",
                    Turn = 1
                }));
            var events = new List<AgentEvent>();
            var bus = new EventBus();
            bus.Subscribe("checkpoint.unsupported_version", events.Add);
            var store = new FileRunCheckpointStore(root, eventBus: bus);

            var state = await store.LoadLatestAsync("future");

            state.Should().BeNull();
            events.Should().ContainSingle();
            Directory.Delete(root, true);
        }

        [Fact]
        public async Task Retention_keeps_latest_checkpoints_per_thread()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "spatial-agent-checkpoints-" + Guid.NewGuid().ToString("N"));
            var store = new FileRunCheckpointStore(
                root,
                new CheckpointRetentionPolicy
                {
                    MaxCheckpointsPerThread = 2,
                    MaxAge = TimeSpan.FromDays(365),
                    MaxTotalBytes = long.MaxValue
                });

            for (int turn = 1; turn <= 4; turn++)
            {
                await store.SaveAsync(new AgentRunState
                {
                    ThreadId = "retention",
                    Turn = turn,
                    Round = 1
                });
            }

            string threadDir = Path.Combine(
                root,
                ".agent",
                "checkpoints",
                "retention");
            var files = Directory.GetFiles(threadDir, "turn_*.json")
                .Select(Path.GetFileName)
                .OrderBy(name => name)
                .ToList();
            files.Should().Equal(
                "turn_0003_round_01.json",
                "turn_0004_round_01.json");
            Directory.Delete(root, true);
        }

        [Fact]
        public async Task Current_and_previous_goal_scopes_roundtrip_independently()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "spatial-agent-checkpoints-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new FileRunCheckpointStore(root);
                await store.SaveAsync(new AgentRunState
                {
                    ThreadId = "scope-thread",
                    Turn = 4,
                    Round = 2,
                    GoalScope = new ResolvedGoalScope
                    {
                        SourceGoal = "current field task",
                        ExplicitTargets = new() { "parcels" }
                    },
                    PreviousGoalScope = new ResolvedGoalScope
                    {
                        SourceGoal = "previous length task",
                        ExplicitTargets = new() { "roads" },
                        SatisfiedTargets = new() { "roads" }
                    }
                });

                var loaded = await store.LoadLatestAsync("scope-thread");

                loaded.Should().NotBeNull();
                loaded!.GoalScope!.SourceGoal.Should().Be("current field task");
                loaded.PreviousGoalScope!.SourceGoal.Should()
                    .Be("previous length task");
                loaded.GoalScope.ExplicitTargets.Should().Equal("parcels");
                loaded.PreviousGoalScope.SatisfiedTargets.Should().Equal("roads");
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
        }

        [Fact]
        public async Task Critical_shutdown_save_is_atomic_and_becomes_latest()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "spatial-agent-checkpoints-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new FileRunCheckpointStore(root);
                store.SaveCritical(new AgentRunState
                {
                    ThreadId = "shutdown-thread",
                    Turn = 3,
                    Round = 99,
                    Status = SessionStatus.Interrupted,
                    TerminationReason = "MainWindowClosed",
                    GoalScope = new ResolvedGoalScope
                    {
                        SourceGoal = "process all line layers",
                        SatisfiedTargets = new() { "roads" }
                    },
                    Messages = new()
                    {
                        new Message { Role = "user", Content = "last request" }
                    }
                });

                var loaded = await store.LoadLatestAsync("shutdown-thread");

                loaded.Should().NotBeNull();
                loaded!.Round.Should().Be(99);
                loaded.TerminationReason.Should().Be("MainWindowClosed");
                loaded.GoalScope!.SatisfiedTargets.Should().Equal("roads");
                string threadDir = Path.Combine(
                    root,
                    ".agent",
                    "checkpoints",
                    "shutdown-thread");
                File.ReadAllText(Path.Combine(threadDir, "latest.json"))
                    .Should().Contain("\"round\": 99");
                Directory.GetFiles(threadDir, "*.tmp").Should().BeEmpty();
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
        }
    }
}
