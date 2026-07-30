namespace WechatRobot.Application.Agents;

public enum AgentCapability
{
    Chat,
    FunctionTools,
    ToolResultLoop,
    JsonObject,
    JsonSchema
}

public sealed record AgentCapabilityReport(
    Guid ModelConfigurationId,
    int ModelConfigurationVersion,
    IReadOnlySet<AgentCapability> Supported,
    string? FailureCode,
    DateTime TestedAtUtc);
