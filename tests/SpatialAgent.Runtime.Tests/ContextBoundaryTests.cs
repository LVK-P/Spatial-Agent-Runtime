using FluentAssertions;
using SpatialAgent.Runtime;
using SpatialAgent.Runtime.Contracts;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    public class ContextBoundaryTests
    {
        [Fact]
        public void SystemPrompt_does_not_inline_dynamic_memory_sections()
        {
            var prompt = SystemPrompt.Build(new AgentContext
            {
                IsNewProject = true,
                MemorySummary = "should-not-appear",
                W5H2Context = "should-not-appear"
            });

            prompt.Should().Contain("first session");
            prompt.Should().NotContain("should-not-appear");
            prompt.Should().NotContain("项目记忆");
            prompt.Should().NotContain("项目认知");
        }

        [Fact]
        public void AgentSession_reset_conversation_clears_runtime_state()
        {
            var session = new AgentSession("thread-old");
            session.AddMessage(new Message { Role = "user", Content = "hello" });
            session.AddObservation(new Observation { ToolName = "analysis_Buffer" });
            session.AddValidation(new ValidationResult { RuleName = "verify_exists", Verdict = ValidationVerdict.FAIL });
            session.Plan = new AgentPlan { Goal = "old" };
            session.SetStatus(SessionStatus.Running);

            session.ResetConversation("thread-new");

            session.ThreadId.Should().Be("thread-new");
            session.Messages.Should().BeEmpty();
            session.Observations.Should().BeEmpty();
            session.Validations.Should().BeEmpty();
            session.Plan.Should().BeNull();
            session.Status.Should().Be(SessionStatus.Idle);
        }
    }
}
