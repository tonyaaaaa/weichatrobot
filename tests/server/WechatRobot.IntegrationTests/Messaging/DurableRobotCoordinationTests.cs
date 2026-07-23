using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using MySql.Data.MySqlClient;
using System.Net.Http.Json;
using WechatRobot.Application.Jobs;
using WechatRobot.Application.WorkTool;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.IntegrationTests.Infrastructure;
using WechatRobot.Worker.Jobs;
using WechatRobot.IntegrationTests.Models;

namespace WechatRobot.IntegrationTests.Messaging;

public sealed class DurableRobotCoordinationTests : IClassFixture<MySqlFixture>
{
    private readonly MySqlFixture _fixture;

    public DurableRobotCoordinationTests(MySqlFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Disabled_robot_blocks_queued_and_leased_commands_and_enable_resumes_fifo_once()
    {
        using var provider = CreateProvider();
        var now = TruncateToMicroseconds(DateTime.UtcNow.AddMinutes(1));
        var robot = await SeedRobotAndCommandsAsync(provider, 50, now, 2);
        await using var leaseScope = provider.CreateAsyncScope();
        var repository = leaseScope.ServiceProvider.GetRequiredService<IDurableJobRepository>();
        var leased = Assert.IsType<LeasedSendCommand>(await repository.LeaseNextSendCommandAsync("disable-worker", now,
            TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken));

        await using var factory = new RobotAdminFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        var disabled = await client.PutAsJsonAsync($"/api/admin/worktool/robots/{robot.Id:D}",
            new { robot.Name, robot.WorkToolRobotId, isEnabled = false }, TestContext.Current.CancellationToken);
        disabled.EnsureSuccessStatusCode();
        Assert.False(await repository.EnsureSendEnabledAsync(leased, TestContext.Current.CancellationToken));
        await using (var disabledScope = provider.CreateAsyncScope())
        {
            var db = disabledScope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            Assert.All(await db.SendCommands.Where(x => x.RobotConfigId == robot.Id).ToArrayAsync(TestContext.Current.CancellationToken),
                command => Assert.Equal("blocked", command.Status));
        }
        Assert.Null(await repository.LeaseNextSendCommandAsync("disabled-worker", now.AddMinutes(1), TimeSpan.FromMinutes(1),
            TestContext.Current.CancellationToken));

        var enabled = await client.PutAsJsonAsync($"/api/admin/worktool/robots/{robot.Id:D}",
            new { robot.Name, robot.WorkToolRobotId, isEnabled = true }, TestContext.Current.CancellationToken);
        enabled.EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync($"/api/admin/worktool/robots/{robot.Id:D}",
            new { robot.Name, robot.WorkToolRobotId, isEnabled = true }, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        await using var resumedScope = provider.CreateAsyncScope();
        var resumedRepository = resumedScope.ServiceProvider.GetRequiredService<IDurableJobRepository>();
        var first = Assert.IsType<LeasedSendCommand>(await resumedRepository.LeaseNextSendCommandAsync("resumed-worker", now.AddMinutes(2),
            TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken));
        await resumedRepository.CompleteSendCommandAsync(first, now.AddMinutes(2), TestContext.Current.CancellationToken);
        var second = Assert.IsType<LeasedSendCommand>(await resumedRepository.LeaseNextSendCommandAsync("resumed-worker", now.AddMinutes(2),
            TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken));
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task Disable_waits_for_an_in_flight_provider_call_before_returning_success()
    {
        var slowClient = new BlockingWorkToolClient();
        using var provider = CreateWorkerProvider(slowClient);
        var now = TruncateToMicroseconds(DateTime.UtcNow);
        var robot = await SeedRobotAndCommandsAsync(provider, 50, now, 1);
        var worker = new RobotSendWorker(provider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System,
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1));

        await using var factory = new RobotAdminFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        var send = worker.ProcessOnceAsync(TestContext.Current.CancellationToken);
        await slowClient.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(await IsRobotSendLockHeldAsync(robot.Id));

        var disable = client.PutAsJsonAsync($"/api/admin/worktool/robots/{robot.Id:D}",
            new { robot.Name, robot.WorkToolRobotId, isEnabled = false }, TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);
        Assert.False(disable.IsCompleted, "Disable must not report success while a provider send is still in flight.");

        slowClient.Complete();
        Assert.True(await send);
        (await disable).EnsureSuccessStatusCode();
        Assert.False(await IsRobotSendLockHeldAsync(robot.Id));
    }

    [Fact]
    public async Task Enqueue_and_enable_are_serialized_so_a_command_cannot_be_left_blocked_after_enable()
    {
        using var provider = CreateProvider();
        var now = TruncateToMicroseconds(DateTime.UtcNow);
        var robot = await SeedRobotAndCommandsAsync(provider, 50, now, 0);
        await using (var seedScope = provider.CreateAsyncScope())
        {
            var database = seedScope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            await database.RobotConfigs.Where(value => value.Id == robot.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.IsEnabled, false), TestContext.Current.CancellationToken);
        }

        await using var factory = new RobotAdminFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await using var blocker = new MySqlConnection(_fixture.ConnectionString);
        await blocker.OpenAsync(TestContext.Current.CancellationToken);
        await SetNamedLockAsync(blocker, robot.Id, acquire: true);
        try
        {
            await using var enqueueScope = provider.CreateAsyncScope();
            var repository = enqueueScope.ServiceProvider.GetRequiredService<IDurableJobRepository>();
            var enqueue = repository.EnqueueSendCommandAsync(new(robot.Id, robot.WorkToolRobotId, "Support", "queued",
                $"enable-race-{Guid.NewGuid():N}"), TestContext.Current.CancellationToken);
            var enable = client.PutAsJsonAsync($"/api/admin/worktool/robots/{robot.Id:D}",
                new { robot.Name, robot.WorkToolRobotId, isEnabled = true }, TestContext.Current.CancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);
            Assert.False(enqueue.IsCompleted);
            Assert.False(enable.IsCompleted);

            await SetNamedLockAsync(blocker, robot.Id, acquire: false);
            Assert.Equal(EnqueueSendCommandResult.Enqueued, await enqueue);
            (await enable).EnsureSuccessStatusCode();
        }
        finally
        {
            await SetNamedLockAsync(blocker, robot.Id, acquire: false);
        }

        await using var assertionScope = provider.CreateAsyncScope();
        var assertionDatabase = assertionScope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        Assert.Equal("pending", await assertionDatabase.SendCommands.Where(value => value.RobotConfigId == robot.Id)
            .Select(value => value.Status).SingleAsync(TestContext.Current.CancellationToken));
    }

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
        var worker = new RobotSendWorker(firstProvider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100));

        var send = worker.ProcessOnceAsync(TestContext.Current.CancellationToken);
        await slowClient.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(1500), TestContext.Current.CancellationToken);

        await using (var secondScope = secondProvider.CreateAsyncScope())
        {
            var secondRepository = secondScope.ServiceProvider.GetRequiredService<IDurableJobRepository>();
            Assert.Null(await secondRepository.LeaseNextSendCommandAsync("other-worker", DateTime.UtcNow, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
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

    private async Task<bool> IsRobotSendLockHeldAsync(Guid robotId)
    {
        await using var connection = new MySqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT IS_USED_LOCK(@name)";
        command.Parameters.AddWithValue("@name", MySqlRobotSendLock.NameFor(robotId));
        var result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return result is not null and not DBNull;
    }

    private static async Task SetNamedLockAsync(MySqlConnection connection, Guid robotId, bool acquire)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = acquire ? "SELECT GET_LOCK(@name, 5)" : "SELECT RELEASE_LOCK(@name)";
        command.Parameters.AddWithValue("@name", MySqlRobotSendLock.NameFor(robotId));
        _ = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }

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

        public Task<WorkToolSendResult> ExecuteGroupOperationAsync(WorkToolGroupOperationRequest request, CancellationToken cancellationToken) => Task.FromResult(WorkToolSendResult.Success());
        public Task<WorkToolSendResult> TestConnectionAsync(string workToolRobotId, CancellationToken cancellationToken) => Task.FromResult(WorkToolSendResult.Success());

        public void Complete() => _result.TrySetResult(WorkToolSendResult.Success());
    }

    private sealed class RobotAdminFactory : WebApplicationFactory<Program>
    {
        private readonly string connectionString;
        public RobotAdminFactory(string connectionString)
        {
            this.connectionString = connectionString;
            Environment.SetEnvironmentVariable("WECHATROBOT_MASTER_KEY_BASE64",
                Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
            Environment.SetEnvironmentVariable("Jwt__Issuer", "robot-tests"); Environment.SetEnvironmentVariable("Jwt__Audience", "robot-tests-api");
            Environment.SetEnvironmentVariable("Jwt__SigningKey", "robot-tests-signing-key-must-be-at-least-32-bytes");
            Environment.SetEnvironmentVariable("ConnectionStrings__WechatRobot", connectionString);
            Environment.SetEnvironmentVariable("Cors__AllowedOrigins__0", "https://admin.example.test");
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.DisableStartupMigrations();
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Issuer"] = "robot-tests", ["Jwt:Audience"] = "robot-tests-api",
                ["Jwt:SigningKey"] = "robot-tests-signing-key-must-be-at-least-32-bytes",
                ["ConnectionStrings:WechatRobot"] = connectionString, ["Cors:AllowedOrigins:0"] = "https://admin.example.test"
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<WechatRobotDbContext>>(); services.RemoveAll<WechatRobotDbContext>();
                services.AddDbContext<WechatRobotDbContext>(options => options.UseMySQL(connectionString));
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = "integration-admin"; options.DefaultChallengeScheme = "integration-admin";
                        options.DefaultForbidScheme = "integration-admin";
                    })
                    .AddScheme<AuthenticationSchemeOptions, IntegrationAdminAuthenticationHandler>("integration-admin", _ => { });
            });
        }
    }

    private static DateTime TruncateToMicroseconds(DateTime value) => new(value.Ticks - value.Ticks % 10, DateTimeKind.Utc);
}
