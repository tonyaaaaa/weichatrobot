using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WechatRobot.Application.Jobs;
using WechatRobot.Application.WorkTool;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.IntegrationTests.Infrastructure;
using WechatRobot.Worker.Jobs;

namespace WechatRobot.IntegrationTests.Messaging;

public sealed class DurableRobotCoordinationTests : IClassFixture<MySqlFixture>
{
    private readonly MySqlFixture _fixture;

    public DurableRobotCoordinationTests(MySqlFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Independent_workers_share_a_durable_per_robot_rate_limit()
    {
        using var firstProvider = CreateProvider();
        using var secondProvider = CreateProvider();
        var now = TruncateToMicroseconds(DateTime.UtcNow.AddMinutes(1));
        var robot = await SeedRobotAndCommandsAsync(firstProvider, 1, now, 2);

        await using var firstScope = firstProvider.CreateAsyncScope();
        var firstRepository = firstScope.ServiceProvider.GetRequiredService<IDurableJobRepository>();
        var first = await firstRepository.LeaseNextSendCommandAsync("worker-one", now, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.NotNull(first);
        await firstRepository.CompleteSendCommandAsync(first!, now, TestContext.Current.CancellationToken);

        await using (var stateScope = firstProvider.CreateAsyncScope())
        {
            var stateDatabase = stateScope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            var state = await stateDatabase.RobotConfigs.AsNoTracking().SingleAsync(value => value.Id == robot.Id, TestContext.Current.CancellationToken);
            Assert.Equal(0m, state.SendRateTokens);
            Assert.Equal(now, state.SendRateUpdatedAtUtc);
        }

        await using var secondScope = secondProvider.CreateAsyncScope();
        var secondRepository = secondScope.ServiceProvider.GetRequiredService<IDurableJobRepository>();
        Assert.Null(await secondRepository.LeaseNextSendCommandAsync("worker-two", now, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

        var afterRefill = await secondRepository.LeaseNextSendCommandAsync("worker-two", now.AddMinutes(1), TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.NotNull(afterRefill);
        Assert.Equal(robot.Id, afterRefill!.RobotConfigId);
    }

    [Fact]
    public async Task Equal_created_timestamps_use_persisted_id_tiebreaker_and_one_robot_guard()
    {
        using var firstProvider = CreateProvider();
        using var secondProvider = CreateProvider();
        var now = TruncateToMicroseconds(DateTime.UtcNow.AddMinutes(1));
        var robot = await SeedRobotAndCommandsAsync(firstProvider, 50, now, 0);
        var firstId = new Guid("00000000-0000-0000-0000-000000000001");
        var secondId = new Guid("00000000-0000-0000-0000-000000000002");
        await using (var seedScope = firstProvider.CreateAsyncScope())
        {
            var database = seedScope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            database.SendCommands.AddRange(CreateCommand(firstId, robot.Id, now), CreateCommand(secondId, robot.Id, now));
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var firstScope = firstProvider.CreateAsyncScope();
        await using var secondScope = secondProvider.CreateAsyncScope();
        var firstRepository = firstScope.ServiceProvider.GetRequiredService<IDurableJobRepository>();
        var secondRepository = secondScope.ServiceProvider.GetRequiredService<IDurableJobRepository>();
        var first = await firstRepository.LeaseNextSendCommandAsync("worker-one", now, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var concurrent = await secondRepository.LeaseNextSendCommandAsync("worker-two", now, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.NotNull(first);
        Assert.Equal(firstId, first!.Id);
        Assert.Null(concurrent);
        await firstRepository.CompleteSendCommandAsync(first, now, TestContext.Current.CancellationToken);
        var second = await secondRepository.LeaseNextSendCommandAsync("worker-two", now, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.NotNull(second);
        Assert.Equal(secondId, second!.Id);
    }

    [Fact]
    public async Task Expired_worker_lease_is_reclaimed_for_at_least_once_redelivery()
    {
        using var firstProvider = CreateProvider();
        using var secondProvider = CreateProvider();
        var now = TruncateToMicroseconds(DateTime.UtcNow.AddMinutes(1));
        await SeedRobotAndCommandsAsync(firstProvider, 50, now, 1);
        await using var firstScope = firstProvider.CreateAsyncScope();
        var firstRepository = firstScope.ServiceProvider.GetRequiredService<IDurableJobRepository>();
        var first = await firstRepository.LeaseNextSendCommandAsync("crashed-worker", now, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.NotNull(first);

        await using var secondScope = secondProvider.CreateAsyncScope();
        var secondRepository = secondScope.ServiceProvider.GetRequiredService<IDurableJobRepository>();
        var reclaimed = await secondRepository.LeaseNextSendCommandAsync("recovery-worker", now.AddSeconds(11), TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.NotNull(reclaimed);
        Assert.Equal(first!.Id, reclaimed!.Id);
    }

    [Fact]
    public async Task Retry_waiting_earlier_command_blocks_later_command_until_it_completes()
    {
        using var firstProvider = CreateProvider();
        using var secondProvider = CreateProvider();
        var now = TruncateToMicroseconds(DateTime.UtcNow.AddMinutes(1));
        var robot = await SeedRobotAndCommandsAsync(firstProvider, 50, now, 0);
        var firstId = new Guid("00000000-0000-0000-0000-000000000011");
        var secondId = new Guid("00000000-0000-0000-0000-000000000012");
        await using (var seedScope = firstProvider.CreateAsyncScope())
        {
            var database = seedScope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            database.SendCommands.AddRange(CreateCommand(firstId, robot.Id, now), CreateCommand(secondId, robot.Id, now.AddMicroseconds(1)));
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var firstScope = firstProvider.CreateAsyncScope();
        await using var secondScope = secondProvider.CreateAsyncScope();
        var firstRepository = firstScope.ServiceProvider.GetRequiredService<IDurableJobRepository>();
        var secondRepository = secondScope.ServiceProvider.GetRequiredService<IDurableJobRepository>();
        var first = await firstRepository.LeaseNextSendCommandAsync("worker-one", now, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.NotNull(first);
        Assert.Equal(firstId, first!.Id);
        await firstRepository.FailSendCommandAsync(first, "retry", now, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Null(await secondRepository.LeaseNextSendCommandAsync("worker-two", now.AddSeconds(1), TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

        var retried = await firstRepository.LeaseNextSendCommandAsync("worker-one", now.AddSeconds(5), TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.NotNull(retried);
        Assert.Equal(firstId, retried!.Id);
        await firstRepository.CompleteSendCommandAsync(retried, now.AddSeconds(5), TestContext.Current.CancellationToken);

        var second = await secondRepository.LeaseNextSendCommandAsync("worker-two", now.AddSeconds(5), TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.NotNull(second);
        Assert.Equal(secondId, second!.Id);
    }

    [Fact]
    public async Task Slow_worktool_call_renews_command_and_robot_leases_before_provider_acceptance()
    {
        var slowClient = new BlockingWorkToolClient();
        using var firstProvider = CreateWorkerProvider(slowClient);
        using var secondProvider = CreateProvider();
        var now = TruncateToMicroseconds(DateTime.UtcNow);
        await SeedRobotAndCommandsAsync(firstProvider, 50, now, 1);
        var worker = new RobotSendWorker(firstProvider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(20));

        var send = worker.ProcessOnceAsync(TestContext.Current.CancellationToken);
        await slowClient.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);

        await using (var secondScope = secondProvider.CreateAsyncScope())
        {
            var secondRepository = secondScope.ServiceProvider.GetRequiredService<IDurableJobRepository>();
            Assert.Null(await secondRepository.LeaseNextSendCommandAsync("other-worker", DateTime.UtcNow, TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken));
        }

        slowClient.Complete();
        Assert.True(await send);
    }

    private async Task<RobotConfigEntity> SeedRobotAndCommandsAsync(ServiceProvider provider, int ratePerMinute, DateTime now, int count)
    {
        await using var scope = provider.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        await database.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await database.DeadLetters.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        await database.SendCommands.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        await database.RobotConfigs.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        var robot = new RobotConfigEntity
        {
            Name = $"coordination-{Guid.NewGuid():N}",
            WorkToolRobotId = $"coordination-{Guid.NewGuid():N}",
            CallbackSecretHash = "test",
            SendRateLimitPerMinute = ratePerMinute
        };
        database.RobotConfigs.Add(robot);
        for (var index = 0; index < count; index++)
        {
            database.SendCommands.Add(new SendCommandEntity
            {
                RobotConfigId = robot.Id,
                IdempotencyKey = $"coordination-{Guid.NewGuid():N}",
                PayloadJson = "{\"workToolRobotId\":\"robot\",\"groupName\":\"group\",\"text\":\"text\"}",
                CreatedAtUtc = now,
                NextAttemptAtUtc = now
            });
        }
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        return robot;
    }

    private static SendCommandEntity CreateCommand(Guid id, Guid robotId, DateTime now) => new()
    {
        Id = id,
        RobotConfigId = robotId,
        IdempotencyKey = $"coordination-{id:N}",
        PayloadJson = "{\"workToolRobotId\":\"robot\",\"groupName\":\"group\",\"text\":\"text\"}",
        CreatedAtUtc = now,
        NextAttemptAtUtc = now
    };

    private ServiceProvider CreateProvider() => new ServiceCollection()
        .AddDbContext<WechatRobotDbContext>(options => options.UseMySQL(_fixture.ConnectionString))
        .AddScoped<IDurableJobRepository, DurableJobRepository>()
        .BuildServiceProvider();

    private ServiceProvider CreateWorkerProvider(IWorkToolClient client) => new ServiceCollection()
        .AddDbContext<WechatRobotDbContext>(options => options.UseMySQL(_fixture.ConnectionString))
        .AddScoped<IDurableJobRepository, DurableJobRepository>()
        .AddSingleton(client)
        .AddSingleton(TimeProvider.System)
        .BuildServiceProvider();

    private sealed class BlockingWorkToolClient : IWorkToolClient
    {
        private readonly TaskCompletionSource<WorkToolSendResult> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<WorkToolSendResult> SendTextAsync(WorkToolSendRequest request, CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            return _result.Task.WaitAsync(cancellationToken);
        }

        public void Complete() => _result.TrySetResult(WorkToolSendResult.Success());
    }

    private static DateTime TruncateToMicroseconds(DateTime value) => new(value.Ticks - value.Ticks % 10, DateTimeKind.Utc);
}
