namespace WechatRobot.Application.Jobs;

public interface IDurableJobRepository
{
    Task<InboundMessageIngestResult> IngestInboundMessageAsync(InboundMessageIngestRequest request, CancellationToken cancellationToken);
    Task<LeasedDurableJob?> LeaseNextJobAsync(string jobType, string leaseOwner, DateTime nowUtc, TimeSpan leaseDuration, CancellationToken cancellationToken);
    Task CompleteJobAsync(Guid jobId, string leaseOwner, DateTime completedAtUtc, CancellationToken cancellationToken);
    Task FailJobAsync(LeasedDurableJob job, string reason, DateTime failedAtUtc, CancellationToken cancellationToken);
    Task DeferJobAsync(
        LeasedDurableJob job,
        string reason,
        DateTime deferredAtUtc,
        TimeSpan retryDelay,
        CancellationToken cancellationToken) =>
        FailJobAsync(job, reason, deferredAtUtc, cancellationToken);
    Task<bool> RenewJobLeaseAsync(
        LeasedDurableJob job,
        DateTime nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken) => Task.FromResult(true);
    Task<EnqueueSendCommandResult> EnqueueSendCommandAsync(EnqueueSendCommandRequest request, CancellationToken cancellationToken);
    Task<LeasedSendCommand?> LeaseNextSendCommandAsync(string leaseOwner, DateTime nowUtc, TimeSpan leaseDuration, CancellationToken cancellationToken);
    Task<bool> EnsureSendEnabledAsync(LeasedSendCommand command, CancellationToken cancellationToken) => Task.FromResult(true);
    Task<bool> MarkSendDispatchingAsync(LeasedSendCommand command, DateTime dispatchedAtUtc, CancellationToken cancellationToken);
    Task MarkSendDeliveryUnknownAsync(LeasedSendCommand command, string reason, DateTime failedAtUtc, CancellationToken cancellationToken);
    Task MarkSendRejectedAsync(LeasedSendCommand command, string reason, DateTime rejectedAtUtc, CancellationToken cancellationToken);
    Task MarkSendAcceptedAsync(LeasedSendCommand command, string workToolMessageId, DateTime acceptedAtUtc, CancellationToken cancellationToken);
    Task ReleaseSendGateAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    Task FailSendCommandAsync(LeasedSendCommand command, string reason, DateTime failedAtUtc, TimeSpan? retryDelay, CancellationToken cancellationToken);
    Task<bool> RenewSendLeasesAsync(LeasedSendCommand command, DateTime nowUtc, TimeSpan leaseDuration, CancellationToken cancellationToken);
}

public sealed record InboundMessageIngestRequest(
    Guid RobotConfigId,
    string WorkToolMessageId,
    string FallbackHash,
    DateTime FallbackWindowStartUtc,
    string GroupName,
    string? GroupRemark,
    string SenderDisplayName,
    string Text,
    DateTime ReceivedAtUtc,
    string? StableSenderId = null,
    bool WasMentioned = false,
    string ChannelType = "Group",
    int? RoomType = 1,
    string? PeerDisplayName = null,
    string? ScopeHash = null);

public enum InboundMessageIngestResult
{
    Accepted,
    Duplicate
}

public sealed record LeasedDurableJob(
    Guid Id,
    string JobType,
    string PayloadJson,
    int AttemptCount,
    string LeaseOwner,
    DateTime? CreatedAtUtc = null);

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
    string LeaseOwner,
    IReadOnlyList<string>? AtList = null);
