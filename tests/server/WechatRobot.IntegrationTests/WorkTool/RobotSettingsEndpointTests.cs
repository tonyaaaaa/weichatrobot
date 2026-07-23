using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.IntegrationTests.Models;

namespace WechatRobot.IntegrationTests.WorkTool;

public sealed class RobotSettingsEndpointTests : IClassFixture<ModelConfigurationApiFactory>
{
    private readonly ModelConfigurationApiFactory _factory;
    public RobotSettingsEndpointTests(ModelConfigurationApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Admin_can_read_and_update_safe_robot_settings_without_robot_identifier_or_callback_hash()
    {
        var robot = new RobotConfigEntity { Name = $"settings-{Guid.NewGuid():N}", WorkToolRobotId = $"secret-{Guid.NewGuid():N}", CallbackSecretHash = "secret-hash" };
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            db.RobotConfigs.Add(robot);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        using var client = _factory.CreateClient();

        var list = await client.GetStringAsync("/api/admin/robots", TestContext.Current.CancellationToken);
        Assert.DoesNotContain(robot.WorkToolRobotId, list, StringComparison.Ordinal);
        Assert.DoesNotContain(robot.CallbackSecretHash, list, StringComparison.Ordinal);
        var update = await client.PutAsJsonAsync($"/api/admin/robots/{robot.Id:D}", new
        {
            name = "安全机器人", isEnabled = false, sendRateLimitPerMinute = 40
        }, TestContext.Current.CancellationToken);

        update.EnsureSuccessStatusCode();
        using var verify = _factory.Services.CreateScope();
        var saved = await verify.ServiceProvider.GetRequiredService<WechatRobotDbContext>().RobotConfigs.AsNoTracking()
            .SingleAsync(item => item.Id == robot.Id, TestContext.Current.CancellationToken);
        Assert.Equal("安全机器人", saved.Name);
        Assert.False(saved.IsEnabled);
        Assert.Equal(40, saved.SendRateLimitPerMinute);
        Assert.StartsWith("secret-", saved.WorkToolRobotId, StringComparison.Ordinal);
    }
}
