using WechatRobot.Application.Jobs;

namespace WechatRobot.Application.Messaging;

public sealed class SendCommandService(IDurableJobRepository durableJobs)
{
    public const int MaximumAttempts = 4;

    public static TimeSpan? GetRetryDelay(int failedAttempt) => failedAttempt switch
    {
        1 => TimeSpan.FromSeconds(5),
        2 => TimeSpan.FromSeconds(15),
        3 => TimeSpan.FromSeconds(45),
        _ => null
    };

    public Task<EnqueueSendCommandResult> EnqueueFixedReplyAsync(
        Guid robotConfigId,
        string workToolRobotId,
        string groupName,
        string text,
        Guid messageId,
        CancellationToken cancellationToken) => durableJobs.EnqueueSendCommandAsync(
            new EnqueueSendCommandRequest(robotConfigId, workToolRobotId, groupName, text, $"fixed-reply:{messageId:D}"), cancellationToken);
}
