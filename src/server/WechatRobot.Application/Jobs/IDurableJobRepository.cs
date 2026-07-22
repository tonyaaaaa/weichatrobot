namespace WechatRobot.Application.Jobs;

public interface IDurableJobRepository
{
    Task<InboundMessageIngestResult> IngestInboundMessageAsync(InboundMessageIngestRequest request, CancellationToken cancellationToken);
    Task<LeasedDurableJob?> LeaseNextJobAsync(string jobType, string leaseOwner, DateTime nowUtc, TimeSpan leaseDuration, CancellationToken cancellationToken);
    Task CompleteJobAsync(Guid jobId, string leaseOwner, DateTime completedAtUtc, CancellationToken cancellationToken);
    Task FailJobAsync(LeasedDurableJob job, string reason, DateTime failedAtUtc, CancellationToken cancellationToken);
    Task<EnqueueSendCommandResult> EnqueueSendCommandAsync(EnqueueSendCommandRequest request, CancellationToken cancellationToken);
    Task<LeasedSendCommand?> LeaseNextSendCommandAsync(string leaseOwner, DateTime nowUtc, TimeSpan leaseDuration, CancellationToken cancellationToken);
    Task CompleteSendCommandAsync(LeasedSendCommand command, DateTime completedAtUtc, CancellationToken cancellationToken);
    Task FailSendCommandAsync(LeasedSendCommand command, string reason, DateTime failedAtUtc, TimeSpan? retryDelay, CancellationToken cancellationToken);
    Task<bool> RenewSendLeasesAsync(LeasedSendCommand command, DateTime nowUtc, TimeSpan leaseDuration, CancellationToken cancellationToken);
}

public sealed record InboundMessageIngestRequest(
    Guid RobotConfigId,
    string WorkToolMessageId,
    string FallbackHash,
    DateTime FallbackWindowStartUtc,
    string GroupName,
    string SenderDisplayName,
    string Text,
    DateTime ReceivedAtUtc,
    string? StableSenderId = null);

public enum InboundMessageIngestResult
{
    Accepted,
    Duplicate
}

public sealed record LeasedDurableJob(Guid Id, string JobType, string PayloadJson, int AttemptCount, string LeaseOwner);

public sealed record EnqueueSendCommandRequest(Guid RobotConfigId, string WorkToolRobotId, string GroupName, string Text, string IdempotencyKey);

public enum EnqueueSendCommandResult
{
    Enqueued,
    AlreadyExists
}

public sealed record LeasedSendCommand(
    Guid Id,
    Guid RobotConfigId,
    string WorkToolRobotId,
    string GroupName,
    string Text,
    string IdempotencyKey,
    int SendRateLimitPerMinute,
    int AttemptCount,
    string LeaseOwner);
