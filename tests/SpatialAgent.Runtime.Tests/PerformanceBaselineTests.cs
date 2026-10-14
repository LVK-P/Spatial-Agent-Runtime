using System.Diagnostics;
using System.Text;
using FluentAssertions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SpatialAgent.Adapters.ArcGIS;
using SpatialAgent.Runtime;
using SpatialAgent.Runtime.Contracts;
using SpatialAgent.Runtime.Planning;
using SpatialAgent.Services;
using Xunit;
using Xunit.Abstractions;

namespace SpatialAgent.Runtime.Tests
{
    [CollectionDefinition("GP dispatcher", DisableParallelization = true)]
    public sealed class GpDispatcherCollection
    {
    }

    /// <summary>
    /// Broad regression budgets, not microbenchmarks. The generous limits are
    /// intended to catch architectural explosions on ordinary desktop runners
    /// while detailed ArcGIS GP timings remain in the Pro integration matrix.
    /// </summary>
    /// <remarks>中文：广泛回归预算，而非微基准测试。这些宽松的限制旨在捕捉普通桌面运行者的架构爆炸，同时详细的ArcGIS GP时序仍保留在Pro集成矩阵中。</remarks>
    [Collection("GP dispatcher")]
    public sealed class PerformanceBaselineTests
    {
        private readonly ITestOutputHelper _output;

        public PerformanceBaselineTests(ITestOutputHelper output) => _output = output;

        private sealed class Descriptor : IToolDescriptor, IToolDataflowDescriptor
        {
            public string Name => "analysis_Copy";
            public string Description => "Copy a dataset.";
            public string Category => "analysis";
            public ToolAnnotation Annotation => ToolAnnotation.Increment;
            public IReadOnlyDictionary<string, ParameterProperty> Parameters { get; }
                = new Dictionary<string, ParameterProperty>();
            public IReadOnlyList<string> RequiredParameters { get; } = Array.Empty<string>();
            public VerificationRule? Verification => null;
            public IReadOnlyDictionary<string, string>? DefaultParameters => null;
            public IReadOnlyList<string> InputParameters { get; } = new[] { "in_features" };
            public IReadOnlyList<string> OutputParameters { get; } = new[] { "out_feature_class" };
            public string? PrimaryOutputParameter => "out_feature_class";
        }

        private sealed class Executor : IToolExecutor
        {
            public Task<ToolResult> ExecuteAsync(
                ToolCall call,
                CancellationToken ct = default) =>
                Task.FromResult(new ToolResult { ToolName = call.Name, Success = true });
        }

        private sealed class InlineDispatcher : IGisDispatcher
        {
            public Task<T> RunAsync<T>(
                Func<Task<T>> operation,
                CancellationToken ct = default) => operation();

            public Task RunAsync(
                Func<Task> operation,
                CancellationToken ct = default) => operation();
        }

        [Fact]
        [Trait("Category", "Performance")]
        public void Dynamic_manifest_load_stays_within_desktop_budget()
        {
            string root = FindRepoRoot();
            JObject budget = LoadBudget(root, "dynamic_tool_load");
            string manifestPath = Path.Combine(
                Path.GetTempPath(),
                $"spatial_agent_performance_manifest_{Guid.NewGuid():N}.json");
            var tools = new JArray(
                Enumerable.Range(0, 2000).Select(index => new JObject
                {
                    ["name"] = $"SyntheticTool{index}",
                    ["function_name"] = $"local_SyntheticTool{index}",
                    ["execution_name"] = $"local.SyntheticTool{index}",
                    ["summary"] = "Synthetic performance fixture.",
                    ["risk_level"] = "ReadOnly",
                    ["params"] = new JArray(new JObject
                    {
                        ["name"] = "input_value",
                        ["type"] = "string",
                        ["required"] = true,
                        ["direction"] = "Input",
                        ["parameter_type"] = "Required",
                        ["order"] = 0
                    })
                }));
            File.WriteAllText(
                manifestPath,
                new JObject { ["Local Tools.tbx"] = tools }.ToString());
            GC.Collect();
            long before = GC.GetTotalMemory(forceFullCollection: true);
            var stopwatch = Stopwatch.StartNew();

            var registry = new ToolRegistry();
            int registered;
            try
            {
                GPToolRunner.DefaultDispatcher = new InlineDispatcher();
                registered = DynamicToolLoader.LoadAndRegister(
                    manifestPath,
                    registry);
            }
            finally
            {
                GPToolRunner.DefaultDispatcher = null;
                if (File.Exists(manifestPath))
                    File.Delete(manifestPath);
            }

            stopwatch.Stop();
            long memoryDelta = Math.Max(
                0,
                GC.GetTotalMemory(forceFullCollection: true) - before);
            WriteMetric("dynamic_tool_load", stopwatch.ElapsedMilliseconds, memoryDelta);

            registered.Should().BeGreaterThanOrEqualTo(
                budget["minimum_registered_tools"]!.Value<int>());
            stopwatch.ElapsedMilliseconds.Should().BeLessThan(
                budget["maximum_elapsed_ms"]!.Value<long>());
            BytesToMegabytes(memoryDelta).Should().BeLessThan(
                budget["maximum_managed_memory_delta_mb"]!.Value<double>());
        }

        [Fact]
        [Trait("Category", "Performance")]
        public void Fifty_step_plan_compilation_stays_within_budget()
        {
            string root = FindRepoRoot();
            JObject budget = LoadBudget(root, "compile_50_step_plan");
            int iterations = budget["iterations"]!.Value<int>();
            var registry = new ToolRegistry();
            registry.Register(new Descriptor(), new Executor());
            var compiler = new DefaultPlanCompiler(
                registry,
                new DomainPackRegistry(),
                new SpatialAgent.Core.DefaultInvocationEffectResolver());
            var request = new PlanningRequest
            {
                Goal = "copy 50 layers",
                ToolCalls = Enumerable.Range(0, 50)
                    .Select(index => new ToolCall
                    {
                        Id = $"call-{index}",
                        Name = "analysis_Copy",
                        Arguments = new()
                        {
                            ["in_features"] = $"Layer_{index}",
                            ["out_feature_class"] = $"Output_{index}"
                        }
                    })
                    .ToList()
            };

            GC.Collect();
            long before = GC.GetTotalMemory(forceFullCollection: true);
            var stopwatch = Stopwatch.StartNew();
            AgentPlan? plan = null;
            for (int index = 0; index < iterations; index++)
                plan = compiler.Compile(request);
            stopwatch.Stop();
            long memoryDelta = Math.Max(
                0,
                GC.GetTotalMemory(forceFullCollection: true) - before);
            WriteMetric("compile_50_step_plan", stopwatch.ElapsedMilliseconds, memoryDelta);

            plan!.Steps.Should().HaveCount(50);
            stopwatch.ElapsedMilliseconds.Should().BeLessThan(
                budget["maximum_elapsed_ms"]!.Value<long>());
            BytesToMegabytes(memoryDelta).Should().BeLessThan(
                budget["maximum_managed_memory_delta_mb"]!.Value<double>());
        }

        [Fact]
        [Trait("Category", "Performance")]
        public void Hundred_turn_checkpoint_serialization_stays_bounded()
        {
            string root = FindRepoRoot();
            JObject budget = LoadBudget(root, "checkpoint_serialization");
            int turnCount = budget["turn_count"]!.Value<int>();
            var state = new AgentRunState
            {
                ThreadId = "performance-thread",
                Turn = turnCount,
                Status = SessionStatus.Completed,
                Messages = Enumerable.Range(0, turnCount)
                    .SelectMany(index => new[]
                    {
                        new Message
                        {
                            Role = "user",
                            Content = $"Turn {index}: inspect layer and report its schema."
                        },
                        new Message
                        {
                            Role = "assistant",
                            Content = new string('x', 1024)
                        }
                    })
                    .ToList(),
                StepResults = Enumerable.Range(0, turnCount)
                    .Select(index => new StepExecutionRecord
                    {
                        StepId = index + 1,
                        ToolName = "agent_describe_layer",
                        Status = StepResultStatus.Completed,
                        ElapsedMs = 10
                    })
                    .ToList()
            };

            var stopwatch = Stopwatch.StartNew();
            string json = JsonConvert.SerializeObject(state);
            var restored = JsonConvert.DeserializeObject<AgentRunState>(json);
            stopwatch.Stop();
            int bytes = Encoding.UTF8.GetByteCount(json);
            _output.WriteLine(
                $"checkpoint_serialization: elapsed_ms={stopwatch.ElapsedMilliseconds}, json_bytes={bytes}");

            restored!.Messages.Should().HaveCount(turnCount * 2);
            stopwatch.ElapsedMilliseconds.Should().BeLessThan(
                budget["maximum_elapsed_ms"]!.Value<long>());
            bytes.Should().BeLessThan(
                budget["maximum_json_bytes"]!.Value<int>());
        }

        private void WriteMetric(string name, long elapsedMs, long memoryBytes) =>
            _output.WriteLine(
                $"{name}: elapsed_ms={elapsedMs}, managed_memory_delta_mb={BytesToMegabytes(memoryBytes):F2}");

        private static double BytesToMegabytes(long bytes) =>
            bytes / 1024d / 1024d;

        private static JObject LoadBudget(string root, string name)
        {
            var baseline = JObject.Parse(
                File.ReadAllText(Path.Combine(root, "performance_baseline.json")));
            return (JObject)baseline[name]!;
        }

        private static string FindRepoRoot()
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                if (File.Exists(Path.Combine(current.FullName, "performance_baseline.json")))
                    return current.FullName;
                current = current.Parent;
            }
            throw new DirectoryNotFoundException(
                "Could not locate performance_baseline.json.");
        }
    }
}
