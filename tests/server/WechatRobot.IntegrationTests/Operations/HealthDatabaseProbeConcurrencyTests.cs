using System.Data.Common;
using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WechatRobot.Infrastructure.Health;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.IntegrationTests.Infrastructure;

namespace WechatRobot.IntegrationTests.Operations;

public sealed class HealthDatabaseProbeConcurrencyTests(MySqlFixture fixture) : IClassFixture<MySqlFixture>
{
    [Fact]
    public async Task Real_mysql_and_worker_heartbeat_probes_can_run_concurrently()
    {
        var observer = new DatabaseContextObserver();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ocr:BaseAddress"] = "http://127.0.0.1:18000/",
            ["Health:WorkerStaleAfterSeconds"] = "45"
        }).Build();
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(configuration)
            .AddSingleton(TimeProvider.System)
            .AddDbContextFactory<WechatRobotDbContext>(options =>
                options.UseMySQL(fixture.ConnectionString).AddInterceptors(observer))
            .AddWechatRobotHealth(configuration)
            .BuildServiceProvider();

        try
        {
            await using (var seedScope = services.CreateAsyncScope())
            {
                var database = seedScope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
                await database.Database.MigrateAsync(TestContext.Current.CancellationToken);
                database.WorkerHeartbeats.Add(new WorkerHeartbeatEntity
                {
                    Name = WorkerHeartbeatService.HeartbeatName,
                    LastSeenAtUtc = DateTime.UtcNow
                });
                await database.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            await using var probeScope = services.CreateAsyncScope();
            var probes = probeScope.ServiceProvider.GetServices<IComponentHealthProbe>().ToArray();
            var mysql = Assert.Single(probes.OfType<MySqlHealthProbe>());
            var heartbeat = Assert.Single(probes.OfType<WorkerHeartbeatHealthProbe>());

            observer.Reset();
            var results = await Task.WhenAll(
                mysql.CheckAsync(TestContext.Current.CancellationToken),
                heartbeat.CheckAsync(TestContext.Current.CancellationToken));

            Assert.All(results, result => Assert.Equal(ComponentHealthState.Healthy, result.State));
            Assert.Equal(2, observer.ContextIds.Distinct().Count());
        }
        finally
        {
            await services.DisposeAsync();
        }
    }

    private sealed class DatabaseContextObserver : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<DbContextId> _contextIds = new();
        public IReadOnlyCollection<DbContextId> ContextIds => _contextIds.ToArray();
        public void Reset()
        {
            while (_contextIds.TryDequeue(out _)) { }
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Record(eventData);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Record(eventData);
            return ValueTask.FromResult(result);
        }

        private void Record(CommandEventData eventData)
        {
            if (eventData.Context is not null) _contextIds.Enqueue(eventData.Context.ContextId);
        }
    }
}
