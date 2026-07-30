using WechatRobot.Application.Jobs;

namespace WechatRobot.Application.PrivateChat;

public interface IPrivateKnowledgeIngestProcessor
{
    Task ProcessAsync(LeasedDurableJob job, CancellationToken cancellationToken);
}
