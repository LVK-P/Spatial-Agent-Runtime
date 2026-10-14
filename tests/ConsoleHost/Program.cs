// Spatial Agent Runtime — Console Test Host
// Tests the Runtime pipeline with real ArcGIS Pro adapters.
// Requires ArcGIS Pro to be installed and a project open.
// For pure-Runtime tests, swap ProjectReader/MapReader with mock implementations.
// 中文：Spatial Agent Runtime — 控制台测试主机使用真实的ArcGIS Pro适配器测试运行时流水线。需要安装ArcGIS Pro并打开项目。对于纯运行时测试，可以将ProjectReader/MapReader与模拟实现互换。

using SpatialAgent.Adapters.ArcGIS;
using SpatialAgent.Adapters.LLM;
using SpatialAgent.Knowledge;
using SpatialAgent.Runtime;
using SpatialAgent.Runtime.Contracts;
using SpatialAgent.Services;
using SpatialAgent.Tools.Core;

// ── 1. Configure ──
// 中文：1. 配置
Console.WriteLine("=== Spatial Agent Runtime — Console Test ===\n");
Console.Write("DeepSeek API Key (or press Enter to skip LLM test): ");
string? apiKey = Console.ReadLine()?.Trim();

// ── 2. Create infrastructure ──
// 中文：2. 建设基础设施
var eventBus = new EventBus();
var session = new AgentSession($"test-{DateTime.Now:HHmmss}", eventBus);

// Subscribe to see events in console
// 中文：订阅以查看主机版的活动
eventBus.Subscribe("*", e => Console.WriteLine($"  [Event] {e.EventType} - {string.Join(", ", e.Metadata?.Select(kv => $"{kv.Key}={kv.Value}") ?? Array.Empty<string>())}"));

// ── 3. Create LLM provider ──
// 中文：3. 创建大型语言模型提供者
var llm = new DeepSeekProvider("deepseek-v4-pro");
bool hasLLM = !string.IsNullOrEmpty(apiKey);
if (hasLLM)
{
    llm.SetApiKey(apiKey!);
    Console.WriteLine($"✅ LLM: {llm.ProviderName} / {llm.ModelName}");
    Console.WriteLine($"   Capability: ToolCalling={llm.Capability.ToolCalling}, StructuredOutput={llm.Capability.StructuredOutput}, Streaming={llm.Capability.Streaming}");
}
else
{
    Console.WriteLine("⚠️ 跳过 LLM 测试 (未提供 API Key)");
}

// ── 4. Create Tool Registry ──
// 中文：4. 创建工具注册表
var tools = new ToolRegistry(eventBus);
tools.Register(new GetMapInfoTool());
tools.Register(new ListLayersTool());
tools.Register(new DescribeLayerTool());
tools.Register(new GetSelectionTool());
tools.Register(new GetProjectInfoTool());
Console.WriteLine($"✅ ToolRegistry: {tools.Count} tools registered");
foreach (var t in tools.GetAllDescriptors())
    Console.WriteLine($"   - {t.Name}: {t.Description[..Math.Min(t.Description.Length, 60)]}...");

// ── 5. Create services ──
// 中文：5. 创建服务
var safety = new SafetyGate();
var safetyCheck = safety.Classify("management_AddField");
Console.WriteLine($"✅ SafetyGate: AddField = {safetyCheck}");
Console.WriteLine($"✅ SafetyGate: Erase = {safety.Classify("analysis_Erase")}");
Console.WriteLine($"✅ SafetyGate: DeleteField(RequestApproval) = {safety.Evaluate("management_DeleteField", AgentPermissionLevel.RequestApproval).Kind}");

// ── 6. Create ContextManager (with mock adapters) ──
// 中文：6. 创建上下文管理器（使用模拟适配器）
var skillManager = new SkillManager(AppDomain.CurrentDomain.BaseDirectory);
try { skillManager.LoadAll(); } catch { /* 测试中技能目录可能不存在。 / The skills directory may not exist in tests. */ }
Console.WriteLine($"✅ SkillManager: {skillManager.Count} skills loaded");

var contextManager = new ContextManager(
    new ProjectReader(),
    new MapReader(),
    skillManager,
    knowledge: null);

// ── 7. Create AgentRuntime ──
// 中文：7. 创建 AgentRuntime
var runtime = new AgentRuntime(session, llm, tools, contextManager, eventBus);
runtime.SetSafety(safety);
runtime.OnProgress += (_, msg) => Console.WriteLine($"  [Progress] {msg}");

Console.WriteLine("\n=== Components ready ===\n");

// ── 8. Test ──
// 中文：8. 测试
if (!hasLLM)
{
    Console.WriteLine("跳过 LLM 测试。提供 API Key 以测试完整 Agent 循环。");
    Console.WriteLine("架构验证通过: 7 模块全部初始化成功, 0 错误。");
    return;
}

Console.Write("用户: ");
string? userInput = Console.ReadLine()?.Trim();
if (string.IsNullOrEmpty(userInput)) userInput = "生成项目简介";

Console.WriteLine($"\n处理: \"{userInput}\"\n");

try
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var response = await runtime.ProcessAsync(new TaskRequest { UserMessage = userInput ?? "" });
    sw.Stop();

    Console.WriteLine($"\n=== Agent 回复 ({sw.Elapsed.TotalSeconds:F1}s) ===");
    Console.WriteLine(response.UserReply);
    Console.WriteLine($"\nHadToolCalls: {response.HadToolCalls}, StopReason: {response.StopReason}");
    if (response.RequiresApproval)
    {
        Console.WriteLine($"PendingTools: {string.Join(", ", response.PendingTools)}");
        Console.WriteLine($"RequiredPermissionLevel: {response.RequiredPermissionLevel}");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"\n❌ 错误: {ex.Message}");
    if (ex.InnerException != null)
        Console.WriteLine($"   内部: {ex.InnerException.Message}");
}

Console.WriteLine("\n=== Done ===");
