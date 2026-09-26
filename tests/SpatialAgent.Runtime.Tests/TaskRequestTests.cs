using FluentAssertions;
using SpatialAgent.Runtime.Contracts;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    /// <summary>
    /// TaskRequest contract tests — verifies safety-critical defaults (ADR 0051).
    /// </summary>
    public class TaskRequestTests
    {
        [Fact]
        public void Approved_defaults_to_false()
        {
            // P0 safety property: fail-closed. If caller forgets to set Approved
            // on an approval resumption, the default must be false (reject).
            new TaskRequest().Approved.Should().BeFalse();
        }

        [Fact]
        public void IsApprovalResumption_defaults_to_false()
        {
            new TaskRequest().IsApprovalResumption.Should().BeFalse();
        }

        [Fact]
        public void AppendUserMessage_defaults_to_true()
        {
            // Normal messages should append user bubble by default.
            new TaskRequest().AppendUserMessage.Should().BeTrue();
        }
    }
}
