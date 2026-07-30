namespace WechatRobot.Application.Agents;

public sealed record AgentModelConfigurationSnapshot(
    Guid Id,
    int Version);

public interface IAgentModelConfigurationReader
{
    Task<AgentModelConfigurationSnapshot> GetAsync(
        Guid modelConfigurationId,
        CancellationToken cancellationToken = default);
}

public interface IAgentCapabilityProbe
{
    Task<AgentCapabilityReport> ProbeAsync(
        Guid modelConfigurationId,
        CancellationToken cancellationToken = default);
}
