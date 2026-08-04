using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Security;
using WechatRobot.Application.WorkTool;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.WorkTool;

namespace WechatRobot.Worker.Jobs;

public sealed class WorkToolGroupReconciliationWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider) : BackgroundService
{
    private const int MaximumAttempts = 5;

    public async Task<bool> ProcessOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var candidate = await database.WorkToolOperationAudits.AsNoTracking()
            .Where(item =>
                (item.ReconciliationStatus == "Pending"
                 || item.ReconciliationStatus == "Retrying")
                && (item.ReconciliationNextAttemptAtUtc == null
                    || item.ReconciliationNextAttemptAtUtc <= now))
            .OrderBy(item => item.ReconciliationNextAttemptAtUtc)
            .ThenBy(item => item.CreatedAtUtc)
            .Select(item => new { item.Id, item.Version })
            .FirstOrDefaultAsync(cancellationToken);
        if (candidate is null)
            return false;

        var claimed = await database.WorkToolOperationAudits
            .Where(item => item.Id == candidate.Id
                           && item.Version == candidate.Version
                           && (item.ReconciliationStatus == "Pending"
                               || item.ReconciliationStatus == "Retrying"))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.ReconciliationStatus, "Retrying")
                .SetProperty(item => item.ReconciliationAttemptCount,
                    item => item.ReconciliationAttemptCount + 1)
                .SetProperty(item => item.ReconciliationNextAttemptAtUtc,
                    now.AddMinutes(1))
                .SetProperty(item => item.Version, item => item.Version + 1),
                cancellationToken);
        if (claimed != 1)
            return true;

        var audit = await database.WorkToolOperationAudits.AsNoTracking()
            .SingleAsync(item => item.Id == candidate.Id, cancellationToken);
        try
        {
            var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
            var command = JsonSerializer.Deserialize<WorkToolGroupOperationRequest>(
                              protector.Unprotect(audit.EncryptedCommandJson!))
                          ?? throw new InvalidOperationException(
                              "Stored WorkTool group operation is invalid.");
            var targetName = command.Kind == WorkToolGroupOperationKind.Rename
                ? command.Value?.Trim()
                : command.GroupIdentifier.Trim();
            if (command.Kind is not (WorkToolGroupOperationKind.Create
                    or WorkToolGroupOperationKind.Rename)
                || string.IsNullOrWhiteSpace(targetName))
            {
                await CompleteAsync(database, audit.Id, "Failed", null,
                    cancellationToken);
                return true;
            }

            var importer = scope.ServiceProvider
                .GetRequiredService<WorkToolGroupImportService>();
            var discovered = await importer.DiscoverAsync(
                command.RobotConfigId,
                targetName,
                1,
                100,
                cancellationToken);
            var exact = discovered.Items
                .Where(item => item.GroupName.Trim() == targetName)
                .ToArray();
            if (exact.Length != 1)
            {
                await CompleteAsync(database, audit.Id, "NeedsConfirmation", null,
                    cancellationToken);
                return true;
            }

            Guid? groupId;
            if (command.Kind == WorkToolGroupOperationKind.Create)
            {
                var imported = await importer.ImportAsync(
                    command.RobotConfigId,
                    [new(targetName, "Available")],
                    "worktool-reconciliation",
                    cancellationToken);
                var result = imported.Single();
                if (result.Status != "Imported" || result.GroupProfileId is null)
                {
                    await CompleteAsync(database, audit.Id, "NeedsConfirmation",
                        null, cancellationToken);
                    return true;
                }
                groupId = result.GroupProfileId;
            }
            else
            {
                var localMatches = await database.GroupProfiles
                    .Where(group =>
                        group.RobotConfigId == command.RobotConfigId
                        && group.Name == command.GroupIdentifier.Trim())
                    .ToArrayAsync(cancellationToken);
                if (localMatches.Length != 1)
                {
                    await CompleteAsync(database, audit.Id, "NeedsConfirmation",
                        null, cancellationToken);
                    return true;
                }
                var existingTarget = await database.GroupProfiles.AsNoTracking()
                    .AnyAsync(group =>
                            group.RobotConfigId == command.RobotConfigId
                            && group.Id != localMatches[0].Id
                            && group.Name == targetName,
                        cancellationToken);
                if (existingTarget)
                {
                    await CompleteAsync(database, audit.Id, "NeedsConfirmation",
                        null, cancellationToken);
                    return true;
                }

                var group = localMatches[0];
                if (group.WorkToolGroupRemark == group.Name)
                    group.WorkToolGroupRemark = targetName;
                group.Name = targetName;
                group.ConfigurationVersion++;
                group.WorkToolLastSeenAtUtc = now;
                group.UpdatedAtUtc = now;
                await database.SaveChangesAsync(cancellationToken);
                groupId = group.Id;
            }

            await CompleteAsync(database, audit.Id, "Reconciled", groupId,
                cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            var status = audit.ReconciliationAttemptCount >= MaximumAttempts
                ? "Failed"
                : "Retrying";
            var nextAttempt = status == "Retrying"
                ? now.AddMinutes(Math.Min(
                    30,
                    1 << Math.Min(audit.ReconciliationAttemptCount, 4)))
                : (DateTime?)null;
            var retryAudit = await database.WorkToolOperationAudits
                .SingleOrDefaultAsync(item => item.Id == audit.Id &&
                                              item.ReconciliationStatus == "Retrying",
                    cancellationToken);
            if (retryAudit is not null)
            {
                retryAudit.ReconciliationStatus = status;
                retryAudit.ReconciliationNextAttemptAtUtc = nextAttempt;
                retryAudit.Version++;
                try
                {
                    await database.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateConcurrencyException)
                {
                    database.ChangeTracker.Clear();
                }
            }
        }
        return true;
    }

    private static async Task<int> CompleteAsync(
        WechatRobotDbContext database,
        Guid id,
        string status,
        Guid? groupProfileId,
        CancellationToken cancellationToken)
    {
        var audit = await database.WorkToolOperationAudits.SingleOrDefaultAsync(
            item => item.Id == id && item.ReconciliationStatus == "Retrying",
            cancellationToken);
        if (audit is null) return 0;
        audit.ReconciliationStatus = status;
        audit.ReconciliationNextAttemptAtUtc = null;
        audit.ReconciledGroupProfileId = groupProfileId;
        audit.Version++;
        try
        {
            return await database.SaveChangesAsync(cancellationToken);
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
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}
