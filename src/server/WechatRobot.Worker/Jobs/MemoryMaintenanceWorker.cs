using Microsoft.EntityFrameworkCore;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.Application.Jobs;
using System.Security.Cryptography;
using System.Text;

namespace WechatRobot.Worker.Jobs;

public sealed class MemoryMaintenanceWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider) : BackgroundService
{
    private readonly string leaseOwner = $"memory-maintenance-{Environment.MachineName}-{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await ScheduleTodayAsync(stoppingToken);
            await ProcessOnceAsync(stoppingToken);
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }

    public async Task<bool> ProcessOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IDurableJobRepository>();
        var job = await jobs.LeaseNextJobAsync(
            "MaintainLongTermMemory",
            leaseOwner,
            timeProvider.GetUtcNow().UtcDateTime,
            TimeSpan.FromMinutes(5),
            cancellationToken);
        if (job is null) return false;
        try
        {
            await ExpireAsync(scope.ServiceProvider, cancellationToken);
            await jobs.CompleteJobAsync(
                job.Id,
                job.LeaseOwner,
                timeProvider.GetUtcNow().UtcDateTime,
                cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            await jobs.FailJobAsync(
                job,
                "memory_maintenance_failed",
                timeProvider.GetUtcNow().UtcDateTime,
                cancellationToken);
        }
        return true;
    }

    private async Task ScheduleTodayAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var date = DateOnly.FromDateTime(now);
        var id = DeterministicGuid($"memory-maintenance:{date:yyyy-MM-dd}");
        if (await database.DurableJobs.AnyAsync(x => x.Id == id, cancellationToken)) return;
        database.DurableJobs.Add(new DurableJobEntity
        {
            Id = id,
            JobType = "MaintainLongTermMemory",
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { date }),
            Status = "pending",
            AvailableAtUtc = now,
            NextAttemptAtUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        try { await database.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException) { database.ChangeTracker.Clear(); }
    }

    private async Task<int> ExpireAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var database = services.GetRequiredService<WechatRobotDbContext>();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var expired = await database.MemoryEntries
            .Where(x => x.Status == "active" && x.ExpiresAtUtc <= now)
            .ToArrayAsync(cancellationToken);
        foreach (var entry in expired)
        {
            entry.Status = "expired";
            entry.StatusVersion++;
            entry.Version++;
            entry.UpdatedAtUtc = now;
            database.MemoryAudits.Add(new MemoryAuditEntity
            {
                Action = "expire",
                ActorType = "system",
                TargetType = "entry",
                TargetId = entry.Id,
                OldStatus = "active",
                NewStatus = "expired",
                Version = entry.Version,
                ReasonCode = "memory_expired",
                CreatedAtUtc = now
            });
        }

        await database.SaveChangesAsync(cancellationToken);
        return expired.Length;
    }

    private static Guid DeterministicGuid(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(hash.AsSpan(0, 16));
    }
}
