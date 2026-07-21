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
            SenderExternalUserId = request.SenderName,
            Text = request.Text,
            ReceivedAtUtc = request.ReceivedAtUtc
        };
        database.ConversationMessages.Add(message);
        database.DurableJobs.Add(new DurableJobEntity
        {
            JobType = "ProcessInboundMessage",
            PayloadJson = JsonSerializer.Serialize(new
            {
                messageId = message.Id,
                request.RobotConfigId,
                workToolRobotId = robot.WorkToolRobotId,
                request.GroupName,
                request.SenderName,
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

    public async Task<LeasedDurableJob?> LeaseNextJobAsync(string leaseOwner, DateTime nowUtc, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        var candidate = await database.DurableJobs.AsNoTracking()
            .Where(job =>
                (job.Status == "pending" || job.Status == "retrying") && job.NextAttemptAtUtc <= nowUtc ||
                job.Status == "leased" && job.LeaseExpiresAtUtc <= nowUtc)
            .OrderBy(job => job.NextAttemptAtUtc)
            .ThenBy(job => job.CreatedAtUtc)
            .Select(job => new { job.Id, job.Version })
            .FirstOrDefaultAsync(cancellationToken);
        if (candidate is null)
        {
            return null;
        }

        var leaseExpiry = nowUtc.Add(leaseDuration);
        var updated = await database.DurableJobs
            .Where(job => job.Id == candidate.Id && job.Version == candidate.Version && (
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
            return null;
        }

        var leased = await database.DurableJobs.AsNoTracking().SingleAsync(job => job.Id == candidate.Id, cancellationToken);
        return new LeasedDurableJob(leased.Id, leased.JobType, leased.PayloadJson, leased.AttemptCount, leaseOwner);
    }

    public Task CompleteJobAsync(Guid jobId, string leaseOwner, DateTime completedAtUtc, CancellationToken cancellationToken) => database.DurableJobs
        .Where(job => job.Id == jobId && job.Status == "leased" && job.LeaseOwner == leaseOwner)
        .ExecuteUpdateAsync(setters => setters
            .SetProperty(job => job.Status, "completed")
            .SetProperty(job => job.CompletedAtUtc, completedAtUtc)
            .SetProperty(job => job.LeaseOwner, (string?)null)
            .SetProperty(job => job.LeaseExpiresAtUtc, (DateTime?)null)
            .SetProperty(job => job.Version, job => job.Version + 1)
            .SetProperty(job => job.UpdatedAtUtc, completedAtUtc), cancellationToken);

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
                database.DeadLetters.Add(new DeadLetterEntity { DurableJobId = job.Id, Reason = reason, PayloadJson = job.PayloadJson, CreatedAtUtc = failedAtUtc });
                await database.SaveChangesAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        var delay = SendRetryDelay(attempts);
        await database.DurableJobs
            .Where(value => value.Id == job.Id && value.Status == "leased" && value.LeaseOwner == job.LeaseOwner)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.Status, "retrying")
                .SetProperty(value => value.AttemptCount, attempts)
                .SetProperty(value => value.NextAttemptAtUtc, failedAtUtc.Add(delay))
                .SetProperty(value => value.LeaseOwner, (string?)null)
                .SetProperty(value => value.LeaseExpiresAtUtc, (DateTime?)null)
                .SetProperty(value => value.Version, value => value.Version + 1)
                .SetProperty(value => value.UpdatedAtUtc, failedAtUtc), cancellationToken);
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
        var candidate = await database.SendCommands.AsNoTracking()
            .Where(command =>
                (((command.Status == "pending" || command.Status == "retrying") && command.NextAttemptAtUtc <= nowUtc) ||
                (command.Status == "leased" && command.LeaseExpiresAtUtc <= nowUtc)) &&
                !database.SendCommands.Any(earlier =>
                    earlier.RobotConfigId == command.RobotConfigId &&
                    earlier.Id != command.Id &&
                    earlier.CreatedAtUtc < command.CreatedAtUtc &&
                    earlier.Status != "completed" &&
                    earlier.Status != "deadLetter"))
            .OrderBy(command => command.NextAttemptAtUtc)
            .ThenBy(command => command.CreatedAtUtc)
            .Select(command => new { command.Id, command.Version })
            .FirstOrDefaultAsync(cancellationToken);
        if (candidate is null)
        {
            return null;
        }

        var updated = await database.SendCommands
            .Where(command => command.Id == candidate.Id && command.Version == candidate.Version &&
                (((command.Status == "pending" || command.Status == "retrying") && command.NextAttemptAtUtc <= nowUtc) ||
                (command.Status == "leased" && command.LeaseExpiresAtUtc <= nowUtc)) &&
                !database.SendCommands.Any(earlier =>
                    earlier.RobotConfigId == command.RobotConfigId &&
                    earlier.Id != command.Id &&
                    earlier.CreatedAtUtc < command.CreatedAtUtc &&
                    earlier.Status != "completed" &&
                    earlier.Status != "deadLetter"))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(command => command.Status, "leased")
                .SetProperty(command => command.LeaseOwner, leaseOwner)
                .SetProperty(command => command.LeaseExpiresAtUtc, nowUtc.Add(leaseDuration))
                .SetProperty(command => command.Version, command => command.Version + 1), cancellationToken);
        if (updated != 1)
        {
            return null;
        }

        var leased = await database.SendCommands.AsNoTracking().SingleAsync(command => command.Id == candidate.Id, cancellationToken);
        var robot = await database.RobotConfigs.AsNoTracking().SingleAsync(config => config.Id == leased.RobotConfigId, cancellationToken);
        var payload = JsonSerializer.Deserialize<SendPayload>(leased.PayloadJson) ?? throw new InvalidOperationException("Send command payload is invalid.");
        return new LeasedSendCommand(leased.Id, leased.RobotConfigId, payload.WorkToolRobotId, payload.GroupName, payload.Text, leased.IdempotencyKey, robot.SendRateLimitPerMinute, leased.AttemptCount, leaseOwner);
    }

    public Task CompleteSendCommandAsync(Guid commandId, string leaseOwner, DateTime completedAtUtc, CancellationToken cancellationToken) => database.SendCommands
        .Where(command => command.Id == commandId && command.Status == "leased" && command.LeaseOwner == leaseOwner)
        .ExecuteUpdateAsync(setters => setters
            .SetProperty(command => command.Status, "completed")
            .SetProperty(command => command.SentAtUtc, completedAtUtc)
            .SetProperty(command => command.CompletedAtUtc, completedAtUtc)
            .SetProperty(command => command.LeaseOwner, (string?)null)
            .SetProperty(command => command.LeaseExpiresAtUtc, (DateTime?)null)
            .SetProperty(command => command.Version, command => command.Version + 1), cancellationToken);

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
            }
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        await database.SendCommands
            .Where(value => value.Id == command.Id && value.Status == "leased" && value.LeaseOwner == command.LeaseOwner)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.Status, "retrying")
                .SetProperty(value => value.AttemptCount, attempts)
                .SetProperty(value => value.NextAttemptAtUtc, failedAtUtc.Add(retryDelay.Value))
                .SetProperty(value => value.LeaseOwner, (string?)null)
                .SetProperty(value => value.LeaseExpiresAtUtc, (DateTime?)null)
                .SetProperty(value => value.Version, value => value.Version + 1), cancellationToken);
    }

    public Task ReleaseSendCommandAsync(Guid commandId, string leaseOwner, DateTime availableAtUtc, CancellationToken cancellationToken) => database.SendCommands
        .Where(command => command.Id == commandId && command.Status == "leased" && command.LeaseOwner == leaseOwner)
        .ExecuteUpdateAsync(setters => setters
            .SetProperty(command => command.Status, "pending")
            .SetProperty(command => command.NextAttemptAtUtc, availableAtUtc)
            .SetProperty(command => command.LeaseOwner, (string?)null)
            .SetProperty(command => command.LeaseExpiresAtUtc, (DateTime?)null)
            .SetProperty(command => command.Version, command => command.Version + 1), cancellationToken);

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
