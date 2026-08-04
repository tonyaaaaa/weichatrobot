using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;
using WechatRobot.Application.Jobs;
using WechatRobot.Application.WorkTool;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.Infrastructure.Persistence;

public sealed class DurableJobRepository(WechatRobotDbContext database) : IDurableJobRepository
{
    private static readonly TimeSpan WorkToolResultTimeout = TimeSpan.FromMinutes(10);
    private MySqlRobotSendLock? activeSendGate;
    public async Task<InboundMessageIngestResult> IngestInboundMessageAsync(InboundMessageIngestRequest request, CancellationToken cancellationToken)
    {
        var hasWorkToolMessageId = !string.IsNullOrWhiteSpace(request.WorkToolMessageId);
        var alreadyIngested = await database.ConversationMessages.AsNoTracking().AnyAsync(
            message =>
                hasWorkToolMessageId && message.WorkToolMessageId == request.WorkToolMessageId
                || message.FallbackHash == request.FallbackHash
                && message.FallbackWindowStartUtc == request.FallbackWindowStartUtc,
            cancellationToken);
        if (alreadyIngested)
        {
            return InboundMessageIngestResult.Duplicate;
        }

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var message = new ConversationMessageEntity
        {
            RobotConfigId = request.RobotConfigId,
            WorkToolMessageId = string.IsNullOrWhiteSpace(request.WorkToolMessageId) ? null : request.WorkToolMessageId,
            FallbackHash = request.FallbackHash,
            FallbackWindowStartUtc = request.FallbackWindowStartUtc,
            GroupName = request.GroupName,
            ChannelType = request.ChannelType,
            RoomType = request.RoomType,
            PeerDisplayName = request.PeerDisplayName,
            ScopeHash = request.ScopeHash,
            GroupRemark = request.GroupRemark,
            SenderDisplayName = request.SenderDisplayName,
            StableSenderId = request.StableSenderId,
            ProcessingState = "pending",
            Text = request.Text,
            ReceivedAtUtc = request.ReceivedAtUtc
        };
        var matchedGroupIds = request.ChannelType == "Private"
            ? []
            : await database.GroupProfiles.AsNoTracking()
            .Where(group =>
                group.RobotConfigId == request.RobotConfigId &&
                group.Name == request.GroupName &&
                (string.IsNullOrEmpty(request.GroupRemark)
                    ? group.WorkToolGroupRemark == null
                    : group.WorkToolGroupRemark == request.GroupRemark || group.WorkToolGroupRemark == null))
            .OrderByDescending(group => group.WorkToolGroupRemark == request.GroupRemark)
            .Select(group => group.Id)
            .Take(2)
            .ToArrayAsync(cancellationToken);
        if (matchedGroupIds.Length == 1)
            message.GroupProfileId = matchedGroupIds[0];
        database.ConversationMessages.Add(message);
        database.DurableJobs.Add(new DurableJobEntity
        {
            JobType = request.ChannelType == "Private"
                ? "ProcessPrivateMessage"
                : "ProcessInboundMessage",
            RelatedConversationMessageId = message.Id,
            GroupProfileId = message.GroupProfileId,
            PayloadJson = JsonSerializer.Serialize(new
            {
                messageId = message.Id,
                request.RobotConfigId,
                request.GroupName,
                request.GroupRemark,
                request.SenderDisplayName,
                request.StableSenderId,
                request.WasMentioned,
                request.ChannelType,
                request.RoomType,
                request.PeerDisplayName,
                request.ScopeHash,
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
        return new LeasedDurableJob(
            leased.Id,
            leased.JobType,
            leased.PayloadJson,
            leased.AttemptCount,
            leaseOwner,
            leased.CreatedAtUtc);
    }

    public async Task CompleteJobAsync(Guid jobId, string leaseOwner, DateTime completedAtUtc, CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var job = await database.DurableJobs.SingleOrDefaultAsync(
            value => value.Id == jobId && value.Status == "leased" && value.LeaseOwner == leaseOwner,
            cancellationToken);
        if (job is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }
        job.Status = "completed";
        job.CompletedAtUtc = completedAtUtc;
        job.LeaseOwner = null;
        job.LeaseExpiresAtUtc = null;
        job.Version++;
        job.UpdatedAtUtc = completedAtUtc;
        await UpdateRelatedMessageStateTrackedAsync(job, "completed", cancellationToken);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            database.ChangeTracker.Clear();
        }
    }

    public async Task DeferJobAsync(
        LeasedDurableJob job,
        string reason,
        DateTime deferredAtUtc,
        TimeSpan retryDelay,
        CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(
            cancellationToken);
        var owned = await database.DurableJobs.SingleOrDefaultAsync(value =>
                value.Id == job.Id
                && value.Status == "leased"
                && value.LeaseOwner == job.LeaseOwner,
            cancellationToken);
        if (owned is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }
        owned.Status = "retrying";
        owned.NextAttemptAtUtc = deferredAtUtc.Add(retryDelay);
        owned.LeaseOwner = null;
        owned.LeaseExpiresAtUtc = null;
        owned.Version++;
        owned.UpdatedAtUtc = deferredAtUtc;
        await UpdateRelatedMessageStateTrackedAsync(owned, "retrying", cancellationToken);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            database.ChangeTracker.Clear();
        }
    }

    public async Task<bool> RenewJobLeaseAsync(
        LeasedDurableJob job,
        DateTime nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        var updated = await database.DurableJobs
            .Where(value =>
                value.Id == job.Id
                && value.Status == "leased"
                && value.LeaseOwner == job.LeaseOwner
                && value.LeaseExpiresAtUtc > nowUtc)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(
                    value => value.LeaseExpiresAtUtc,
                    nowUtc.Add(leaseDuration))
                .SetProperty(value => value.Version, value => value.Version + 1)
                .SetProperty(value => value.UpdatedAtUtc, nowUtc),
                cancellationToken);
        return updated == 1;
    }

    public async Task FailJobAsync(LeasedDurableJob job, string reason, DateTime failedAtUtc, CancellationToken cancellationToken)
    {
        var attempts = job.AttemptCount + 1;
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var owned = await database.DurableJobs.SingleOrDefaultAsync(
            value => value.Id == job.Id && value.Status == "leased" && value.LeaseOwner == job.LeaseOwner,
            cancellationToken);
        if (owned is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }
        owned.AttemptCount = attempts;
        owned.LeaseOwner = null;
        owned.LeaseExpiresAtUtc = null;
        owned.Version++;
        owned.UpdatedAtUtc = failedAtUtc;
        if (attempts >= 4)
        {
            owned.Status = "deadLetter";
            await UpdateRelatedMessageStateTrackedAsync(owned, "deadLetter", cancellationToken);
            database.DeadLetters.Add(new DeadLetterEntity { DurableJobId = job.Id, Reason = reason, PayloadJson = job.PayloadJson, CreatedAtUtc = failedAtUtc });
        }
        else
        {
            owned.Status = "retrying";
            owned.NextAttemptAtUtc = failedAtUtc.Add(SendRetryDelay(attempts));
            await UpdateRelatedMessageStateTrackedAsync(owned, "retrying", cancellationToken);
        }
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            database.ChangeTracker.Clear();
        }
    }

    public async Task<EnqueueSendCommandResult> EnqueueSendCommandAsync(EnqueueSendCommandRequest request, CancellationToken cancellationToken)
    {
        await using var sendGate = await MySqlRobotSendCoordinator.AcquireAsync(database, request.RobotConfigId, cancellationToken);
        await using var transaction = database.Database.IsRelational()
            ? await database.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var status = await MySqlRobotSendCoordinator.InitialStatusAsync(database, request.RobotConfigId, cancellationToken);
        database.SendCommands.Add(new SendCommandEntity
        {
            RobotConfigId = request.RobotConfigId,
            IdempotencyKey = request.IdempotencyKey,
            PayloadJson = JsonSerializer.Serialize(new { request.GroupName, request.Text }),
            Status = status,
            NextAttemptAtUtc = DateTime.UtcNow
        });
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return EnqueueSendCommandResult.Enqueued;
        }
        catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
        {
            if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None);
            database.ChangeTracker.Clear();
            return EnqueueSendCommandResult.AlreadyExists;
        }
    }

    public async Task<LeasedSendCommand?> LeaseNextSendCommandAsync(string leaseOwner, DateTime nowUtc, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        foreach (var timedOut in await database.SendCommands
            .Where(command => command.Status == WorkToolCommandStatuses.Accepted &&
                              command.AcceptedAtUtc <= nowUtc.Subtract(WorkToolResultTimeout))
            .ToArrayAsync(cancellationToken))
        {
            timedOut.Status = WorkToolCommandStatuses.ResultTimeout;
            timedOut.CompletedAtUtc = nowUtc;
            timedOut.Version++;
        }

        var expiredExternal = await database.SendCommands
            .Where(command => command.Status == WorkToolCommandStatuses.Dispatching && command.LeaseExpiresAtUtc <= nowUtc)
            .ToArrayAsync(cancellationToken);
        foreach (var expired in expiredExternal)
        {
            var expiredOwner = expired.LeaseOwner;
            expired.Status = WorkToolCommandStatuses.DeliveryUnknown;
            expired.ReconciliationReason = "external_dispatch_lease_expired";
            expired.LeaseOwner = null;
            expired.LeaseExpiresAtUtc = null;
            expired.Version++;
            var robot = await database.RobotConfigs.SingleOrDefaultAsync(
                value => value.Id == expired.RobotConfigId && value.SendLeaseOwner == expiredOwner,
                cancellationToken);
            if (robot is not null)
            {
                robot.SendLeaseOwner = null;
                robot.SendLeaseExpiresAtUtc = null;
                robot.SendCoordinationVersion++;
            }
        }
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            database.ChangeTracker.Clear();
        }
        var candidate = await (from command in database.SendCommands.AsNoTracking()
                               join robot in database.RobotConfigs.AsNoTracking() on command.RobotConfigId equals robot.Id
                               where (((command.Status == "pending" || command.Status == "retrying") && command.NextAttemptAtUtc <= nowUtc) ||
                                      (command.Status == "leased" && command.LeaseExpiresAtUtc <= nowUtc)) &&
                                     robot.IsEnabled &&
                                     (robot.SendLeaseOwner == null || robot.SendLeaseExpiresAtUtc <= nowUtc) &&
                                     !database.SendCommands.Any(earlier =>
                                         earlier.RobotConfigId == command.RobotConfigId &&
                                         (earlier.Status == WorkToolCommandStatuses.Pending ||
                                           earlier.Status == WorkToolCommandStatuses.Retrying ||
                                           earlier.Status == WorkToolCommandStatuses.Leased ||
                                           earlier.Status == WorkToolCommandStatuses.Dispatching ||
                                           earlier.Status == WorkToolCommandStatuses.Blocked) &&
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
        var robotState = await database.RobotConfigs.AsNoTracking().SingleOrDefaultAsync(value => value.Id == candidate.RobotConfigId && value.IsEnabled, cancellationToken);
        if (robotState is null) { await transaction.RollbackAsync(cancellationToken); return null; }
        var capacity = (decimal)robotState.SendRateLimitPerMinute;
        var elapsedSeconds = Math.Max(0, (nowUtc - robotState.SendRateUpdatedAtUtc).TotalSeconds);
        var availableTokens = Math.Min(capacity, robotState.SendRateTokens + (decimal)(elapsedSeconds * robotState.SendRateLimitPerMinute / 60d));
        if (availableTokens < 1)
        {
            return null;
        }

        var robotUpdated = await database.RobotConfigs
            .Where(value => value.Id == robotState.Id && value.SendCoordinationVersion == robotState.SendCoordinationVersion &&
                value.IsEnabled &&
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
        return new LeasedSendCommand(leased.Id, leased.RobotConfigId, string.Empty, payload.GroupName, payload.Text, leased.IdempotencyKey,
            robotState.SendRateLimitPerMinute, leased.AttemptCount, leaseOwner, payload.AtList);
    }

    public async Task<bool> EnsureSendEnabledAsync(LeasedSendCommand command, CancellationToken cancellationToken)
    {
        if (activeSendGate is not null) throw new InvalidOperationException("A robot send gate is already held by this repository.");
        var sendGate = await MySqlRobotSendCoordinator.AcquireAsync(database, command.RobotConfigId, cancellationToken);
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            var robot = await database.RobotConfigs.FromSqlInterpolated(
                $"SELECT * FROM robot_config WHERE Id = {command.RobotConfigId} FOR UPDATE").AsNoTracking().SingleOrDefaultAsync(cancellationToken);
            var groupEnabled = await database.SendCommands.AsNoTracking()
                .Where(value => value.Id == command.Id)
                .Select(value => value.GroupProfileId == null ||
                    database.GroupProfiles.Any(group =>
                        group.Id == value.GroupProfileId &&
                        group.IsEnabled &&
                        group.ArchivedAtUtc == null))
                .SingleAsync(cancellationToken);
            if (robot is not null && robot.IsEnabled && groupEnabled)
            {
                var owned = await database.SendCommands.AsNoTracking().AnyAsync(value => value.Id == command.Id && value.Status == "leased" &&
                    value.LeaseOwner == command.LeaseOwner, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                if (owned) activeSendGate = sendGate;
                else await sendGate.DisposeAsync();
                return owned;
            }
            var blocked = await database.SendCommands.SingleOrDefaultAsync(
                value => value.Id == command.Id && value.Status == "leased" && value.LeaseOwner == command.LeaseOwner,
                cancellationToken);
            if (blocked is not null)
            {
                blocked.Status = "blocked";
                blocked.LeaseOwner = null;
                blocked.LeaseExpiresAtUtc = null;
                blocked.Version++;
            }
            _ = await ReleaseRobotGuardTrackedAsync(command, cancellationToken);
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            await sendGate.DisposeAsync();
            return false;
        }
        catch
        {
            await sendGate.DisposeAsync();
            throw;
        }
    }

    public async Task ReleaseSendGateAsync(CancellationToken cancellationToken)
    {
        if (activeSendGate is null) return;
        var sendGate = activeSendGate;
        activeSendGate = null;
        await sendGate.DisposeAsync();
    }

    public async Task<bool> MarkSendDispatchingAsync(LeasedSendCommand command, DateTime dispatchedAtUtc, CancellationToken cancellationToken)
    {
        var updated = await database.SendCommands
            .Where(value =>
                value.Id == command.Id &&
                value.Status == "leased" &&
                value.LeaseOwner == command.LeaseOwner &&
                (value.GroupProfileId == null || database.GroupProfiles.Any(group =>
                    group.Id == value.GroupProfileId &&
                    group.IsEnabled &&
                    group.ArchivedAtUtc == null)))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.Status, WorkToolCommandStatuses.Dispatching)
                .SetProperty(value => value.ExternalDispatchStartedAtUtc, dispatchedAtUtc)
                .SetProperty(value => value.Version, value => value.Version + 1), cancellationToken);
        return updated == 1;
    }

    public async Task MarkSendDeliveryUnknownAsync(LeasedSendCommand command, string reason, DateTime failedAtUtc, CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var entity = await database.SendCommands.SingleOrDefaultAsync(
            value => value.Id == command.Id && value.Status == WorkToolCommandStatuses.Dispatching && value.LeaseOwner == command.LeaseOwner,
            cancellationToken);
        if (entity is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }
        entity.Status = WorkToolCommandStatuses.DeliveryUnknown;
        entity.ReconciliationReason = reason.Length > 256 ? reason[..256] : reason;
        entity.LeaseOwner = null;
        entity.LeaseExpiresAtUtc = null;
        entity.Version++;
        _ = await ReleaseRobotGuardTrackedAsync(command, cancellationToken);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            database.ChangeTracker.Clear();
        }
    }

    public async Task MarkSendRejectedAsync(
        LeasedSendCommand command,
        string reason,
        DateTime rejectedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var entity = await database.SendCommands.SingleOrDefaultAsync(
            value => value.Id == command.Id && value.Status == WorkToolCommandStatuses.Dispatching && value.LeaseOwner == command.LeaseOwner,
            cancellationToken);
        if (entity is null || await ReleaseRobotGuardTrackedAsync(command, cancellationToken) != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }
        entity.Status = WorkToolCommandStatuses.Rejected;
        entity.ReconciliationReason = reason.Length > 256 ? reason[..256] : reason;
        entity.CompletedAtUtc = rejectedAtUtc;
        entity.LeaseOwner = null;
        entity.LeaseExpiresAtUtc = null;
        entity.Version++;
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            database.ChangeTracker.Clear();
        }
    }

    public async Task MarkSendAcceptedAsync(
        LeasedSendCommand command,
        string workToolMessageId,
        DateTime acceptedAtUtc,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workToolMessageId) || workToolMessageId.Length > 128)
            throw new ArgumentException("A valid WorkTool message ID is required.", nameof(workToolMessageId));

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var entity = await database.SendCommands.SingleOrDefaultAsync(
            value => value.Id == command.Id && value.Status == WorkToolCommandStatuses.Dispatching && value.LeaseOwner == command.LeaseOwner,
            cancellationToken);
        if (entity is null || await ReleaseRobotGuardTrackedAsync(command, cancellationToken) != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }
        entity.Status = WorkToolCommandStatuses.Accepted;
        entity.WorkToolCommandMessageId = workToolMessageId;
        entity.SentAtUtc = acceptedAtUtc;
        entity.AcceptedAtUtc = acceptedAtUtc;
        entity.CompletedAtUtc = null;
        entity.ReconciliationReason = null;
        entity.LeaseOwner = null;
        entity.LeaseExpiresAtUtc = null;
        entity.Version++;
        var memoryIds = ReadMemoryRecallIds(entity.PayloadJson);
        if (memoryIds.Length > 0)
        {
            foreach (var batch in GuidBatchQuery.CreateBatches(memoryIds))
            {
                foreach (var memory in await database.MemoryEntries
                             .Where(GuidBatchQuery.BuildPredicate<MemoryEntryEntity>(batch, entry => entry.Id))
                             .Where(entry => entry.Status == "active")
                             .ToArrayAsync(cancellationToken))
                {
                    memory.RecallCount++;
                    memory.LastRecalledAtUtc = acceptedAtUtc;
                    memory.UpdatedAtUtc = acceptedAtUtc;
                }
            }
        }
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            database.ChangeTracker.Clear();
        }
    }

    private static Guid[] ReadMemoryRecallIds(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return [];
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (!document.RootElement.TryGetProperty("MemoryRecallIds", out var values) ||
                values.ValueKind != JsonValueKind.Array)
                return [];
            return values.EnumerateArray()
                .Take(5)
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => Guid.TryParse(value.GetString(), out var id) ? id : Guid.Empty)
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public async Task FailSendCommandAsync(LeasedSendCommand command, string reason, DateTime failedAtUtc, TimeSpan? retryDelay, CancellationToken cancellationToken)
    {
        var attempts = command.AttemptCount + 1;
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var entity = await database.SendCommands.SingleOrDefaultAsync(
            value => value.Id == command.Id &&
                     (value.Status == WorkToolCommandStatuses.Leased || value.Status == WorkToolCommandStatuses.Dispatching) &&
                     value.LeaseOwner == command.LeaseOwner,
            cancellationToken);
        if (entity is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }
        entity.AttemptCount = attempts;
        entity.LeaseOwner = null;
        entity.LeaseExpiresAtUtc = null;
        entity.Version++;
        if (retryDelay is null)
        {
            entity.Status = "deadLetter";
            database.DeadLetters.Add(new DeadLetterEntity { SendCommandId = command.Id, Reason = reason, PayloadJson = JsonSerializer.Serialize(command), CreatedAtUtc = failedAtUtc });
        }
        else
        {
            entity.Status = "retrying";
            entity.NextAttemptAtUtc = failedAtUtc.Add(retryDelay.Value);
        }
        _ = await ReleaseRobotGuardTrackedAsync(command, cancellationToken);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            database.ChangeTracker.Clear();
        }
    }

    public async Task<bool> RenewSendLeasesAsync(LeasedSendCommand command, DateTime nowUtc, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var commandRenewed = await database.SendCommands
            .Where(value => value.Id == command.Id && value.Status == WorkToolCommandStatuses.Dispatching && value.LeaseOwner == command.LeaseOwner)
            .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.LeaseExpiresAtUtc, nowUtc.Add(leaseDuration)), cancellationToken);
        var robotRenewed = await database.RobotConfigs
            .Where(value => value.Id == command.RobotConfigId && value.IsEnabled && value.SendLeaseOwner == command.LeaseOwner)
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

    private async Task<int> ReleaseRobotGuardTrackedAsync(
        LeasedSendCommand command,
        CancellationToken cancellationToken)
    {
        var robot = await database.RobotConfigs.SingleOrDefaultAsync(
            value => value.Id == command.RobotConfigId && value.SendLeaseOwner == command.LeaseOwner,
            cancellationToken);
        if (robot is null) return 0;
        robot.SendLeaseOwner = null;
        robot.SendLeaseExpiresAtUtc = null;
        robot.SendCoordinationVersion++;
        return 1;
    }

    private async Task UpdateRelatedMessageStateAsync(Guid jobId, string state, CancellationToken token)
    {
        var messageId = await database.DurableJobs.AsNoTracking().Where(job => job.Id == jobId)
            .Select(job => job.RelatedConversationMessageId).SingleOrDefaultAsync(token);
        if (messageId is { } id)
            await database.ConversationMessages.Where(message => message.Id == id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(message => message.ProcessingState, state), token);
    }

    private async Task UpdateRelatedMessageStateTrackedAsync(
        DurableJobEntity job,
        string state,
        CancellationToken token)
    {
        if (job.RelatedConversationMessageId is not { } messageId) return;
        var message = await database.ConversationMessages.SingleOrDefaultAsync(
            value => value.Id == messageId,
            token);
        if (message is not null) message.ProcessingState = state;
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
        public string GroupName { get; init; } = string.Empty;
        public string Text { get; init; } = string.Empty;
        public string[]? AtList { get; init; }
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException exception) => exception.InnerException is MySqlException { Number: 1062 };
}
