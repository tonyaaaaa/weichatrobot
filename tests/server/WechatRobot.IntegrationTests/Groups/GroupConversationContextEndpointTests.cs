using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WechatRobot.IntegrationTests.Infrastructure;
using WechatRobot.IntegrationTests.Models;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.IntegrationTests.Groups;

public sealed class GroupConversationContextEndpointTests(MySqlFixture fixture) : IClassFixture<MySqlFixture>
{
    [Fact]
    public async Task Query_returns_effective_redacted_context_and_clear_preserves_history()
    {
        await using var factory = new ContextApiFactory(fixture.ConnectionString);
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        await database.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var now = DateTime.UtcNow;
        var robot = new RobotConfigEntity
        {
            Name = $"context-{Guid.NewGuid():N}",
            WorkToolRobotId = $"secret-robot-{Guid.NewGuid():N}",
            CallbackSecretHash = "secret-callback"
        };
        var group = new GroupProfileEntity
        {
            RobotConfigId = robot.Id,
            Name = $"上下文群-{Guid.NewGuid():N}",
            ContextHistoryTurns = 1,
            ContextTokenCap = 3000,
            ContextIdleTimeoutMinutes = 30
        };
        var rawScope = $"stable:secret-user-{Guid.NewGuid():N}";
        var session = new ConversationSessionEntity
        {
            GroupProfileId = group.Id,
            SenderScopeKey = rawScope,
            Summary = "历史摘要",
            LastActivityAtUtc = now,
            NextSequence = 3,
            CreatedAtUtc = now.AddMinutes(-2),
            UpdatedAtUtc = now
        };
        var oldMessage = Message(robot.Id, group, session, 1, "旧问题", "user", "客户甲", now.AddMinutes(-2));
        var currentQuestion = Message(robot.Id, group, session, 2, "当前问题", "user", "客户甲", now.AddSeconds(-2));
        var currentAnswer = Message(robot.Id, group, session, 3, "当前回答", "assistant", "机器人", now.AddSeconds(-1));
        database.AddRange(robot, group, session, oldMessage, currentQuestion, currentAnswer);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);

        using var client = factory.CreateClient();
        using var query = await client.GetAsync(
            $"/api/groups/{group.Id:D}/conversation-context?page=1&pageSize=20",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, query.StatusCode);
        var rawJson = await query.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(rawScope, rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain(robot.WorkToolRobotId, rawJson, StringComparison.Ordinal);
        var body = JsonDocument.Parse(rawJson).RootElement;
        Assert.Equal(1, body.GetProperty("total").GetInt32());
        var item = body.GetProperty("items")[0];
        Assert.Equal("客户甲", item.GetProperty("senderDisplayName").GetString());
        Assert.StartsWith("sender:", item.GetProperty("scope").GetString(), StringComparison.Ordinal);
        Assert.Equal("历史摘要", item.GetProperty("summary").GetString());
        Assert.Equal(2, item.GetProperty("messages").GetArrayLength());
        Assert.Equal("当前问题", item.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal("当前回答", item.GetProperty("messages")[1].GetProperty("content").GetString());

        using var cleared = await client.PostAsJsonAsync(
            $"/api/groups/{group.Id:D}/conversation-context/clear",
            new { expectedConfigurationVersion = 0 },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        database.ChangeTracker.Clear();
        Assert.Equal(3, await database.ConversationMessages.CountAsync(
            message => message.GroupProfileId == group.Id,
            TestContext.Current.CancellationToken));
        var persisted = await database.ConversationSessions.AsNoTracking().SingleAsync(
            item => item.Id == session.Id,
            TestContext.Current.CancellationToken);
        Assert.Null(persisted.Summary);
        Assert.Equal(persisted.NextSequence, persisted.ClearedThroughSequence);
        Assert.NotNull(persisted.ClearedAtUtc);

        using var stale = await client.PostAsJsonAsync(
            $"/api/groups/{group.Id:D}/conversation-context/clear",
            new { expectedConfigurationVersion = 1 },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    private static ConversationMessageEntity Message(
        Guid robotId,
        GroupProfileEntity group,
        ConversationSessionEntity session,
        long sequence,
        string text,
        string role,
        string sender,
        DateTime at) => new()
    {
        RobotConfigId = robotId,
        GroupProfileId = group.Id,
        ConversationSessionId = session.Id,
        SessionSequence = sequence,
        ProcessingState = "completed",
        Direction = role == "assistant" ? "outbound" : "inbound",
        Role = role,
        FallbackHash = Guid.NewGuid().ToString("N"),
        FallbackWindowStartUtc = at,
        GroupName = group.Name,
        SenderDisplayName = sender,
        Text = text,
        ReceivedAtUtc = at,
        CreatedAtUtc = at
    };

    private sealed class ContextApiFactory : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;

        public ContextApiFactory(string connectionString)
        {
            _connectionString = connectionString;
            Environment.SetEnvironmentVariable("ConnectionStrings__WechatRobot", connectionString);
            Environment.SetEnvironmentVariable("Cors__AllowedOrigins__0", "https://admin.example.test");
            Environment.SetEnvironmentVariable("Jwt__Issuer", "context-tests");
            Environment.SetEnvironmentVariable("Jwt__Audience", "context-tests-api");
            Environment.SetEnvironmentVariable("Jwt__SigningKey", "context-tests-signing-key-at-least-32-bytes");
            Environment.SetEnvironmentVariable(
                "WECHATROBOT_MASTER_KEY_BASE64",
                Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.DisableStartupMigrations();
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:WechatRobot"] = _connectionString,
                    ["Cors:AllowedOrigins:0"] = "https://admin.example.test",
                    ["Jwt:Issuer"] = "context-tests",
                    ["Jwt:Audience"] = "context-tests-api",
                    ["Jwt:SigningKey"] = "context-tests-signing-key-at-least-32-bytes"
                }));
            builder.ConfigureServices(services =>
            {
                foreach (var descriptor in services
                    .Where(service => service.ServiceType == typeof(DbContextOptions<WechatRobotDbContext>))
                    .ToArray())
                    services.Remove(descriptor);
                services.AddDbContext<WechatRobotDbContext>(options => options.UseMySQL(_connectionString));
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = "integration-admin";
                        options.DefaultChallengeScheme = "integration-admin";
                        options.DefaultForbidScheme = "integration-admin";
                    })
                    .AddScheme<AuthenticationSchemeOptions, IntegrationAdminAuthenticationHandler>(
                        "integration-admin",
                        _ => { });
            });
        }
    }
}
