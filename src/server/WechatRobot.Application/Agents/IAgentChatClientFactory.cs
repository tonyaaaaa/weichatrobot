using Microsoft.Extensions.AI;

namespace WechatRobot.Application.Agents;

public interface IAgentChatClientFactory
{
    Task<IChatClient> CreateAsync(
        Guid modelConfigurationId,
        CancellationToken cancellationToken = default);
}
