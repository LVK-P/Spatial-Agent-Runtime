using FluentAssertions;
using SpatialAgent.Runtime.Contracts;
using Xunit;

namespace SpatialAgent.Runtime.Tests
{
    /// <summary>
    /// TaskRequest contract tests — verifies safety-critical defaults (ADR 0051).
    /// </summary>
    /// <remarks>中文：TaskRequest 合同测试——验证安全关键的默认设置（ADR 0051）。</remarks>
    public class TaskRequestTests
    {
        [Fact]
        public void Approved_defaults_to_false()
        {
            // P0 safety property: fail-closed. If caller forgets to set Approved
            // on an approval resumption, the default must be false (reject).
            // 中文：P0 安全属性：失败关闭。如果调用方在批准恢复时忘记设置“已批准”，默认值必须为 false（拒绝）。
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
            // 中文：普通消息默认应该会加上用户气泡。
            new TaskRequest().AppendUserMessage.Should().BeTrue();
        }
    }
}
