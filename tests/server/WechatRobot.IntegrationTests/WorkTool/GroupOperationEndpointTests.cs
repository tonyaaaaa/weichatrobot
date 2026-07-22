using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.IntegrationTests.Models;
using WechatRobot.Application.WorkTool;

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
        var robots = await client.GetStringAsync("/api/admin/worktool/robots", TestContext.Current.CancellationToken);
        Assert.DoesNotContain(robot.WorkToolRobotId, robots, StringComparison.Ordinal);
        Assert.Contains("robotReference", robots, StringComparison.Ordinal);
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
        Assert.All(audits, item => Assert.Equal(207, item.WorkToolCommandNumber));
        Assert.DoesNotContain(audits.Select(item => item.SanitizedRequestJson), value => value.Contains("private announcement", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Confirmation_is_bound_to_robot_and_replay_or_provider_failure_is_audited()
    {
        var first = new RobotConfigEntity { Name = $"robot-{Guid.NewGuid():N}", WorkToolRobotId = $"robot-{Guid.NewGuid():N}", CallbackSecretHash = "test" };
        var second = new RobotConfigEntity { Name = $"robot-{Guid.NewGuid():N}", WorkToolRobotId = $"robot-{Guid.NewGuid():N}", CallbackSecretHash = "test" };
        using (var scope = _factory.Services.CreateScope()) { var db = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>(); db.RobotConfigs.AddRange(first, second); await db.SaveChangesAsync(TestContext.Current.CancellationToken); }
        var recorder = _factory.Services.GetRequiredService<RecordingWorkToolClient>(); recorder.Reset();
        using var client = _factory.CreateClient();
        var preview = await PreviewAsync(client, first.Id, "UpdateAnnouncement", "secret announcement");
        var crossRobot = await client.PostAsJsonAsync("/api/admin/worktool/group-operations/execute", new { operation = Operation(second.Id, "UpdateAnnouncement", "secret announcement"), confirmationToken = preview }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, crossRobot.StatusCode); Assert.Equal(0, recorder.GroupOperationCalls);

        var success = await client.PostAsJsonAsync("/api/admin/worktool/group-operations/execute", new { operation = Operation(first.Id, "UpdateAnnouncement", "secret announcement"), confirmationToken = preview }, TestContext.Current.CancellationToken);
        success.EnsureSuccessStatusCode(); Assert.Equal(1, recorder.GroupOperationCalls);
        var replay = await client.PostAsJsonAsync("/api/admin/worktool/group-operations/execute", new { operation = Operation(first.Id, "UpdateAnnouncement", "secret announcement"), confirmationToken = preview }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode); Assert.Equal(1, recorder.GroupOperationCalls);

        var concurrentToken = await PreviewAsync(client, first.Id, "UpdateAnnouncement", "secret announcement");
        var firstAttempt = client.PostAsJsonAsync("/api/admin/worktool/group-operations/execute", new { operation = Operation(first.Id, "UpdateAnnouncement", "secret announcement"), confirmationToken = concurrentToken }, TestContext.Current.CancellationToken);
        var secondAttempt = client.PostAsJsonAsync("/api/admin/worktool/group-operations/execute", new { operation = Operation(first.Id, "UpdateAnnouncement", "secret announcement"), confirmationToken = concurrentToken }, TestContext.Current.CancellationToken);
        var attempts = await Task.WhenAll(firstAttempt, secondAttempt);
        Assert.Equal(1, attempts.Count(response => response.StatusCode == HttpStatusCode.OK)); Assert.Equal(1, attempts.Count(response => response.StatusCode == HttpStatusCode.BadRequest)); Assert.Equal(2, recorder.GroupOperationCalls);

        recorder.Reset(WorkToolSendResult.Failed("provider echoed secret announcement"));
        var failingToken = await PreviewAsync(client, first.Id, "UpdateAnnouncement", "secret announcement");
        var failed = await client.PostAsJsonAsync("/api/admin/worktool/group-operations/execute", new { operation = Operation(first.Id, "UpdateAnnouncement", "secret announcement"), confirmationToken = failingToken }, TestContext.Current.CancellationToken);
        failed.EnsureSuccessStatusCode();
        using var verifyScope = _factory.Services.CreateScope();
        var audits = await verifyScope.ServiceProvider.GetRequiredService<WechatRobotDbContext>().WorkToolOperationAudits.OrderBy(item => item.CreatedAtUtc).ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Contains(audits, item => item.Status == "Failed" && item.Result == "WorkTool rejected the command.");
        Assert.Contains(audits, item => item.Status == "Rejected");
        var auditResponse = await client.GetStringAsync("/api/admin/worktool/group-operations", TestContext.Current.CancellationToken);
        Assert.Contains("\"workToolCommandNumber\":207", auditResponse, StringComparison.Ordinal);
        Assert.DoesNotContain(audits.Select(item => $"{item.SanitizedRequestJson}{item.Result}"), value => value.Contains("secret announcement", StringComparison.Ordinal));
    }

    private static object Operation(Guid robotId, string kind, string value) => new { robotConfigId = robotId, kind, groupIdentifier = "group-1", memberIds = Array.Empty<string>(), value };
    private static async Task<string> PreviewAsync(HttpClient client, Guid robotId, string kind, string value)
    {
        var response = await client.PostAsJsonAsync("/api/admin/worktool/group-operations/preview", Operation(robotId, kind, value), TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode(); using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)); return document.RootElement.GetProperty("confirmationToken").GetString()!;
    }
}
