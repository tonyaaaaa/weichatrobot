using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;
using WechatRobot.Application.Jobs;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.Infrastructure.Persistence;

public sealed class DurableJobRepository(WechatRobotDbContext database) : IDurableJobRepository
{
    public async Task<InboundMessageIngestResult> IngestInboundMessageAsync(InboundMessageIngestRequest request, CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var robot = await database.RobotConfigs.SingleAsync(config => config.Id == request.RobotConfigId, cancellationToken);
        var message = new ConversationMessageEntity
        {
            RobotConfigId = request.RobotConfigId,
            WorkToolMessageId = string.IsNullOrWhiteSpace(request.WorkToolMessageId) ? null : request.WorkToolMessageId,
            FallbackHash = request.FallbackHash,
            FallbackWindowStartUtc = request.FallbackWindowStartUtc,
            GroupName = request.GroupName,
            SenderDisplayName = request.SenderDisplayName,
            StableSenderId = request.StableSenderId,
            ProcessingState = "pending",
            Text = request.Text,
            ReceivedAtUtc = request.ReceivedAtUtc
        };
        database.ConversationMessages.Add(message);
        database.DurableJobs.Add(new DurableJobEntity
        {
            JobType = "ProcessInboundMessage",
            RelatedConversationMessageId = message.Id,
            PayloadJson = JsonSerializer.Serialize(new
            {
                messageId = message.Id,
                request.RobotConfigId,
                workToolRobotId = robot.WorkToolRobotId,
                request.GroupName,
                request.SenderDisplayName,
                request.StableSenderId,
                request.Text,
                request.ReceivedAtUtc
            })
        });

        try
        {
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return InboundMessageIngestResult.Accepted;
        }
        catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return InboundMessageIngestResult.Duplicate;
        }
        catch (OperationCanceledException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<LeasedDurableJob?> LeaseNextJobAsync(string jobType, string leaseOwner, DateTime nowUtc, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        var candidate = await database.DurableJobs.AsNoTracking()
            .Where(job =>
                job.JobType == jobType && ((job.Status == "pending" || job.Status == "retrying") && job.NextAttemptAtUtc <= nowUtc ||
                job.Status == "leased" && job.LeaseExpiresAtUtc <= nowUtc))
            .OrderBy(job => job.NextAttemptAtUtc)
            .ThenBy(job => job.CreatedAtUtc)
            .Select(job => new { job.Id, job.Version })
            .FirstOrDefaultAsync(cancellationToken);
        if (candidate is null)
        {
            return null;
        }

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var leaseExpiry = nowUtc.Add(leaseDuration);
        var updated = await database.DurableJobs
            .Where(job => job.Id == candidate.Id && job.JobType == jobType && job.Version == candidate.Version && (
                (job.Status == "pending" || job.Status == "retrying") && job.NextAttemptAtUtc <= nowUtc ||
                job.Status == "leased" && job.LeaseExpiresAtUtc <= nowUtc))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Status, "leased")
                .SetProperty(job => job.LeaseOwner, leaseOwner)
                .SetProperty(job => job.LeaseExpiresAtUtc, leaseExpiry)
                .SetProperty(job => job.Version, job => job.Version + 1)
                .SetProperty(job => job.UpdatedAtUtc, nowUtc), cancellationToken);
        if (updated != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        await UpdateRelatedMessageStateAsync(candidate.Id, "leased", cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var leased = await database.DurableJobs.AsNoTracking().SingleAsync(job => job.Id == candidate.Id, cancellationToken);
        return new LeasedDurableJob(leased.Id, leased.JobType, leased.PayloadJson, leased.AttemptCount, leaseOwner);
    }

    public async Task CompleteJobAsync(Guid jobId, string leaseOwner, DateTime completedAtUtc, CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var updated = await database.DurableJobs.Where(job => job.Id == jobId && job.Status == "leased" && job.LeaseOwner == leaseOwner)
        .ExecuteUpdateAsync(setters => setters
            .SetProperty(job => job.Status, "completed")
            .SetProperty(job => job.CompletedAtUtc, completedAtUtc)
            .SetProperty(job => job.LeaseOwner, (string?)null)
            .SetProperty(job => job.LeaseExpiresAtUtc, (DateTime?)null)
            .SetProperty(job => job.Version, job => job.Version + 1)
            .SetProperty(job => job.UpdatedAtUtc, completedAtUtc), cancellationToken);
        if (updated == 1) await UpdateRelatedMessageStateAsync(jobId, "completed", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task FailJobAsync(LeasedDurableJob job, string reason, DateTime failedAtUtc, CancellationToken cancellationToken)
    {
        var attempts = job.AttemptCount + 1;
        if (attempts >= 4)
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            var updated = await database.DurableJobs
                .Where(value => value.Id == job.Id && value.Status == "leased" && value.LeaseOwner == job.LeaseOwner)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(value => value.Status, "deadLetter")
                    .SetProperty(value => value.AttemptCount, attempts)
                    .SetProperty(value => value.LeaseOwner, (string?)null)
                    .SetProperty(value => value.LeaseExpiresAtUtc, (DateTime?)null)
                    .SetProperty(value => value.Version, value => value.Version + 1)
                    .SetProperty(value => value.UpdatedAtUtc, failedAtUtc), cancellationToken);
            if (updated == 1)
            {
                await UpdateRelatedMessageStateAsync(job.Id, "deadLetter", cancellationToken);
                database.DeadLetters.Add(new DeadLetterEntity { DurableJobId = job.Id, Reason = reason, PayloadJson = job.PayloadJson, CreatedAtUtc = failedAtUtc });
                await database.SaveChangesAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        var delay = SendRetryDelay(attempts);
        await using var retryTransaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var retried = await database.DurableJobs
            .Where(value => value.Id == job.Id && value.Status == "leased" && value.LeaseOwner == job.LeaseOwner)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.Status, "retrying")
                .SetProperty(value => value.AttemptCount, attempts)
                .SetProperty(value => value.NextAttemptAtUtc, failedAtUtc.Add(delay))
                .SetProperty(value => value.LeaseOwner, (string?)null)
                .SetProperty(value => value.LeaseExpiresAtUtc, (DateTime?)null)
                .SetProperty(value => value.Version, value => value.Version + 1)
                .SetProperty(value => value.UpdatedAtUtc, failedAtUtc), cancellationToken);
        if (retried == 1)
        {
            await UpdateRelatedMessageStateAsync(job.Id, "retrying", cancellationToken);
            await retryTransaction.CommitAsync(cancellationToken);
        }
        else await retryTransaction.RollbackAsync(cancellationToken);
    }

    public async Task<EnqueueSendCommandResult> EnqueueSendCommandAsync(EnqueueSendCommandRequest request, CancellationToken cancellationToken)
    {
        database.SendCommands.Add(new SendCommandEntity
        {
            RobotConfigId = request.RobotConfigId,
            IdempotencyKey = request.IdempotencyKey,
            PayloadJson = JsonSerializer.Serialize(new { request.WorkToolRobotId, request.GroupName, request.Text }),
            NextAttemptAtUtc = DateTime.UtcNow
        });
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            return EnqueueSendCommandResult.Enqueued;
        }
        catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
        {
            database.ChangeTracker.Clear();
            return EnqueueSendCommandResult.AlreadyExists;
        }
    }

    public async Task<LeasedSendCommand?> LeaseNextSendCommandAsync(string leaseOwner, DateTime nowUtc, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        var candidate = await (from command in database.SendCommands.AsNoTracking()
                               join robot in database.RobotConfigs.AsNoTracking() on command.RobotConfigId equals robot.Id
                               where (((command.Status == "pending" || command.Status == "retrying") && command.NextAttemptAtUtc <= nowUtc) ||
                                      (command.Status == "leased" && command.LeaseExpiresAtUtc <= nowUtc)) &&
                                     (robot.SendLeaseOwner == null || robot.SendLeaseExpiresAtUtc <= nowUtc) &&
                                     !database.SendCommands.Any(earlier =>
                                         earlier.RobotConfigId == command.RobotConfigId &&
                                         earlier.Status != "completed" &&
                                         earlier.Status != "deadLetter" &&
                                         (earlier.CreatedAtUtc < command.CreatedAtUtc ||
                                          (earlier.CreatedAtUtc == command.CreatedAtUtc && earlier.Id.CompareTo(command.Id) < 0)))
                               orderby command.NextAttemptAtUtc, command.CreatedAtUtc, command.Id
                               select new { command.Id, command.RobotConfigId, command.Version })
            .FirstOrDefaultAsync(cancellationToken);
        if (candidate is null)
        {
            return null;
        }

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var robotState = await database.RobotConfigs.AsNoTracking().SingleAsync(value => value.Id == candidate.RobotConfigId, cancellationToken);
        var capacity = (decimal)robotState.SendRateLimitPerMinute;
        var elapsedSeconds = Math.Max(0, (nowUtc - robotState.SendRateUpdatedAtUtc).TotalSeconds);
        var availableTokens = Math.Min(capacity, robotState.SendRateTokens + (decimal)(elapsedSeconds * robotState.SendRateLimitPerMinute / 60d));
        if (availableTokens < 1)
        {
            return null;
        }

        var robotUpdated = await database.RobotConfigs
            .Where(value => value.Id == robotState.Id && value.SendCoordinationVersion == robotState.SendCoordinationVersion &&
                (value.SendLeaseOwner == null || value.SendLeaseExpiresAtUtc <= nowUtc))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.SendRateTokens, availableTokens - 1)
                .SetProperty(value => value.SendRateUpdatedAtUtc, nowUtc)
                .SetProperty(value => value.SendLeaseOwner, leaseOwner)
                .SetProperty(value => value.SendLeaseExpiresAtUtc, nowUtc.Add(leaseDuration))
                .SetProperty(value => value.SendCoordinationVersion, value => value.SendCoordinationVersion + 1), cancellationToken);
        if (robotUpdated != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var updated = await database.SendCommands
            .Where(command => command.Id == candidate.Id && command.Version == candidate.Version &&
                (((command.Status == "pending" || command.Status == "retrying") && command.NextAttemptAtUtc <= nowUtc) ||
                (command.Status == "leased" && command.LeaseExpiresAtUtc <= nowUtc)))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(command => command.Status, "leased")
                .SetProperty(command => command.LeaseOwner, leaseOwner)
                .SetProperty(command => command.LeaseExpiresAtUtc, nowUtc.Add(leaseDuration))
                .SetProperty(command => command.Version, command => command.Version + 1), cancellationToken);
        if (updated != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        await transaction.CommitAsync(cancellationToken);
        var leased = await database.SendCommands.AsNoTracking().SingleAsync(command => command.Id == candidate.Id, cancellationToken);
        var payload = JsonSerializer.Deserialize<SendPayload>(leased.PayloadJson) ?? throw new InvalidOperationException("Send command payload is invalid.");
        return new LeasedSendCommand(leased.Id, leased.RobotConfigId, payload.WorkToolRobotId, payload.GroupName, payload.Text, leased.IdempotencyKey, robotState.SendRateLimitPerMinute, leased.AttemptCount, leaseOwner);
    }

    public async Task CompleteSendCommandAsync(LeasedSendCommand command, DateTime completedAtUtc, CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var completed = await database.SendCommands
            .Where(value => value.Id == command.Id && value.Status == "leased" && value.LeaseOwner == command.LeaseOwner)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.Status, "completed")
                .SetProperty(value => value.SentAtUtc, completedAtUtc)
                .SetProperty(value => value.CompletedAtUtc, completedAtUtc)
                .SetProperty(value => value.LeaseOwner, (string?)null)
                .SetProperty(value => value.LeaseExpiresAtUtc, (DateTime?)null)
                .SetProperty(value => value.Version, value => value.Version + 1), cancellationToken);
        if (completed != 1 || await ReleaseRobotGuardAsync(command, cancellationToken) != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task FailSendCommandAsync(LeasedSendCommand command, string reason, DateTime failedAtUtc, TimeSpan? retryDelay, CancellationToken cancellationToken)
    {
        var attempts = command.AttemptCount + 1;
        if (retryDelay is null)
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            var updated = await database.SendCommands
                .Where(value => value.Id == command.Id && value.Status == "leased" && value.LeaseOwner == command.LeaseOwner)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(value => value.Status, "deadLetter")
                    .SetProperty(value => value.AttemptCount, attempts)
                    .SetProperty(value => value.LeaseOwner, (string?)null)
                    .SetProperty(value => value.LeaseExpiresAtUtc, (DateTime?)null)
                    .SetProperty(value => value.Version, value => value.Version + 1), cancellationToken);
            if (updated == 1)
            {
                database.DeadLetters.Add(new DeadLetterEntity { SendCommandId = command.Id, Reason = reason, PayloadJson = JsonSerializer.Serialize(command), CreatedAtUtc = failedAtUtc });
                await database.SaveChangesAsync(cancellationToken);
                await ReleaseRobotGuardAsync(command, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        await using var retryTransaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var retried = await database.SendCommands
            .Where(value => value.Id == command.Id && value.Status == "leased" && value.LeaseOwner == command.LeaseOwner)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.Status, "retrying")
                .SetProperty(value => value.AttemptCount, attempts)
                .SetProperty(value => value.NextAttemptAtUtc, failedAtUtc.Add(retryDelay.Value))
                .SetProperty(value => value.LeaseOwner, (string?)null)
                .SetProperty(value => value.LeaseExpiresAtUtc, (DateTime?)null)
                .SetProperty(value => value.Version, value => value.Version + 1), cancellationToken);
        if (retried == 1)
        {
            await ReleaseRobotGuardAsync(command, cancellationToken);
            await retryTransaction.CommitAsync(cancellationToken);
        }
        else
        {
            await retryTransaction.RollbackAsync(cancellationToken);
        }
    }

    public async Task<bool> RenewSendLeasesAsync(LeasedSendCommand command, DateTime nowUtc, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var commandRenewed = await database.SendCommands
            .Where(value => value.Id == command.Id && value.Status == "leased" && value.LeaseOwner == command.LeaseOwner)
            .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.LeaseExpiresAtUtc, nowUtc.Add(leaseDuration)), cancellationToken);
        var robotRenewed = await database.RobotConfigs
            .Where(value => value.Id == command.RobotConfigId && value.SendLeaseOwner == command.LeaseOwner)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.SendLeaseExpiresAtUtc, nowUtc.Add(leaseDuration))
                .SetProperty(value => value.SendCoordinationVersion, value => value.SendCoordinationVersion + 1), cancellationToken);
        if (commandRenewed != 1 || robotRenewed != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private Task<int> ReleaseRobotGuardAsync(LeasedSendCommand command, CancellationToken cancellationToken) => database.RobotConfigs
        .Where(value => value.Id == command.RobotConfigId && value.SendLeaseOwner == command.LeaseOwner)
        .ExecuteUpdateAsync(setters => setters
            .SetProperty(value => value.SendLeaseOwner, (string?)null)
            .SetProperty(value => value.SendLeaseExpiresAtUtc, (DateTime?)null)
            .SetProperty(value => value.SendCoordinationVersion, value => value.SendCoordinationVersion + 1), cancellationToken);

    private async Task UpdateRelatedMessageStateAsync(Guid jobId, string state, CancellationToken token)
    {
        var messageId = await database.DurableJobs.AsNoTracking().Where(job => job.Id == jobId)
            .Select(job => job.RelatedConversationMessageId).SingleOrDefaultAsync(token);
        if (messageId is { } id)
            await database.ConversationMessages.Where(message => message.Id == id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(message => message.ProcessingState, state), token);
    }

    private static TimeSpan SendRetryDelay(int attempts) => attempts switch
    {
        1 => TimeSpan.FromSeconds(5),
        2 => TimeSpan.FromSeconds(15),
        3 => TimeSpan.FromSeconds(45),
        _ => throw new InvalidOperationException("Only retryable attempts have a delay.")
    };

    private sealed class SendPayload
    {
        public string WorkToolRobotId { get; init; } = string.Empty;
        public string GroupName { get; init; } = string.Empty;
        public string Text { get; init; } = string.Empty;
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException exception) => exception.InnerException is MySqlException { Number: 1062 };
}
