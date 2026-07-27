using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.IntegrationTests.Models;

namespace WechatRobot.IntegrationTests.Groups;

public sealed class GroupHumanAgentEndpointTests(
    ModelConfigurationApiFactory factory)
    : IClassFixture<ModelConfigurationApiFactory>
{
    [Fact]
    public async Task Group_agent_update_is_rejected_until_a_verified_member_snapshot_exists()
    {
        var robot = new RobotConfigEntity
        {
            Name = $"agent-gate-{Guid.NewGuid():N}",
            WorkToolRobotId = $"legacy-{Guid.NewGuid():N}",
            CallbackSecretHash = "test"
        };
        var group = new GroupProfileEntity
        {
            RobotConfigId = robot.Id,
            Name = $"agent-group-{Guid.NewGuid():N}"
        };
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            database.AddRange(robot, group);
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(
            $"/api/groups/{group.Id:D}/human-agents",
            new { userIds = Array.Empty<Guid>() },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>(
            TestContext.Current.CancellationToken);
        Assert.Equal("worktool-member-snapshot-unavailable", problem?["error"]);
        using var verify = factory.Services.CreateScope();
        Assert.False(await verify.ServiceProvider.GetRequiredService<WechatRobotDbContext>()
            .GroupHumanAgents.AnyAsync(
                agent => agent.GroupProfileId == group.Id && agent.IsEnabled,
                TestContext.Current.CancellationToken));
    }
}
