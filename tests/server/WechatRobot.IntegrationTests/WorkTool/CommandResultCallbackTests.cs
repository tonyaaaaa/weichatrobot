using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WechatRobot.Application.Security;
using WechatRobot.Application.WorkTool;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.IntegrationTests.Infrastructure;

namespace WechatRobot.IntegrationTests.WorkTool;

public sealed class CommandResultCallbackTests : IClassFixture<MySqlFixture>
{
    private readonly MySqlFixture fixture;

    public CommandResultCallbackTests(MySqlFixture fixture) => this.fixture = fixture;

    public static TheoryData<int, string[], string[], string> FinalStatusCases => new()
    {
        { 0, ["Alice"], [], WorkToolCommandStatuses.ExecutedSucceeded },
        { 0, ["Alice"], ["Bob"], WorkToolCommandStatuses.ExecutedPartially },
        { 0, [], ["Bob"], WorkToolCommandStatuses.ExecutedFailed },
        { 1001, ["Alice"], [], WorkToolCommandStatuses.ExecutedFailed }
    };

    [Theory]
    [MemberData(nameof(FinalStatusCases))]
    public async Task Authenticated_send_result_maps_to_a_durable_terminal_status(
        int errorCode,
        string[] successList,
        string[] failList,
        string expectedStatus)
    {
        await using var factory = new CallbackApiFactory(fixture.ConnectionString);
        var seeded = await SeedSendAsync(factory, WorkToolCommandStatuses.Accepted);
        using var client = factory.CreateClient();

        var response = await PostAsync(
            client,
            seeded,
            new { messageId = seeded.MessageId, errorCode, type = 1, successList, failList });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"code":0,"message":"accepted"}""", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var command = await database.SendCommands.AsNoTracking().SingleAsync(
            item => item.Id == seeded.TargetId,
            TestContext.Current.CancellationToken);
        Assert.Equal(expectedStatus, command.Status);
        Assert.Equal(errorCode, command.WorkToolResultCode);
        Assert.NotNull(command.WorkToolResultAtUtc);
        Assert.Equal(successList, Deserialize(command.WorkToolSuccessListJson));
        Assert.Equal(failList, Deserialize(command.WorkToolFailListJson));
    }

    [Fact]
    public async Task Group_operation_result_is_reconciled_without_exposing_display_names()
    {
        await using var factory = new CallbackApiFactory(fixture.ConnectionString);
        var seeded = await SeedOperationAsync(factory, WorkToolCommandStatuses.Accepted);
        using var client = factory.CreateClient();

        var response = await PostAsync(
            client,
            seeded,
            new { messageId = seeded.MessageId, errorCode = 0, type = 1, successList = new[] { "Alice" }, failList = Array.Empty<string>() });
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("Alice", body, StringComparison.Ordinal);
        await using var scope = factory.Services.CreateAsyncScope();
        var audit = await scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>().WorkToolOperationAudits.AsNoTracking()
            .SingleAsync(item => item.Id == seeded.TargetId, TestContext.Current.CancellationToken);
        Assert.Equal(WorkToolCommandStatuses.ExecutedSucceeded, audit.Status);
        Assert.Equal(["Alice"], Deserialize(audit.WorkToolSuccessListJson));
    }

    [Fact]
    public async Task Duplicate_is_idempotent_and_conflicting_terminal_result_preserves_the_first_result()
    {
        await using var factory = new CallbackApiFactory(fixture.ConnectionString);
        var seeded = await SeedSendAsync(factory, WorkToolCommandStatuses.Accepted);
        using var client = factory.CreateClient();
        var succeeded = new { messageId = seeded.MessageId, errorCode = 0, type = 1, successList = new[] { "Alice" }, failList = Array.Empty<string>() };

        (await PostAsync(client, seeded, succeeded)).EnsureSuccessStatusCode();
        (await PostAsync(client, seeded, succeeded)).EnsureSuccessStatusCode();
        (await PostAsync(client, seeded, new
        {
            messageId = seeded.MessageId,
            errorCode = 1001,
            type = 1,
            successList = Array.Empty<string>(),
            failList = new[] { "Alice" }
        })).EnsureSuccessStatusCode();

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var command = await database.SendCommands.AsNoTracking().SingleAsync(item => item.Id == seeded.TargetId, TestContext.Current.CancellationToken);
        Assert.Equal(WorkToolCommandStatuses.ExecutedSucceeded, command.Status);
        Assert.Equal(0, command.WorkToolResultCode);
        Assert.Equal(1, await database.AdministrationAudits.AsNoTracking().CountAsync(
            audit => audit.Action == "worktool.command-result.conflict" &&
                     audit.TargetId == seeded.TargetId.ToString("D"),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Parallel_identical_results_converge_to_one_terminal_result_without_conflict()
    {
        await using var factory = new CallbackApiFactory(fixture.ConnectionString);
        var seeded = await SeedSendAsync(factory, WorkToolCommandStatuses.Accepted);
        using var firstClient = factory.CreateClient();
        using var secondClient = factory.CreateClient();
        var payload = new
        {
            messageId = seeded.MessageId,
            errorCode = 0,
            type = 1,
            successList = new[] { "Alice" },
            failList = Array.Empty<string>()
        };

        var responses = await Task.WhenAll(
            PostAsync(firstClient, seeded, payload),
            PostAsync(secondClient, seeded, payload));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var command = await database.SendCommands.AsNoTracking().SingleAsync(item => item.Id == seeded.TargetId, TestContext.Current.CancellationToken);
        Assert.Equal(WorkToolCommandStatuses.ExecutedSucceeded, command.Status);
        Assert.Equal(0, await database.AdministrationAudits.AsNoTracking().CountAsync(
            audit => audit.Action == "worktool.command-result.conflict" &&
                     audit.TargetId == seeded.TargetId.ToString("D"),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Late_result_after_result_timeout_is_audited_without_reversing_the_timeout()
    {
        await using var factory = new CallbackApiFactory(fixture.ConnectionString);
        var seeded = await SeedSendAsync(factory, WorkToolCommandStatuses.ResultTimeout);
        using var client = factory.CreateClient();

        (await PostAsync(client, seeded, new
        {
            messageId = seeded.MessageId,
            errorCode = 0,
            type = 1,
            successList = new[] { "Alice" },
            failList = Array.Empty<string>()
        })).EnsureSuccessStatusCode();

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var command = await database.SendCommands.AsNoTracking().SingleAsync(item => item.Id == seeded.TargetId, TestContext.Current.CancellationToken);
        Assert.Equal(WorkToolCommandStatuses.ResultTimeout, command.Status);
        Assert.Null(command.WorkToolResultAtUtc);
        Assert.Equal(1, await database.AdministrationAudits.AsNoTracking().CountAsync(
            audit => audit.Action == "worktool.command-result.conflict" &&
                     audit.TargetId == seeded.TargetId.ToString("D"),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Unknown_message_id_is_acknowledged_and_recorded_as_a_sanitized_orphan()
    {
        await using var factory = new CallbackApiFactory(fixture.ConnectionString);
        var seeded = await SeedRobotAsync(factory);
        var unknownMessageId = $"unknown-{Guid.NewGuid():N}";
        using var client = factory.CreateClient();

        var response = await PostAsync(client, seeded, new
        {
            messageId = unknownMessageId,
            errorCode = 0,
            type = 1,
            successList = new[] { "Sensitive Display Name" },
            failList = Array.Empty<string>()
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var audit = await scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>().AdministrationAudits.AsNoTracking()
            .SingleAsync(item => item.Action == "worktool.command-result.orphan" &&
                                 item.TargetId == seeded.RobotId.ToString("D"),
                TestContext.Current.CancellationToken);
        Assert.DoesNotContain(unknownMessageId, audit.SanitizedDetailJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Sensitive Display Name", audit.SanitizedDetailJson, StringComparison.Ordinal);
        using var detail = System.Text.Json.JsonDocument.Parse(audit.SanitizedDetailJson);
        Assert.Equal(1, detail.RootElement.GetProperty("SuccessCount").GetInt32());
    }

    [Fact]
    public async Task Invalid_token_and_oversized_payload_are_rejected_without_result_audits()
    {
        await using var factory = new CallbackApiFactory(fixture.ConnectionString);
        var seeded = await SeedRobotAsync(factory);
        using var client = factory.CreateClient();
        var messageId = $"invalid-{Guid.NewGuid():N}";
        var payload = new { messageId, errorCode = 0, type = 1 };

        var invalidToken = await client.PostAsJsonAsync(
            $"/api/worktool/command-results/{seeded.RouteCode}?token=wrong",
            payload,
            TestContext.Current.CancellationToken);
        var oversized = await PostAsync(client, seeded, new
        {
            messageId,
            errorCode = 0,
            type = 1,
            successList = new[] { new string('x', 129) }
        });

        Assert.Equal(HttpStatusCode.Unauthorized, invalidToken.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>().AdministrationAudits.AsNoTracking()
            .CountAsync(audit => audit.Action.StartsWith("worktool.command-result") &&
                                 audit.TargetId == seeded.RobotId.ToString("D"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Result_for_owned_nonterminal_nonaccepted_row_is_applied_and_audited_as_out_of_order()
    {
        await using var factory = new CallbackApiFactory(fixture.ConnectionString);
        var seeded = await SeedSendAsync(factory, WorkToolCommandStatuses.Dispatching);
        using var client = factory.CreateClient();

        (await PostAsync(client, seeded, new
        {
            messageId = seeded.MessageId,
            errorCode = 0,
            type = 1,
            successList = Array.Empty<string>(),
            failList = Array.Empty<string>()
        })).EnsureSuccessStatusCode();

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var command = await database.SendCommands.AsNoTracking().SingleAsync(item => item.Id == seeded.TargetId, TestContext.Current.CancellationToken);
        Assert.Equal(WorkToolCommandStatuses.ExecutedSucceeded, command.Status);
        Assert.Equal("command_result_out_of_order", command.ReconciliationReason);
        Assert.Equal(1, await database.AdministrationAudits.AsNoTracking().CountAsync(
            audit => audit.Action == "worktool.command-result.out-of-order" &&
                     audit.TargetId == seeded.TargetId.ToString("D"),
            TestContext.Current.CancellationToken));
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, SeededTarget target, object payload) =>
        client.PostAsJsonAsync(
            $"/api/worktool/command-results/{target.RouteCode}?token={target.Secret}",
            payload,
            TestContext.Current.CancellationToken);

    private static async Task<SeededTarget> SeedSendAsync(CallbackApiFactory factory, string status)
    {
        var robot = await SeedRobotAsync(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var command = new SendCommandEntity
        {
            RobotConfigId = robot.RobotId,
            IdempotencyKey = $"result-{Guid.NewGuid():N}",
            PayloadJson = "{}",
            Status = status,
            WorkToolCommandMessageId = robot.MessageId,
            AcceptedAtUtc = status == WorkToolCommandStatuses.Accepted ? DateTime.UtcNow : null
        };
        database.SendCommands.Add(command);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        return robot with { TargetId = command.Id };
    }

    private static async Task<SeededTarget> SeedOperationAsync(CallbackApiFactory factory, string status)
    {
        var robot = await SeedRobotAsync(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var audit = new WorkToolOperationAuditEntity
        {
            RobotConfigId = robot.RobotId,
            OperatorName = "test",
            Operation = "Rename",
            WorkToolCommandNumber = 203,
            SanitizedRequestJson = "{}",
            Status = status,
            WorkToolCommandMessageId = robot.MessageId,
            AcceptedAtUtc = status == WorkToolCommandStatuses.Accepted ? DateTime.UtcNow : null
        };
        database.WorkToolOperationAudits.Add(audit);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        return robot with { TargetId = audit.Id };
    }

    private static async Task<SeededTarget> SeedRobotAsync(CallbackApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        var robotId = $"result-robot-{Guid.NewGuid():N}";
        var secret = $"secret-{Guid.NewGuid():N}";
        var robot = new RobotConfigEntity
        {
            Name = robotId,
            WorkToolRobotId = robotId,
            EncryptedWorkToolRobotId = protector.Protect(robotId),
            CallbackRouteCode = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant(),
            CallbackSecretHash = Hash(secret),
            EncryptedCallbackSecret = protector.Protect(secret)
        };
        database.RobotConfigs.Add(robot);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        return new(robot.Id, robot.CallbackRouteCode, secret, $"result-message-{Guid.NewGuid():N}", Guid.Empty);
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string[] Deserialize(string? json) =>
        System.Text.Json.JsonSerializer.Deserialize<string[]>(json ?? "[]") ?? [];

    private sealed record SeededTarget(
        Guid RobotId,
        string RouteCode,
        string Secret,
        string MessageId,
        Guid TargetId);
}
