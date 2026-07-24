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

    public async Task<bool> ProcessOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await database.WorkToolOperationAudits
            .Where(item => item.Status == "ExternalInFlight" && item.LeaseExpiresAtUtc <= now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, "DeliveryUncertain")
                .SetProperty(item => item.Result, "External dispatch lease expired; reconciliation required.")
                .SetProperty(item => item.LeaseOwner, (string?)null)
                .SetProperty(item => item.LeaseExpiresAtUtc, (DateTime?)null)
                .SetProperty(item => item.Version, item => item.Version + 1), cancellationToken);

        var candidate = await database.WorkToolOperationAudits.AsNoTracking()
            .Where(item => item.Status == "Queued")
            .OrderBy(item => item.CreatedAtUtc).ThenBy(item => item.Id)
            .Select(item => new { item.Id, item.Version })
            .FirstOrDefaultAsync(cancellationToken);
        if (candidate is null) return false;
        var leased = await database.WorkToolOperationAudits
            .Where(item => item.Id == candidate.Id && item.Status == "Queued" && item.Version == candidate.Version)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, "ExternalInFlight")
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
            if (result.Succeeded)
            {
                await CompleteAsync(database, candidate.Id, "Succeeded", null, now, cancellationToken);
            }
            else if (result.DeliveryMayHaveOccurred)
            {
                await CompleteAsync(database, candidate.Id, "DeliveryUncertain", "Provider outcome is unknown; reconciliation required.", null, cancellationToken);
            }
            else
            {
                await CompleteAsync(database, candidate.Id, "Failed", "WorkTool rejected the command.", now, cancellationToken);
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            await CompleteAsync(database, candidate.Id, "DeliveryUncertain", "Provider outcome is unknown; reconciliation required.", null, cancellationToken);
        }
        return true;
    }

    private Task<int> CompleteAsync(WechatRobotDbContext database, Guid id, string status, string? result, DateTime? completedAtUtc, CancellationToken token) =>
        database.WorkToolOperationAudits
            .Where(item => item.Id == id && item.Status == "ExternalInFlight" && item.LeaseOwner == _owner)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, status)
                .SetProperty(item => item.Result, result)
                .SetProperty(item => item.CompletedAtUtc, completedAtUtc)
                .SetProperty(item => item.LeaseOwner, (string?)null)
                .SetProperty(item => item.LeaseExpiresAtUtc, (DateTime?)null)
                .SetProperty(item => item.Version, item => item.Version + 1), token);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!await ProcessOnceAsync(stoppingToken))
                await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken);
        }
    }
}
