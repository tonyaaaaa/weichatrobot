using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WechatRobot.Application.Security;
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

    [Fact]
    public async Task Full_administration_requires_disable_rotate_probe_enable_and_returns_only_safe_metadata()
    {
        var robot = new RobotConfigEntity
        {
            Name = $"governed-{Guid.NewGuid():N}",
            WorkToolRobotId = "legacy-redacted",
            CallbackSecretHash = "callback-secret-hash",
            IsEnabled = true,
            SendRateLimitPerMinute = 50
        };
        using (var scope = _factory.Services.CreateScope())
        {
            var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
            robot.EncryptedWorkToolRobotId = protector.Protect("plaintext-robot-id");
            var db = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            db.RobotConfigs.Add(robot);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        using var client = _factory.CreateClient();

        var listJson = await client.GetStringAsync(
            "/api/admin/worktool/robots",
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain("plaintext-robot-id", listJson, StringComparison.Ordinal);
        Assert.DoesNotContain(robot.CallbackSecretHash, listJson, StringComparison.Ordinal);
        using (var list = JsonDocument.Parse(listJson))
        {
            var item = list.RootElement.EnumerateArray()
                .Single(value => value.GetProperty("id").GetGuid() == robot.Id);
            Assert.True(item.GetProperty("hasWorkToolRobotId").GetBoolean());
            Assert.Equal(50, item.GetProperty("sendRateLimitPerMinute").GetInt32());
        }

        var rotateWhileEnabled = await client.PutAsJsonAsync(
            $"/api/admin/worktool/robots/{robot.Id:D}",
            new
            {
                name = robot.Name,
                workToolRobotId = "replacement-secret",
                isEnabled = true,
                sendRateLimitPerMinute = 40
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, rotateWhileEnabled.StatusCode);

        var disable = await client.PutAsJsonAsync(
            $"/api/admin/worktool/robots/{robot.Id:D}",
            new
            {
                name = robot.Name,
                isEnabled = false,
                sendRateLimitPerMinute = 40
            },
            TestContext.Current.CancellationToken);
        disable.EnsureSuccessStatusCode();

        var enableWithoutProbe = await client.PutAsJsonAsync(
            $"/api/admin/worktool/robots/{robot.Id:D}",
            new
            {
                name = robot.Name,
                isEnabled = true,
                sendRateLimitPerMinute = 40
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, enableWithoutProbe.StatusCode);

        var probe = await client.PostAsync(
            $"/api/admin/worktool/robots/{robot.Id:D}/test-connection",
            null,
            TestContext.Current.CancellationToken);
        probe.EnsureSuccessStatusCode();
        using var probeJson = JsonDocument.Parse(
            await probe.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var confirmation = probeJson.RootElement
            .GetProperty("enableConfirmationToken")
            .GetString();
        Assert.False(string.IsNullOrWhiteSpace(confirmation));

        var enable = await client.PutAsJsonAsync(
            $"/api/admin/worktool/robots/{robot.Id:D}",
            new
            {
                name = robot.Name,
                isEnabled = true,
                sendRateLimitPerMinute = 40,
                enableConfirmationToken = confirmation
            },
            TestContext.Current.CancellationToken);
        enable.EnsureSuccessStatusCode();

        using var verify = _factory.Services.CreateScope();
        var database = verify.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var saved = await database.RobotConfigs.AsNoTracking()
            .SingleAsync(item => item.Id == robot.Id, TestContext.Current.CancellationToken);
        Assert.True(saved.IsEnabled);
        Assert.Equal(40, saved.SendRateLimitPerMinute);
        var auditJson = string.Join(
            "\n",
            await database.AdministrationAudits.AsNoTracking()
                .Where(audit => audit.TargetId == robot.Id.ToString("D"))
                .Select(audit => audit.SanitizedDetailJson)
                .ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain("plaintext-robot-id", auditJson, StringComparison.Ordinal);
        Assert.DoesNotContain("replacement-secret", auditJson, StringComparison.Ordinal);
    }
}
