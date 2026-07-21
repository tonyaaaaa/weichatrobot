namespace WechatRobot.Application.Jobs;

public interface IDurableJobRepository
{
    Task<InboundMessageIngestResult> IngestInboundMessageAsync(InboundMessageIngestRequest request, CancellationToken cancellationToken);
}

public sealed record InboundMessageIngestRequest(
    Guid RobotConfigId,
    string WorkToolMessageId,
    string FallbackHash,
    DateTime FallbackWindowStartUtc,
    string GroupName,
    string SenderName,
    string Text,
    DateTime ReceivedAtUtc);

public enum InboundMessageIngestResult
{
    Accepted,
    Duplicate
}
