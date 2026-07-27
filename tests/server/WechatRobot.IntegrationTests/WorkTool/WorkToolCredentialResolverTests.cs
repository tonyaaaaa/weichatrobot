using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Security;
using WechatRobot.Application.WorkTool;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.Infrastructure.WorkTool;

namespace WechatRobot.IntegrationTests.WorkTool;

public sealed class WorkToolCredentialResolverTests
{
    [Fact]
    public async Task Configured_resolution_allows_disabled_robot_but_enabled_resolution_rejects_it()
    {
        var options = new DbContextOptionsBuilder<WechatRobotDbContext>()
            .UseInMemoryDatabase($"worktool-credentials-{Guid.NewGuid():N}")
            .Options;
        await using var database = new WechatRobotDbContext(options);
        var robot = new RobotConfigEntity
        {
            Name = "disabled-configured",
            IsEnabled = false,
            EncryptedWorkToolRobotId = "protected-robot-id"
        };
        database.RobotConfigs.Add(robot);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        var resolver = new WorkToolCredentialResolver(database, new PassThroughProtector());

        var configured = await resolver.ResolveConfiguredRobotIdAsync(
            robot.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal("protected-robot-id", configured);
        await Assert.ThrowsAsync<WorkToolCredentialUnavailableException>(() =>
            resolver.ResolveEnabledRobotIdAsync(
                robot.Id,
                TestContext.Current.CancellationToken));
    }

    private sealed class PassThroughProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }
}
