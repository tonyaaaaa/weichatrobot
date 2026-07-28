using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.Application.Security;
using WechatRobot.Infrastructure.Security;
using WechatRobot.IntegrationTests.Infrastructure;

namespace WechatRobot.IntegrationTests.WorkTool;

public sealed class CallbackIngestionTests : IClassFixture<MySqlFixture>
{
    private readonly MySqlFixture _fixture;

    public CallbackIngestionTests(MySqlFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Legacy_robot_id_backfill_encrypts_removes_plaintext_and_is_idempotent()
    {
        await using var factory = new CallbackApiFactory(_fixture.ConnectionString);
        var marker = $"legacy-robot-{Guid.NewGuid():N}";
        Guid robotId;
        await using (var seedScope = factory.Services.CreateAsyncScope())
        {
            var database = seedScope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            var robot = new RobotConfigEntity
            {
                Name = marker,
                WorkToolRobotId = marker,
                CallbackSecretHash = "test"
            };
            database.RobotConfigs.Add(robot);
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
            robotId = robot.Id;
        }

        await using (var migrateScope = factory.Services.CreateAsyncScope())
        {
            var migrator = migrateScope.ServiceProvider.GetRequiredService<RobotCredentialBackfillService>();
            await migrator.BackfillAsync(TestContext.Current.CancellationToken);
            await migrator.BackfillAsync(TestContext.Current.CancellationToken);
        }

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDatabase = verifyScope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var stored = await verifyDatabase.RobotConfigs.AsNoTracking().SingleAsync(robot => robot.Id == robotId, TestContext.Current.CancellationToken);
        var protector = verifyScope.ServiceProvider.GetRequiredService<ISecretProtector>();
        Assert.Equal(marker, protector.Unprotect(stored.EncryptedWorkToolRobotId!));
        Assert.DoesNotContain(marker, stored.WorkToolRobotId, StringComparison.Ordinal);
        Assert.NotEqual(marker, stored.CallbackRouteCode);
        Assert.Equal(48, stored.CallbackRouteCode!.Length);
        Assert.Null(stored.EncryptedCallbackSecret);
        Assert.Equal("test", stored.CallbackSecretHash);
        Assert.Equal(1, await verifyDatabase.AdministrationAudits.AsNoTracking().CountAsync(
            audit => audit.TargetId == robotId.ToString("D") &&
                     audit.Action == "worktool.callback-credential.rotation-required",
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Valid_callback_returns_accepted_in_under_500ms_and_persists_one_message_and_job()
    {
        await using var factory = new CallbackApiFactory(_fixture.ConnectionString);
        var robot = await SeedRobotAsync(factory, "callback-valid", "callback-secret");
        using var client = factory.CreateClient();
        var warmup = await client.PostAsJsonAsync(
            "/api/worktool/callback/warmup?token=wrong",
            ValidPayload("message-warmup"),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, warmup.StatusCode);

        var stopwatch = Stopwatch.StartNew();
        var response = await client.PostAsJsonAsync($"/api/worktool/callback/{robot.CallbackRouteCode}?token=callback-secret", ValidPayload("message-valid"), TestContext.Current.CancellationToken);
        stopwatch.Stop();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"code\":0,\"message\":\"accepted\"}", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(500), $"Callback took {stopwatch.ElapsedMilliseconds} ms.");

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var message = await database.ConversationMessages.SingleAsync(
            message => message.RobotConfigId == robot.Id,
            TestContext.Current.CancellationToken);
        Assert.Equal("Support", message.GroupName);
        Assert.Equal("Support", message.GroupRemark);
        Assert.Equal(1, await CountJobsForRobotAsync(database, robot.Id));
        var job = await database.DurableJobs.SingleAsync(item => item.RelatedConversationMessageId != null
            && item.PayloadJson.Contains(robot.Id.ToString()), TestContext.Current.CancellationToken);
        Assert.Contains("\"WasMentioned\":false", job.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"GroupRemark\":\"Support\"", job.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Official_string_atMe_callback_is_accepted_and_preserves_mention_state()
    {
        await using var factory = new CallbackApiFactory(_fixture.ConnectionString);
        var robot = await SeedRobotAsync(factory, "callback-official-atme", "callback-secret");
        using var client = factory.CreateClient();
        const string payload = """
            {
              "spoken": "How do I reset my password?",
              "rawSpoken": "How do I reset my password?",
              "receivedName": "Alice",
              "groupName": "Support",
              "groupRemark": "Support",
              "roomType": 1,
              "atMe": "true",
              "textType": 1,
              "messageId": "message-official-string-atme"
            }
            """;

        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        var response = await client.PostAsync(
            $"/api/worktool/callback/{robot.CallbackRouteCode}?token=callback-secret",
            content,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"code\":0,\"message\":\"accepted\"}", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        Assert.Equal(1, await database.ConversationMessages.CountAsync(
            message => message.RobotConfigId == robot.Id,
            TestContext.Current.CancellationToken));
        var job = await database.DurableJobs.SingleAsync(
            item => item.RelatedConversationMessageId != null &&
                    item.PayloadJson.Contains(robot.Id.ToString()),
            TestContext.Current.CancellationToken);
        Assert.Contains("\"WasMentioned\":true", job.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Configuration_callback_uses_opaque_route_and_returns_required_json_200()
    {
        await using var factory = new CallbackApiFactory(_fixture.ConnectionString);
        var robot = await SeedRobotAsync(factory, "config-callback", "callback-secret");
        using var client = factory.CreateClient();

        var accepted = await client.PostAsJsonAsync(
            $"/api/worktool/config-callback/{robot.CallbackRouteCode}",
            new { type = 1, code = 0 },
            TestContext.Current.CancellationToken);
        var legacyRoute = await client.PostAsJsonAsync(
            $"/api/worktool/config-callback/{robot.WorkToolRobotId}",
            new { type = 1, code = 0 },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("{\"code\":0,\"message\":\"accepted\"}", await accepted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.Unauthorized, legacyRoute.StatusCode);
    }

    [Fact]
    public async Task Invalid_token_is_rejected_without_enqueuing()
    {
        await using var factory = new CallbackApiFactory(_fixture.ConnectionString);
        var robot = await SeedRobotAsync(factory, "callback-token", "callback-secret");
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/worktool/callback/{robot.CallbackRouteCode}?token=wrong", ValidPayload("message-invalid-token"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertNoInboundDataAsync(factory, robot.Id);
    }

    [Theory]
    [InlineData("roomType", 2)]
    [InlineData("roomType", 3)]
    [InlineData("roomType", 4)]
    [InlineData("textType", 2)]
    [InlineData("textType", 3)]
    [InlineData("textType", 9)]
    public async Task Official_but_unsupported_callback_is_acknowledged_and_ignored(string field, int value)
    {
        await using var factory = new CallbackApiFactory(_fixture.ConnectionString);
        var robot = await SeedRobotAsync(factory, $"callback-ignored-{field}-{value}", "callback-secret");
        var payload = ValidPayload($"message-ignored-{field}-{value}");
        payload[field] = value;
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/worktool/callback/{robot.CallbackRouteCode}?token=callback-secret", payload, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "{\"code\":0,\"message\":\"ignored\"}",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        await AssertNoInboundDataAsync(factory, robot.Id);
    }

    [Fact]
    public async Task Repeated_valid_callback_is_accepted_but_creates_one_durable_job()
    {
        await using var factory = new CallbackApiFactory(_fixture.ConnectionString);
        var robot = await SeedRobotAsync(factory, "callback-duplicate", "callback-secret");
        using var client = factory.CreateClient();
        var payload = ValidPayload("message-duplicate");

        var first = await client.PostAsJsonAsync($"/api/worktool/callback/{robot.CallbackRouteCode}?token=callback-secret", payload, TestContext.Current.CancellationToken);
        var second = await client.PostAsJsonAsync($"/api/worktool/callback/{robot.CallbackRouteCode}?token=callback-secret", payload, TestContext.Current.CancellationToken);

        Assert.Equal("{\"code\":0,\"message\":\"accepted\"}", await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("{\"code\":0,\"message\":\"accepted\"}", await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        Assert.Equal(1, await database.ConversationMessages.CountAsync(message => message.RobotConfigId == robot.Id, TestContext.Current.CancellationToken));
        Assert.Equal(1, await CountJobsForRobotAsync(database, robot.Id));
    }

    [Fact]
    public async Task Parallel_duplicate_callbacks_are_accepted_but_create_one_message_and_job()
    {
        await using var factory = new CallbackApiFactory(_fixture.ConnectionString);
        var robot = await SeedRobotAsync(factory, "callback-parallel-duplicate", "callback-secret");
        var payload = ValidPayload("message-parallel-duplicate");

        using var firstClient = factory.CreateClient();
        using var secondClient = factory.CreateClient();
        var responses = await Task.WhenAll(
            firstClient.PostAsJsonAsync($"/api/worktool/callback/{robot.CallbackRouteCode}?token=callback-secret", payload, TestContext.Current.CancellationToken),
            secondClient.PostAsJsonAsync($"/api/worktool/callback/{robot.CallbackRouteCode}?token=callback-secret", payload, TestContext.Current.CancellationToken));

        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("{\"code\":0,\"message\":\"accepted\"}", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        Assert.Equal(1, await database.ConversationMessages.CountAsync(message => message.RobotConfigId == robot.Id, TestContext.Current.CancellationToken));
        Assert.Equal(1, await CountJobsForRobotAsync(database, robot.Id));
    }

    [Fact]
    public async Task Ingestion_timeout_returns_redacted_failure_and_rolls_back_the_transaction()
    {
        var interceptor = new DelayingSaveChangesInterceptor(TimeSpan.FromMilliseconds(200));
        await using var factory = new CallbackApiFactory(_fixture.ConnectionString, callbackDeadlineMilliseconds: 50, interceptor: interceptor);
        var robot = await SeedRobotAsync(factory, "callback-timeout", "callback-secret");
        interceptor.DelayOnSave = true;
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/worktool/callback/{robot.CallbackRouteCode}?token=callback-secret", ValidPayload("message-timeout"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("timeout", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.OrdinalIgnoreCase);
        await AssertNoInboundDataAsync(factory, robot.Id);
    }

    [Fact]
    public async Task Oversized_persisted_callback_field_is_rejected_without_enqueuing()
    {
        await using var factory = new CallbackApiFactory(_fixture.ConnectionString);
        var robot = await SeedRobotAsync(factory, "callback-oversized", "callback-secret");
        var payload = ValidPayload("message-oversized");
        payload["receivedName"] = new string('a', 129);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/worktool/callback/{robot.CallbackRouteCode}?token=callback-secret", payload, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("received", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.OrdinalIgnoreCase);
        await AssertNoInboundDataAsync(factory, robot.Id);
    }

    [Fact]
    public async Task Fallback_deduplication_bucket_uses_configured_runtime_window()
    {
        await using var factory = new CallbackApiFactory(_fixture.ConnectionString, fallbackTimeBucketSeconds: 420);
        var robot = await SeedRobotAsync(factory, "callback-configured-bucket", "callback-secret");
        var payload = ValidPayload(string.Empty);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/worktool/callback/{robot.CallbackRouteCode}?token=callback-secret", payload, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var message = await database.ConversationMessages.SingleAsync(value => value.RobotConfigId == robot.Id, TestContext.Current.CancellationToken);
        var window = TimeSpan.FromMinutes(7);
        var expectedWindowStart = new DateTime(message.ReceivedAtUtc.Ticks - message.ReceivedAtUtc.Ticks % window.Ticks, DateTimeKind.Utc);
        Assert.Equal(expectedWindowStart, message.FallbackWindowStartUtc);
    }

    [Fact]
    public async Task One_source_cannot_bypass_callback_limit_by_varying_robot_codes()
    {
        await using var factory = new CallbackApiFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();

        var responses = new List<HttpResponseMessage>();
        for (var index = 0; index < 51; index++)
        {
            responses.Add(await client.PostAsJsonAsync($"/api/worktool/callback/unregistered-{index}?token=wrong", ValidPayload($"message-rate-{index}"), TestContext.Current.CancellationToken));
        }

        Assert.All(responses.Take(50), response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
        Assert.Equal(HttpStatusCode.TooManyRequests, responses[50].StatusCode);
    }

    [Fact]
    public async Task Job_enqueue_failure_rolls_back_inbound_message()
    {
        await using var factory = new CallbackApiFactory(_fixture.ConnectionString);
        var robot = await SeedRobotAsync(factory, "callback-rollback", "callback-secret");
        await using var triggerScope = factory.Services.CreateAsyncScope();
        var triggerDatabase = triggerScope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        await triggerDatabase.Database.ExecuteSqlRawAsync(
            """
            CREATE TRIGGER `fail_callback_durable_job`
            BEFORE INSERT ON `durable_job`
            FOR EACH ROW
            SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'callback durable job failure';
            """,
            TestContext.Current.CancellationToken);
        using var client = factory.CreateClient();

        try
        {
            var response = await client.PostAsJsonAsync($"/api/worktool/callback/{robot.CallbackRouteCode}?token=callback-secret", ValidPayload("message-rollback"), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            await AssertNoInboundDataAsync(factory, robot.Id);
        }
        finally
        {
            await triggerDatabase.Database.ExecuteSqlRawAsync(
                "DROP TRIGGER IF EXISTS `fail_callback_durable_job`;",
                TestContext.Current.CancellationToken);
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
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        var robot = new RobotConfigEntity
        {
            Name = robotCode,
            WorkToolRobotId = robotCode,
            EncryptedWorkToolRobotId = protector.Protect(robotCode),
            CallbackRouteCode = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant(),
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
    private readonly int? _callbackDeadlineMilliseconds;
    private readonly int? _fallbackTimeBucketSeconds;
    private readonly IInterceptor? _interceptor;

    public CallbackApiFactory(string connectionString, int? callbackDeadlineMilliseconds = null, int? fallbackTimeBucketSeconds = null, IInterceptor? interceptor = null)
    {
        _connectionString = connectionString;
        _callbackDeadlineMilliseconds = callbackDeadlineMilliseconds;
        _fallbackTimeBucketSeconds = fallbackTimeBucketSeconds;
        _interceptor = interceptor;
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
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            var values = new Dictionary<string, string?>
            {
            ["Jwt:Issuer"] = "callback-tests",
            ["Jwt:Audience"] = "callback-tests-api",
            ["Jwt:SigningKey"] = "callback-tests-signing-key-must-be-at-least-32-bytes",
            ["ConnectionStrings:WechatRobot"] = _connectionString,
            ["Cors:AllowedOrigins:0"] = "https://admin.example.test",
            ["Database:ApplyMigrationsOnStartup"] = "true"
            };
            if (_callbackDeadlineMilliseconds is not null)
            {
                values["WorkToolCallback:IngestionDeadlineMilliseconds"] = _callbackDeadlineMilliseconds.Value.ToString();
            }

            if (_fallbackTimeBucketSeconds is not null)
            {
                values["WorkToolCallback:FallbackDeduplicationWindowSeconds"] = _fallbackTimeBucketSeconds.Value.ToString();
            }

            configuration.AddInMemoryCollection(values);
        });

        if (_interceptor is not null)
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<WechatRobotDbContext>>();
                services.RemoveAll<WechatRobotDbContext>();
                services.AddDbContext<WechatRobotDbContext>(options => options.UseMySQL(_connectionString).AddInterceptors(_interceptor));
            });
        }
    }
}

public sealed class DelayingSaveChangesInterceptor(TimeSpan delay) : SaveChangesInterceptor
{
    public bool DelayOnSave { get; set; }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (DelayOnSave)
        {
            await Task.Delay(delay, cancellationToken);
        }

        return result;
    }
}
