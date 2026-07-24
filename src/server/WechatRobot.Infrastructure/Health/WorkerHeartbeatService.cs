using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using MySql.Data.MySqlClient;

namespace WechatRobot.Infrastructure.Health;

public sealed class WorkerHeartbeatService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<WorkerHeartbeatService> logger,
    IConfiguration configuration) : BackgroundService
{
    public const string HeartbeatName = "primary-worker";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10), timeProvider);
        do
        {
            try
            {
                await WriteHeartbeatAsync(stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Worker heartbeat persistence failed: {ErrorType}.", exception.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task WriteHeartbeatAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var heartbeat = await database.WorkerHeartbeats
            .SingleOrDefaultAsync(value => value.Name == HeartbeatName, cancellationToken);
        if (heartbeat is null)
        {
            database.WorkerHeartbeats.Add(new WorkerHeartbeatEntity { Name = HeartbeatName, LastSeenAtUtc = now });
            try { await database.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateException exception) when (exception.InnerException is MySqlException { Number: 1062 })
            {
                database.ChangeTracker.Clear();
            }
        }
        else
        {
            heartbeat.LastSeenAtUtc = now;
            await database.SaveChangesAsync(cancellationToken);
        }
        var readyFile = configuration["Health:HeartbeatReadyFile"];
        if (!string.IsNullOrWhiteSpace(readyFile))
        {
            await File.WriteAllTextAsync(readyFile, timeProvider.GetUtcNow().ToString("O"), cancellationToken);
        }
    }
}
