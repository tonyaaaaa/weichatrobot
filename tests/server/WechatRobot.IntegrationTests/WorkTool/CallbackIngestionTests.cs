using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.IntegrationTests.Infrastructure;

namespace WechatRobot.IntegrationTests.WorkTool;

public sealed class CallbackIngestionTests : IClassFixture<MySqlFixture>
{
    private readonly MySqlFixture _fixture;

    public CallbackIngestionTests(MySqlFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Valid_callback_returns_accepted_in_under_500ms_and_persists_one_message_and_job()
    {
        await using var factory = new CallbackApiFactory(_fixture.ConnectionString);
        var robot = await SeedRobotAsync(factory, "callback-valid", "callback-secret");
        using var client = factory.CreateClient();
        var stopwatch = Stopwatch.StartNew();
        var response = await client.PostAsJsonAsync($"/api/worktool/callback/{robot.WorkToolRobotId}?token=callback-secret", ValidPayload("message-valid"), TestContext.Current.CancellationToken);
        stopwatch.Stop();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"code\":0,\"message\":\"accepted\"}", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(500), $"Callback took {stopwatch.ElapsedMilliseconds} ms.");

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        Assert.Equal(1, await database.ConversationMessages.CountAsync(message => message.RobotConfigId == robot.Id, TestContext.Current.CancellationToken));
        Assert.Equal(1, await CountJobsForRobotAsync(database, robot.Id));
    }

    [Fact]
    public async Task Invalid_token_is_rejected_without_enqueuing()
    {
        await using var factory = new CallbackApiFactory(_fixture.ConnectionString);
        var robot = await SeedRobotAsync(factory, "callback-token", "callback-secret");
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/worktool/callback/{robot.WorkToolRobotId}?token=wrong", ValidPayload("message-invalid-token"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertNoInboundDataAsync(factory, robot.Id);
    }

    [Theory]
    [InlineData("roomType", 2)]
    [InlineData("textType", 2)]
    public async Task Non_group_or_non_text_callback_is_rejected_without_enqueuing(string field, int value)
    {
        await using var factory = new CallbackApiFactory(_fixture.ConnectionString);
        var robot = await SeedRobotAsync(factory, $"callback-{field}-{value}", "callback-secret");
        var payload = ValidPayload($"message-{field}-{value}");
        payload[field] = value;
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/worktool/callback/{robot.WorkToolRobotId}?token=callback-secret", payload, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoInboundDataAsync(factory, robot.Id);
    }

    [Fact]
    public async Task Repeated_valid_callback_is_accepted_but_creates_one_durable_job()
    {
        await using var factory = new CallbackApiFactory(_fixture.ConnectionString);
        var robot = await SeedRobotAsync(factory, "callback-duplicate", "callback-secret");
        using var client = factory.CreateClient();
        var payload = ValidPayload("message-duplicate");

        var first = await client.PostAsJsonAsync($"/api/worktool/callback/{robot.WorkToolRobotId}?token=callback-secret", payload, TestContext.Current.CancellationToken);
        var second = await client.PostAsJsonAsync($"/api/worktool/callback/{robot.WorkToolRobotId}?token=callback-secret", payload, TestContext.Current.CancellationToken);

        Assert.Equal("{\"code\":0,\"message\":\"accepted\"}", await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("{\"code\":0,\"message\":\"accepted\"}", await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        Assert.Equal(1, await database.ConversationMessages.CountAsync(message => message.RobotConfigId == robot.Id, TestContext.Current.CancellationToken));
        Assert.Equal(1, await CountJobsForRobotAsync(database, robot.Id));
    }

    [Fact]
    public async Task Job_enqueue_failure_rolls_back_inbound_message()
    {
        await using var factory = new CallbackApiFactory(_fixture.ConnectionString);
        var robot = await SeedRobotAsync(factory, "callback-rollback", "callback-secret");
        await using var triggerScope = factory.Services.CreateAsyncScope();
        var triggerDatabase = triggerScope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var checkConstraint = $"CHECK (PayloadJson NOT LIKE '%{robot.Id:D}%')";
#pragma warning disable EF1002 // The value is a locally generated Guid and MySQL does not parameterize DDL constraint expressions.
        await triggerDatabase.Database.ExecuteSqlRawAsync($"ALTER TABLE durable_job ADD CONSTRAINT fail_callback_durable_job {checkConstraint};", TestContext.Current.CancellationToken);
#pragma warning restore EF1002
        using var client = factory.CreateClient();

        try
        {
            var response = await client.PostAsJsonAsync($"/api/worktool/callback/{robot.WorkToolRobotId}?token=callback-secret", ValidPayload("message-rollback"), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            await AssertNoInboundDataAsync(factory, robot.Id);
        }
        finally
        {
            await triggerDatabase.Database.ExecuteSqlRawAsync("ALTER TABLE durable_job DROP CHECK fail_callback_durable_job;", TestContext.Current.CancellationToken);
        }
    }

    private static Dictionary<string, object> ValidPayload(string messageId) => new()
    {
        ["spoken"] = "How do I reset my password?",
        ["rawSpoken"] = "How do I reset my password?",
        ["receivedName"] = "Alice",
        ["groupName"] = "Support",
        ["groupRemark"] = "Support",
        ["roomType"] = 1,
        ["atMe"] = false,
        ["textType"] = 1,
        ["messageId"] = messageId
    };

    private static async Task<RobotConfigEntity> SeedRobotAsync(CallbackApiFactory factory, string robotCode, string callbackSecret)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var robot = new RobotConfigEntity
        {
            Name = robotCode,
            WorkToolRobotId = robotCode,
            CallbackSecretHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(callbackSecret)))
        };
        database.RobotConfigs.Add(robot);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        return robot;
    }

    private static async Task AssertNoInboundDataAsync(CallbackApiFactory factory, Guid robotId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        Assert.Equal(0, await database.ConversationMessages.CountAsync(message => message.RobotConfigId == robotId, TestContext.Current.CancellationToken));
        Assert.Equal(0, await CountJobsForRobotAsync(database, robotId));
    }

    private static Task<int> CountJobsForRobotAsync(WechatRobotDbContext database, Guid robotId) => database.DurableJobs.CountAsync(
        job => job.JobType == "ProcessInboundMessage" && job.PayloadJson.Contains(robotId.ToString()),
        TestContext.Current.CancellationToken);
}

public sealed class CallbackApiFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    public CallbackApiFactory(string connectionString)
    {
        _connectionString = connectionString;
        Environment.SetEnvironmentVariable("WECHATROBOT_MASTER_KEY_BASE64", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        Environment.SetEnvironmentVariable("Jwt__Issuer", "callback-tests");
        Environment.SetEnvironmentVariable("Jwt__Audience", "callback-tests-api");
        Environment.SetEnvironmentVariable("Jwt__SigningKey", "callback-tests-signing-key-must-be-at-least-32-bytes");
        Environment.SetEnvironmentVariable("ConnectionStrings__WechatRobot", _connectionString);
        Environment.SetEnvironmentVariable("Cors__AllowedOrigins__0", "https://admin.example.test");
        Environment.SetEnvironmentVariable("Database__ApplyMigrationsOnStartup", "true");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Issuer"] = "callback-tests",
            ["Jwt:Audience"] = "callback-tests-api",
            ["Jwt:SigningKey"] = "callback-tests-signing-key-must-be-at-least-32-bytes",
            ["ConnectionStrings:WechatRobot"] = _connectionString,
            ["Cors:AllowedOrigins:0"] = "https://admin.example.test",
            ["Database:ApplyMigrationsOnStartup"] = "true"
        }));
    }
}
