using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WechatRobot.Application.WorkTool;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.IntegrationTests.Identity;

namespace WechatRobot.IntegrationTests.Operations;

public sealed class SendCommandOperationsEndpointTests
    : IClassFixture<UserAdministrationApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly UserAdministrationApiFactory _factory;

    public SendCommandOperationsEndpointTests(
        UserAdministrationApiFactory factory) =>
        _factory = factory;

    [Fact]
    public async Task List_requires_admin_and_returns_bounded_redacted_projection()
    {
        await _factory.ResetAsync();
        using var anonymous = _factory.CreateClient();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync(
                "/api/admin/operations/send-commands",
                TestContext.Current.CancellationToken)).StatusCode);

        var admin = await _factory.CreateUserAsync(
            "queue-admin@example.test",
            "Queue Admin",
            "Temporary1!Password",
            [SystemRoles.Admin]);
        var seeded = await SeedCommandAsync(
            WorkToolCommandStatuses.Pending,
            """{"groupName":"Support Group","text":"sensitive message body","workToolRobotId":"robot-sensitive-id"}""");
        using var client = _factory.CreateAdminClient(admin);

        var response = await client.GetAsync(
            $"/api/admin/operations/send-commands?robotConfigId={seeded.RobotId:D}&group=Support&status=pending&page=1&pageSize=20",
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain("sensitive message body", json, StringComparison.Ordinal);
        Assert.DoesNotContain("robot-sensitive-id", json, StringComparison.Ordinal);
        Assert.DoesNotContain("payloadJson", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("workToolCommandMessageId", json, StringComparison.OrdinalIgnoreCase);
        var page = JsonSerializer.Deserialize<SendCommandPage>(json, JsonOptions);
        var item = Assert.Single(page!.Items);
        Assert.Equal(seeded.CommandId, item.Id);
        Assert.Equal("Support Group", item.GroupName);
        Assert.Equal("Queue Robot", item.RobotName);
        Assert.Equal(WorkToolCommandStatuses.Pending, item.Status);
        Assert.Equal("sensitive message body".Length, item.MessageLength);
        Assert.Equal(1, page.Total);
    }

    [Theory]
    [InlineData(WorkToolCommandStatuses.Pending)]
    [InlineData(WorkToolCommandStatuses.Retrying)]
    public async Task Cancel_transitions_only_unsent_commands_and_writes_audit(
        string sourceStatus)
    {
        await _factory.ResetAsync();
        var admin = await _factory.CreateUserAsync(
            $"cancel-{sourceStatus}@example.test",
            "Queue Admin",
            "Temporary1!Password",
            [SystemRoles.Admin]);
        var seeded = await SeedCommandAsync(sourceStatus);
        using var client = _factory.CreateAdminClient(admin);

        var response = await client.PostAsJsonAsync(
            $"/api/admin/operations/send-commands/{seeded.CommandId:D}/cancel",
            new { expectedVersion = 0 },
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var command = await database.SendCommands.AsNoTracking().SingleAsync(
            item => item.Id == seeded.CommandId,
            TestContext.Current.CancellationToken);
        Assert.Equal("cancelled", command.Status);
        Assert.NotNull(command.CompletedAtUtc);
        var audit = await database.AdministrationAudits.AsNoTracking().SingleAsync(
            item => item.TargetId == seeded.CommandId.ToString("D"),
            TestContext.Current.CancellationToken);
        Assert.Equal("send-command.cancel", audit.Action);
        Assert.DoesNotContain("message body", audit.SanitizedDetailJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Acknowledge_unknown_records_resolution_without_claiming_success()
    {
        await _factory.ResetAsync();
        var admin = await _factory.CreateUserAsync(
            "unknown-admin@example.test",
            "Queue Admin",
            "Temporary1!Password",
            [SystemRoles.Admin]);
        var seeded = await SeedCommandAsync(WorkToolCommandStatuses.DeliveryUnknown);
        using var client = _factory.CreateAdminClient(admin);

        var response = await client.PostAsJsonAsync(
            $"/api/admin/operations/send-commands/{seeded.CommandId:D}/acknowledge-unknown",
            new { expectedVersion = 0 },
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        await using var scope = _factory.Services.CreateAsyncScope();
        var command = await scope.ServiceProvider
            .GetRequiredService<WechatRobotDbContext>()
            .SendCommands.AsNoTracking()
            .SingleAsync(
                item => item.Id == seeded.CommandId,
                TestContext.Current.CancellationToken);
        Assert.Equal("deliveryUnknownResolved", command.Status);
        Assert.NotEqual(WorkToolCommandStatuses.ExecutedSucceeded, command.Status);
        Assert.NotNull(command.CompletedAtUtc);
    }

    [Fact]
    public async Task Mutation_returns_conflict_when_version_or_status_changed()
    {
        await _factory.ResetAsync();
        var admin = await _factory.CreateUserAsync(
            "conflict-admin@example.test",
            "Queue Admin",
            "Temporary1!Password",
            [SystemRoles.Admin]);
        var pending = await SeedCommandAsync(WorkToolCommandStatuses.Pending);
        var dispatching = await SeedCommandAsync(WorkToolCommandStatuses.Dispatching);
        using var client = _factory.CreateAdminClient(admin);

        var stale = await client.PostAsJsonAsync(
            $"/api/admin/operations/send-commands/{pending.CommandId:D}/cancel",
            new { expectedVersion = 99 },
            TestContext.Current.CancellationToken);
        var unsafeState = await client.PostAsJsonAsync(
            $"/api/admin/operations/send-commands/{dispatching.CommandId:D}/cancel",
            new { expectedVersion = 0 },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, unsafeState.StatusCode);
    }

    private async Task<(Guid RobotId, Guid CommandId)> SeedCommandAsync(
        string status,
        string payload =
            """{"groupName":"Support Group","text":"message body"}""")
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var robot = await database.RobotConfigs.SingleOrDefaultAsync(
            item => item.Name == "Queue Robot",
            TestContext.Current.CancellationToken);
        if (robot is null)
        {
            robot = new RobotConfigEntity
            {
                Name = "Queue Robot",
                WorkToolRobotId = "robot-sensitive-id",
                EncryptedWorkToolRobotId = "encrypted-sensitive-id",
                CallbackSecretHash = "callback-sensitive-hash"
            };
            database.RobotConfigs.Add(robot);
        }

        var command = new SendCommandEntity
        {
            RobotConfigId = robot.Id,
            IdempotencyKey = $"queue-{Guid.NewGuid():N}",
            PayloadJson = payload,
            Status = status,
            ReconciliationReason = status == WorkToolCommandStatuses.DeliveryUnknown
                ? "delivery_outcome_unknown"
                : null,
            CreatedAtUtc = DateTime.UtcNow,
            NextAttemptAtUtc = DateTime.UtcNow
        };
        database.SendCommands.Add(command);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (robot.Id, command.Id);
    }

    private sealed record SendCommandPage(
        SendCommandItem[] Items,
        int Total,
        int Page,
        int PageSize);

    private sealed record SendCommandItem(
        Guid Id,
        string RobotName,
        string GroupName,
        string Status,
        int MessageLength);
}
