using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Security;
using WechatRobot.Application.WorkTool;
using WechatRobot.Infrastructure.Persistence;

namespace WechatRobot.Worker.Jobs;

public sealed class WorkToolGroupOperationWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider) : BackgroundService
{
    private readonly string _owner = $"group-operation-{Environment.MachineName}-{Guid.NewGuid():N}";
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ResultTimeout = TimeSpan.FromMinutes(10);

    public async Task<bool> ProcessOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var timedOut = await database.WorkToolOperationAudits
            .Where(item => item.Status == WorkToolCommandStatuses.Accepted &&
                           item.AcceptedAtUtc <= now.Subtract(ResultTimeout))
            .ToArrayAsync(cancellationToken);
        foreach (var audit in timedOut)
        {
            audit.Status = WorkToolCommandStatuses.ResultTimeout;
            audit.CompletedAtUtc = now;
            audit.Version++;
        }

        var expiredDispatches = await database.WorkToolOperationAudits
            .Where(item => item.Status == WorkToolCommandStatuses.Dispatching && item.LeaseExpiresAtUtc <= now)
            .ToArrayAsync(cancellationToken);
        foreach (var audit in expiredDispatches)
        {
            audit.Status = WorkToolCommandStatuses.DeliveryUnknown;
            audit.Result = "external_dispatch_lease_expired";
            audit.LeaseOwner = null;
            audit.LeaseExpiresAtUtc = null;
            audit.Version++;
        }
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            database.ChangeTracker.Clear();
        }

        var candidate = await database.WorkToolOperationAudits.AsNoTracking()
            .Where(item => item.Status == WorkToolCommandStatuses.Queued || item.Status == "Queued")
            .OrderBy(item => item.CreatedAtUtc).ThenBy(item => item.Id)
            .Select(item => new { item.Id, item.Version })
            .FirstOrDefaultAsync(cancellationToken);
        if (candidate is null) return false;
        var leased = await database.WorkToolOperationAudits
            .Where(item => item.Id == candidate.Id &&
                           (item.Status == WorkToolCommandStatuses.Queued || item.Status == "Queued") &&
                           item.Version == candidate.Version)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, WorkToolCommandStatuses.Dispatching)
                .SetProperty(item => item.ExternalDispatchStartedAtUtc, now)
                .SetProperty(item => item.LeaseOwner, _owner)
                .SetProperty(item => item.LeaseExpiresAtUtc, now.Add(LeaseDuration))
                .SetProperty(item => item.Version, item => item.Version + 1), cancellationToken);
        if (leased != 1) return true;

        var commandRow = await database.WorkToolOperationAudits.AsNoTracking()
            .SingleAsync(item => item.Id == candidate.Id, cancellationToken);
        try
        {
            var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
            var command = JsonSerializer.Deserialize<WorkToolGroupOperationRequest>(
                protector.Unprotect(commandRow.EncryptedCommandJson!))
                ?? throw new InvalidOperationException("Stored group operation is invalid.");
            var result = await scope.ServiceProvider.GetRequiredService<IWorkToolClient>()
                .ExecuteGroupOperationAsync(command, cancellationToken);
            if (result.Accepted &&
                !string.IsNullOrWhiteSpace(result.MessageId) &&
                result.MessageId.Length <= WorkToolCommandResultDto.MaximumMessageIdLength)
            {
                await MarkAcceptedAsync(
                    database,
                    candidate.Id,
                    result.MessageId,
                    timeProvider.GetUtcNow().UtcDateTime,
                    cancellationToken);
            }
            else if (result.Accepted || result.DeliveryMayHaveOccurred)
            {
                await CompleteAsync(database, candidate.Id, WorkToolCommandStatuses.DeliveryUnknown, "delivery_outcome_unknown", null, cancellationToken);
            }
            else
            {
                await CompleteAsync(database, candidate.Id, WorkToolCommandStatuses.Rejected, result.FailureCode ?? "worktool_rejected",
                    timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            await CompleteAsync(database, candidate.Id, WorkToolCommandStatuses.DeliveryUnknown, "delivery_outcome_unknown", null, cancellationToken);
        }
        return true;
    }

    private async Task<int> MarkAcceptedAsync(
        WechatRobotDbContext database,
        Guid id,
        string workToolMessageId,
        DateTime acceptedAtUtc,
        CancellationToken token)
    {
        var audit = await database.WorkToolOperationAudits.SingleOrDefaultAsync(
            item => item.Id == id &&
                    item.Status == WorkToolCommandStatuses.Dispatching &&
                    item.LeaseOwner == _owner,
            token);
        if (audit is null) return 0;
        audit.Status = WorkToolCommandStatuses.Accepted;
        audit.WorkToolCommandMessageId = workToolMessageId;
        audit.AcceptedAtUtc = acceptedAtUtc;
        audit.CompletedAtUtc = null;
        audit.Result = null;
        audit.LeaseOwner = null;
        audit.LeaseExpiresAtUtc = null;
        audit.Version++;
        try
        {
            return await database.SaveChangesAsync(token);
        }
        catch (DbUpdateConcurrencyException)
        {
            database.ChangeTracker.Clear();
            return 0;
        }
    }

    private async Task<int> CompleteAsync(
        WechatRobotDbContext database,
        Guid id,
        string status,
        string? result,
        DateTime? completedAtUtc,
        CancellationToken token)
    {
        var audit = await database.WorkToolOperationAudits.SingleOrDefaultAsync(
            item => item.Id == id &&
                    item.Status == WorkToolCommandStatuses.Dispatching &&
                    item.LeaseOwner == _owner,
            token);
        if (audit is null) return 0;
        audit.Status = status;
        audit.Result = result;
        audit.CompletedAtUtc = completedAtUtc;
        audit.LeaseOwner = null;
        audit.LeaseExpiresAtUtc = null;
        audit.Version++;
        try
        {
            return await database.SaveChangesAsync(token);
        }
        catch (DbUpdateConcurrencyException)
        {
            database.ChangeTracker.Clear();
            return 0;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!await ProcessOnceAsync(stoppingToken))
                await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken);
        }
    }
}
