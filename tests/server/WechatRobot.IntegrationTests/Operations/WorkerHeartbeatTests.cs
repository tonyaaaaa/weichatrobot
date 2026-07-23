using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WechatRobot.Infrastructure.Health;
using WechatRobot.Infrastructure.Persistence;

namespace WechatRobot.IntegrationTests.Operations;

public sealed class WorkerHeartbeatTests
{
    [Fact]
    public async Task Successful_persistence_updates_database_and_fresh_ready_marker()
    {
        var readyFile = Path.Combine(Path.GetTempPath(), $"wechatrobot-heartbeat-{Guid.NewGuid():N}.ready");
        var databaseName = Guid.NewGuid().ToString();
        var services = new ServiceCollection()
            .AddDbContext<WechatRobotDbContext>(options => options.UseInMemoryDatabase(databaseName))
            .BuildServiceProvider();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Health:HeartbeatReadyFile"] = readyFile
        }).Build();
        var service = new WorkerHeartbeatService(
            services.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<WorkerHeartbeatService>.Instance,
            configuration);
        try
        {
            await service.WriteHeartbeatAsync(TestContext.Current.CancellationToken);
            await using var scope = services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            var heartbeat = await database.WorkerHeartbeats.SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(WorkerHeartbeatService.HeartbeatName, heartbeat.Name);
            Assert.True(DateTime.UtcNow - heartbeat.LastSeenAtUtc < TimeSpan.FromSeconds(5));
            Assert.True(File.Exists(readyFile));
        }
        finally
        {
            File.Delete(readyFile);
            await services.DisposeAsync();
        }
    }

    [Fact]
    public async Task Persistence_honors_cancellation_and_does_not_write_ready_marker()
    {
        var readyFile = Path.Combine(Path.GetTempPath(), $"wechatrobot-heartbeat-{Guid.NewGuid():N}.ready");
        var databaseName = Guid.NewGuid().ToString();
        var services = new ServiceCollection()
            .AddDbContext<WechatRobotDbContext>(options => options.UseInMemoryDatabase(databaseName))
            .BuildServiceProvider();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Health:HeartbeatReadyFile"] = readyFile
        }).Build();
        var service = new WorkerHeartbeatService(
            services.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<WorkerHeartbeatService>.Instance,
            configuration);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.WriteHeartbeatAsync(cancelled.Token));
            Assert.False(File.Exists(readyFile));
        }
        finally
        {
            File.Delete(readyFile);
            await services.DisposeAsync();
        }
    }
}
