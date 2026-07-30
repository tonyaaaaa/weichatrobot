using WechatRobot.Application.Conversations;

namespace WechatRobot.Application.Agents;

public interface IAnswerAgent
{
    Task<GroundedAnswerResult> AnswerAsync(
        GroundedAnswerRequest request,
        CancellationToken cancellationToken);
}
