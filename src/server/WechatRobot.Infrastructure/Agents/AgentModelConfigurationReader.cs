using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Agents;
using WechatRobot.Infrastructure.Persistence;

namespace WechatRobot.Infrastructure.Agents;

public sealed class AgentModelConfigurationReader(WechatRobotDbContext database)
    : IAgentModelConfigurationReader
{
    public async Task<AgentModelConfigurationSnapshot> GetAsync(
        Guid modelConfigurationId,
        CancellationToken cancellationToken = default)
    {
        var model = await database.ModelConfigs
            .AsNoTracking()
            .Where(item =>
                item.Id == modelConfigurationId
                && item.ConfigurationType == "chat")
            .Select(item => new AgentModelConfigurationSnapshot(item.Id, item.Version))
            .SingleOrDefaultAsync(cancellationToken);
        return model ?? throw new KeyNotFoundException(
            "Chat model configuration was not found.");
    }
}
