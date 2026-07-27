using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WechatRobot.Application.WorkTool;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.WorkTool;
using WechatRobot.IntegrationTests.Infrastructure;

namespace WechatRobot.IntegrationTests.WorkTool;

public sealed class WorkToolGlobalRateLimiterTests(MySqlFixture fixture)
    : IClassFixture<MySqlFixture>
{
    [Fact]
    public async Task Concurrent_callers_share_database_permits_at_one_second_intervals()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<WechatRobotDbContext>(
            options => options.UseMySQL(fixture.ConnectionString));
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<WechatRobotDbContext>>();
        await using (var database = await factory.CreateDbContextAsync(
                         TestContext.Current.CancellationToken))
        {
            await database.Database.MigrateAsync(TestContext.Current.CancellationToken);
            await database.WorkToolRateLimitBuckets.ExecuteDeleteAsync(
                TestContext.Current.CancellationToken);
        }

        var limiter = new MySqlWorkToolGlobalRateLimiter(
            factory,
            Options.Create(new WorkToolRateLimitOptions
            {
                ScopeKey = $"test-{Guid.NewGuid():N}",
                RequestsPerMinute = 60,
                MaxWaitSeconds = 10
            }));
        var stopwatch = Stopwatch.StartNew();

        WorkToolRateLimitLease[] leases = await Task.WhenAll(
            Enumerable.Range(0, 3).Select(_ => limiter.AcquireAsync(
                "integration-test",
                TestContext.Current.CancellationToken)));

        stopwatch.Stop();
        Assert.All(leases, lease => Assert.True(lease.Acquired));
        Assert.True(
            stopwatch.Elapsed >= TimeSpan.FromMilliseconds(1900),
            $"Three permits completed in {stopwatch.Elapsed}.");
    }
}
