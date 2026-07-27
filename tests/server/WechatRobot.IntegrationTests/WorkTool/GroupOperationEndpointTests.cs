using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
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
    public async Task Existing_group_registration_uses_name_and_optional_WorkTool_remark_without_a_fabricated_external_id()
    {
        var robot = new RobotConfigEntity
        {
            Name = $"robot-{Guid.NewGuid():N}",
            WorkToolRobotId = $"robot-{Guid.NewGuid():N}",
            CallbackSecretHash = "test"
        };
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            database.RobotConfigs.Add(robot);
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var client = _factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/admin/worktool/groups/register", new
        {
            robotConfigId = robot.Id,
            name = "技术支持群",
            workToolGroupRemark = "support-east",
            manualInvitationCompleted = true
        }, TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("技术支持群", document.RootElement.GetProperty("name").GetString());
        Assert.Equal("support-east", document.RootElement.GetProperty("workToolGroupRemark").GetString());
        Assert.False(document.RootElement.TryGetProperty("externalGroupId", out _));

        using var verifyScope = _factory.Services.CreateScope();
        var saved = await verifyScope.ServiceProvider.GetRequiredService<WechatRobotDbContext>().GroupProfiles
            .SingleAsync(group => group.RobotConfigId == robot.Id && group.Name == "技术支持群",
                TestContext.Current.CancellationToken);
        Assert.Null(saved.ExternalGroupId);
        Assert.Equal("support-east", saved.WorkToolGroupRemark);
    }

    [Fact]
    public async Task Group_list_returns_display_metadata_with_the_backend_generated_id()
    {
        var robot = new RobotConfigEntity
        {
            Name = $"robot-{Guid.NewGuid():N}",
            WorkToolRobotId = $"robot-{Guid.NewGuid():N}",
            CallbackSecretHash = "test"
        };
        var updatedAt = new DateTime(2026, 7, 25, 1, 2, 3, DateTimeKind.Utc);
        var group = new GroupProfileEntity
        {
            RobotConfigId = robot.Id,
            Name = "技术群",
            WorkToolGroupRemark = "tech-east",
            IsEnabled = false,
            UpdatedAtUtc = updatedAt
        };
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            database.AddRange(robot, group);
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var client = _factory.CreateClient();
        var items = await client.GetFromJsonAsync<JsonElement[]>(
            "/api/admin/worktool/groups",
            TestContext.Current.CancellationToken);
        var item = items!.Single(value => value.GetProperty("id").GetGuid() == group.Id);

        Assert.Equal(robot.Id, item.GetProperty("robotConfigId").GetGuid());
        Assert.Equal(robot.Name, item.GetProperty("robotName").GetString());
        Assert.Equal("技术群", item.GetProperty("name").GetString());
        Assert.Equal("tech-east", item.GetProperty("workToolGroupRemark").GetString());
        Assert.False(item.GetProperty("isEnabled").GetBoolean());
        Assert.Equal(updatedAt, item.GetProperty("updatedAtUtc").GetDateTime());
    }

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
        var initial = new { robotConfigId = robot.Id, kind = "UpdateAnnouncement", groupIdentifier = "group-1", memberDisplayNames = Array.Empty<string>(), value = "first private announcement" };
        using var client = _factory.CreateClient();
        var robots = await client.GetStringAsync("/api/admin/worktool/robots", TestContext.Current.CancellationToken);
        Assert.DoesNotContain(robot.WorkToolRobotId, robots, StringComparison.Ordinal);
        Assert.Contains("robotReference", robots, StringComparison.Ordinal);
        var preview = await client.PostAsJsonAsync("/api/admin/worktool/group-operations/preview", initial, TestContext.Current.CancellationToken);
        preview.EnsureSuccessStatusCode();
        using var previewDocument = JsonDocument.Parse(await preview.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var confirmationToken = previewDocument.RootElement.GetProperty("confirmationToken").GetString();
        using var beforeExecuteScope = _factory.Services.CreateScope();
        var existingAuditIds = await beforeExecuteScope.ServiceProvider.GetRequiredService<WechatRobotDbContext>()
            .WorkToolOperationAudits
            .Select(item => item.Id)
            .ToArrayAsync(TestContext.Current.CancellationToken);

        var execute = await client.PostAsJsonAsync("/api/admin/worktool/group-operations/execute", new
        {
            operation = new { robotConfigId = robot.Id, kind = "UpdateAnnouncement", groupIdentifier = "group-1", memberDisplayNames = Array.Empty<string>(), value = "changed private announcement" }, confirmationToken
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, execute.StatusCode);
        using var verifyScope = _factory.Services.CreateScope();
        var audits = await verifyScope.ServiceProvider.GetRequiredService<WechatRobotDbContext>().WorkToolOperationAudits
            .Where(item => !existingAuditIds.Contains(item.Id))
            .OrderBy(item => item.CreatedAtUtc)
            .ToArrayAsync(TestContext.Current.CancellationToken);
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
        Assert.Equal(HttpStatusCode.Accepted, success.StatusCode); Assert.Equal(0, recorder.GroupOperationCalls);
        using var successDocument = JsonDocument.Parse(await success.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var successAuditId = successDocument.RootElement.GetProperty("auditId").GetGuid();
        Assert.NotEqual(Guid.Empty, successAuditId);
        var replay = await client.PostAsJsonAsync("/api/admin/worktool/group-operations/execute", new { operation = Operation(first.Id, "UpdateAnnouncement", "secret announcement"), confirmationToken = preview }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode); Assert.Equal(0, recorder.GroupOperationCalls);

        var concurrentToken = await PreviewAsync(client, first.Id, "UpdateAnnouncement", "secret announcement");
        var firstAttempt = client.PostAsJsonAsync("/api/admin/worktool/group-operations/execute", new { operation = Operation(first.Id, "UpdateAnnouncement", "secret announcement"), confirmationToken = concurrentToken }, TestContext.Current.CancellationToken);
        var secondAttempt = client.PostAsJsonAsync("/api/admin/worktool/group-operations/execute", new { operation = Operation(first.Id, "UpdateAnnouncement", "secret announcement"), confirmationToken = concurrentToken }, TestContext.Current.CancellationToken);
        var attempts = await Task.WhenAll(firstAttempt, secondAttempt);
        Assert.Equal(1, attempts.Count(response => response.StatusCode == HttpStatusCode.Accepted)); Assert.Equal(1, attempts.Count(response => response.StatusCode == HttpStatusCode.BadRequest)); Assert.Equal(0, recorder.GroupOperationCalls);

        recorder.Reset(WorkToolSendResult.Failed("provider echoed secret announcement"));
        var failingToken = await PreviewAsync(client, first.Id, "UpdateAnnouncement", "secret announcement");
        var failed = await client.PostAsJsonAsync("/api/admin/worktool/group-operations/execute", new { operation = Operation(first.Id, "UpdateAnnouncement", "secret announcement"), confirmationToken = failingToken }, TestContext.Current.CancellationToken);
        failed.EnsureSuccessStatusCode();
        using var verifyScope = _factory.Services.CreateScope();
        var audits = await verifyScope.ServiceProvider.GetRequiredService<WechatRobotDbContext>().WorkToolOperationAudits.OrderBy(item => item.CreatedAtUtc).ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Contains(audits, item => item.Id == successAuditId && item.Status == WorkToolCommandStatuses.Queued
            && item.OperatorName == "model-admin" && item.WorkToolCommandNumber == 207);
        Assert.Contains(audits, item => item.Status == WorkToolCommandStatuses.Queued && item.EncryptedCommandJson != null);
        Assert.Contains(audits, item => item.Status == "Rejected");
        var auditResponse = await client.GetStringAsync("/api/admin/worktool/group-operations", TestContext.Current.CancellationToken);
        Assert.Contains("\"workToolCommandNumber\":207", auditResponse, StringComparison.Ordinal);
        Assert.DoesNotContain(audits.Select(item => $"{item.SanitizedRequestJson}{item.Result}"), value => value.Contains("secret announcement", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Execute_rejects_an_authenticated_admin_without_a_stable_operator_identity()
    {
        var robot = new RobotConfigEntity
        {
            Name = $"robot-{Guid.NewGuid():N}", WorkToolRobotId = $"robot-{Guid.NewGuid():N}",
            CallbackSecretHash = "test"
        };
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            database.RobotConfigs.Add(robot);
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var recorder = _factory.Services.GetRequiredService<RecordingWorkToolClient>();
        recorder.Reset();
        using var client = _factory.CreateClient();
        var confirmationToken = await PreviewAsync(client, robot.Id, "UpdateAnnouncement", "private announcement");
        client.DefaultRequestHeaders.Add("X-Test-No-Name", "1");

        var response = await client.PostAsJsonAsync("/api/admin/worktool/group-operations/execute",
            new { operation = Operation(robot.Id, "UpdateAnnouncement", "private announcement"), confirmationToken },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, recorder.GroupOperationCalls);
        using var verifyScope = _factory.Services.CreateScope();
        var audits = await verifyScope.ServiceProvider.GetRequiredService<WechatRobotDbContext>().WorkToolOperationAudits
            .Where(item => item.OperatorName == "unknown").ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Empty(audits);
    }

    [Fact]
    public async Task Create_and_rename_return_distinct_exact_redacted_audit_records()
    {
        var robot = new RobotConfigEntity
        {
            Name = $"robot-{Guid.NewGuid():N}", WorkToolRobotId = $"provider-secret-{Guid.NewGuid():N}",
            CallbackSecretHash = "callback-secret"
        };
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            database.RobotConfigs.Add(robot);
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        _factory.Services.GetRequiredService<RecordingWorkToolClient>().Reset();
        using var client = _factory.CreateClient();
        var create = new
        {
            robotConfigId = robot.Id, kind = "Create", groupIdentifier = "group-exact",
            memberDisplayNames = new[] { "member-b", "member-a" }, value = "private announcement"
        };
        var rename = new
        {
            robotConfigId = robot.Id, kind = "Rename", groupIdentifier = "group-exact",
            memberDisplayNames = Array.Empty<string>(), value = "renamed exact"
        };

        var createAuditId = await ExecuteAsync(client, create);
        var renameAuditId = await ExecuteAsync(client, rename);

        using var verifyScope = _factory.Services.CreateScope();
        var ids = new[] { createAuditId, renameAuditId };
        var audits = await verifyScope.ServiceProvider.GetRequiredService<WechatRobotDbContext>().WorkToolOperationAudits
            .Where(item => ids.Contains(item.Id)).ToArrayAsync(TestContext.Current.CancellationToken);
        var createAudit = Assert.Single(audits, item => item.Id == createAuditId);
        var renameAudit = Assert.Single(audits, item => item.Id == renameAuditId);
        AssertAudit(createAudit, "Create", 206, robot.Id, "group-exact", 2,
            Hash("member-a\nmember-b"), "private announcement");
        AssertAudit(renameAudit, "Rename", 207, robot.Id, "group-exact", 0,
            Hash(string.Empty), "renamed exact");
        Assert.DoesNotContain(robot.WorkToolRobotId, string.Join('|', audits.Select(item => item.SanitizedRequestJson)), StringComparison.Ordinal);
        Assert.DoesNotContain("private announcement", createAudit.SanitizedRequestJson, StringComparison.Ordinal);
        Assert.DoesNotContain("renamed exact", renameAudit.SanitizedRequestJson, StringComparison.Ordinal);
    }

    private static object Operation(Guid robotId, string kind, string value) => new { robotConfigId = robotId, kind, groupIdentifier = "group-1", memberDisplayNames = Array.Empty<string>(), value };
    private static async Task<string> PreviewAsync(HttpClient client, Guid robotId, string kind, string value)
    {
        var response = await client.PostAsJsonAsync("/api/admin/worktool/group-operations/preview", Operation(robotId, kind, value), TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode(); using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)); return document.RootElement.GetProperty("confirmationToken").GetString()!;
    }
    private static async Task<Guid> ExecuteAsync(HttpClient client, object operation)
    {
        using var preview = await client.PostAsJsonAsync("/api/admin/worktool/group-operations/preview", operation, TestContext.Current.CancellationToken);
        preview.EnsureSuccessStatusCode();
        using var previewDocument = JsonDocument.Parse(await preview.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var confirmationToken = previewDocument.RootElement.GetProperty("confirmationToken").GetString();
        using var execute = await client.PostAsJsonAsync("/api/admin/worktool/group-operations/execute",
            new { operation, confirmationToken }, TestContext.Current.CancellationToken);
        execute.EnsureSuccessStatusCode();
        using var executeDocument = JsonDocument.Parse(await execute.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain("provider-secret", executeDocument.RootElement.GetRawText(), StringComparison.Ordinal);
        return executeDocument.RootElement.GetProperty("auditId").GetGuid();
    }
    private static void AssertAudit(WorkToolOperationAuditEntity audit, string operation, int command, Guid robotId,
        string groupIdentifier, int memberCount, string memberDisplayNamesHash, string value)
    {
        Assert.Equal(WorkToolCommandStatuses.Queued, audit.Status);
        Assert.Equal("model-admin", audit.OperatorName);
        Assert.Equal(operation, audit.Operation);
        Assert.Equal(command, audit.WorkToolCommandNumber);
        using var request = JsonDocument.Parse(audit.SanitizedRequestJson);
        var root = request.RootElement;
        Assert.Equal(robotId, root.GetProperty("robotConfigId").GetGuid());
        Assert.Equal(operation, root.GetProperty("kind").GetString());
        Assert.Equal(groupIdentifier, root.GetProperty("groupIdentifier").GetString());
        Assert.Equal(memberCount, root.GetProperty("memberCount").GetInt32());
        Assert.Equal(memberDisplayNamesHash, root.GetProperty("memberDisplayNamesHash").GetString());
        Assert.Equal(value.Length, root.GetProperty("valueLength").GetInt32());
        Assert.Equal(Hash(value), root.GetProperty("valueHash").GetString());
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
