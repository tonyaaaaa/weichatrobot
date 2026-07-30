using WechatRobot.Application.Agents;

namespace WechatRobot.UnitTests.Agents;

public sealed class AgentRuntimeContractTests
{
    [Fact]
    public void Chat_capability_does_not_imply_function_tool_support()
    {
        var report = new AgentCapabilityReport(
            Guid.NewGuid(),
            3,
            new HashSet<AgentCapability> { AgentCapability.Chat },
            null,
            DateTime.UtcNow);

        Assert.Contains(AgentCapability.Chat, report.Supported);
        Assert.DoesNotContain(AgentCapability.FunctionTools, report.Supported);
        Assert.DoesNotContain(AgentCapability.ToolResultLoop, report.Supported);
    }

    [Fact]
    public void Capability_report_keeps_model_version_and_stable_failure_code()
    {
        var modelId = Guid.NewGuid();
        var testedAt = new DateTime(2026, 7, 29, 0, 0, 0, DateTimeKind.Utc);

        var report = new AgentCapabilityReport(
            modelId,
            8,
            new HashSet<AgentCapability>(),
            "agent_probe_invalid_output",
            testedAt);

        Assert.Equal(modelId, report.ModelConfigurationId);
        Assert.Equal(8, report.ModelConfigurationVersion);
        Assert.Equal("agent_probe_invalid_output", report.FailureCode);
        Assert.Equal(testedAt, report.TestedAtUtc);
    }
}
