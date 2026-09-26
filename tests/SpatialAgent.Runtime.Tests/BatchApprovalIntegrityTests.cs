using FluentAssertions;
using NSubstitute;
using SpatialAgent.Runtime;
using SpatialAgent.Runtime.Contracts;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    public sealed class BatchApprovalIntegrityTests
    {
        private sealed class Descriptor : IToolDescriptor
        {
            public string Name { get; init; } = "";
            public string Description => Name;
            public string Category => "management";
            public ToolAnnotation Annotation => ToolAnnotation.Destructive;
            public IReadOnlyDictionary<string, ParameterProperty> Parameters =>
                new Dictionary<string, ParameterProperty>();
            public IReadOnlyList<string> RequiredParameters => Array.Empty<string>();
            public VerificationRule? Verification => null;
            public IReadOnlyDictionary<string, string>? DefaultParameters => null;
        }

        private sealed class CountingExecutor : IToolExecutor
        {
            public int Count { get; private set; }
            public Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken ct = default)
            {
                Count++;
                return Task.FromResult(new ToolResult { ToolName = call.Name, Success = true });
            }
        }

        [Fact]
        public async Task Exact_batch_ticket_covers_every_step_and_executes_each_once()
        {
            var registry = new ToolRegistry();
            var executor = new CountingExecutor();
            registry.Register(new Descriptor { Name = "management_DeleteRows" }, executor);
            var safety = Substitute.For<ISafetyGate>();
            safety.Classify("management_DeleteRows").Returns(ToolRiskLevel.Destructive);
            safety.Evaluate("management_DeleteRows", Arg.Any<AgentPermissionLevel>())
                .Returns(new SafetyDecision
                {
                    Kind = SafetyDecisionKind.RequireApproval,
                    RiskLevel = ToolRiskLevel.Destructive,
                    Reason = "destructive batch"
                });
            var workspace = Substitute.For<IWorkspaceService>();
            workspace.BackupAsync(Arg.Any<string>(), Arg.Any<string>())
                .Returns(call => $"backup-{call.ArgAt<string>(0)}");
            var scheduler = new Scheduler(registry, safety: safety);
            scheduler.SetWorkspace(workspace);
            scheduler.CurrentPermissionLevel = AgentPermissionLevel.RequestApproval;
            var plan = new AgentPlan
            {
                Steps = new()
                {
                    Step(1, "LayerA"),
                    Step(2, "LayerB")
                }
            };

            var paused = await scheduler.RunAsync(plan);

            paused.Should().HaveCount(2);
            paused.Should().OnlyContain(result => result.Status == StepResultStatus.AwaitingApproval);
            paused.Select(result => result.ApprovalTicket!.Id).Distinct().Should().ContainSingle();
            var ticket = paused[0].ApprovalTicket!;
            ticket.Scope.Should().Be(ApprovalScope.ExactBatch);
            ticket.CoveredStepIds.Should().BeEquivalentTo(new[] { 1, 2 });
            ticket.CoveredCallSignatures.Should().HaveCount(2);
            ticket.CoveredCalls.Should().HaveCount(2);
            ticket.CoveredCalls.Select(item => item.Target.Dataset)
                .Should().BeEquivalentTo(new[] { "LayerA", "LayerB" });
            executor.Count.Should().Be(0);

            foreach (var step in plan.Steps)
            {
                step.Status = PlanStepStatus.AwaitingApproval;
                step.PendingApproval = ticket;
            }
            scheduler.IsApprovalResumption = true;
            scheduler.ApprovedTicketIds = new HashSet<string> { ticket.Id };

            var completed = await scheduler.RunAsync(plan);

            completed.Should().HaveCount(2);
            completed.Should().OnlyContain(result =>
                result.Status == StepResultStatus.Completed && result.UserApproved == true);
            executor.Count.Should().Be(2);
        }

        [Fact]
        public async Task Mutating_one_call_after_batch_approval_invalidates_ticket()
        {
            var registry = new ToolRegistry();
            var executor = new CountingExecutor();
            registry.Register(new Descriptor { Name = "management_DeleteRows" }, executor);
            var safety = Substitute.For<ISafetyGate>();
            safety.Classify(Arg.Any<string>()).Returns(ToolRiskLevel.Destructive);
            safety.Evaluate(Arg.Any<string>(), Arg.Any<AgentPermissionLevel>())
                .Returns(new SafetyDecision
                {
                    Kind = SafetyDecisionKind.RequireApproval,
                    RiskLevel = ToolRiskLevel.Destructive
                });
            var scheduler = new Scheduler(registry, safety: safety)
            {
                CurrentPermissionLevel = AgentPermissionLevel.RequestApproval
            };
            var plan = new AgentPlan { Steps = new() { Step(1, "A"), Step(2, "B") } };
            var paused = await scheduler.RunAsync(plan);
            var ticket = paused[0].ApprovalTicket!;
            foreach (var step in plan.Steps)
            {
                step.Status = PlanStepStatus.AwaitingApproval;
                step.PendingApproval = ticket;
            }
            plan.Steps[1].ToolCall!.Arguments["in_table"] = "CHANGED";
            scheduler.IsApprovalResumption = true;
            scheduler.ApprovedTicketIds = new HashSet<string> { ticket.Id };

            var resumed = await scheduler.RunAsync(plan);

            resumed.Should().BeEmpty();
            executor.Count.Should().Be(0);
        }

        private static PlanStep Step(int id, string layer) => new()
        {
            Id = id,
            ToolCall = new ToolCall
            {
                Id = $"call-{id}",
                Name = "management_DeleteRows",
                Arguments = new() { ["in_table"] = layer }
            }
        };
    }
}
