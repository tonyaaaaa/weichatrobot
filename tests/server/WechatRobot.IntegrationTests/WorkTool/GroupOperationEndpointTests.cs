using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.IntegrationTests.Models;

namespace WechatRobot.IntegrationTests.WorkTool;

public sealed class GroupOperationEndpointTests : IClassFixture<ModelConfigurationApiFactory>
{
    private readonly ModelConfigurationApiFactory _factory;
    public GroupOperationEndpointTests(ModelConfigurationApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Changed_confirmation_payload_is_rejected_and_audited_without_raw_announcement()
    {
        var robot = new RobotConfigEntity { Name = $"robot-{Guid.NewGuid():N}", WorkToolRobotId = $"robot-{Guid.NewGuid():N}", CallbackSecretHash = "test" };
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            database.RobotConfigs.Add(robot);
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var initial = new { robotConfigId = robot.Id, kind = "UpdateAnnouncement", groupIdentifier = "group-1", memberIds = Array.Empty<string>(), value = "first private announcement" };
        using var client = _factory.CreateClient();
        var preview = await client.PostAsJsonAsync("/api/admin/worktool/group-operations/preview", initial, TestContext.Current.CancellationToken);
        preview.EnsureSuccessStatusCode();
        using var previewDocument = JsonDocument.Parse(await preview.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var confirmationToken = previewDocument.RootElement.GetProperty("confirmationToken").GetString();

        var execute = await client.PostAsJsonAsync("/api/admin/worktool/group-operations/execute", new
        {
            operation = new { robotConfigId = robot.Id, kind = "UpdateAnnouncement", groupIdentifier = "group-1", memberIds = Array.Empty<string>(), value = "changed private announcement" }, confirmationToken
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, execute.StatusCode);
        using var verifyScope = _factory.Services.CreateScope();
        var audits = await verifyScope.ServiceProvider.GetRequiredService<WechatRobotDbContext>().WorkToolOperationAudits.OrderBy(item => item.CreatedAtUtc).ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Contains(audits, item => item.Status == "Rejected");
        Assert.DoesNotContain(audits.Select(item => item.SanitizedRequestJson), value => value.Contains("private announcement", StringComparison.Ordinal));
    }
}
