using WechatRobot.Application.Jobs;

namespace WechatRobot.Application.PrivateChat;

public interface IPrivateChatProcessor
{
    Task ProcessAsync(
        LeasedDurableJob job,
        CancellationToken cancellationToken);
}
