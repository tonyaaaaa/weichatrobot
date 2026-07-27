using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WechatRobot.Application.Conversations;
using WechatRobot.IntegrationTests.Infrastructure;
using WechatRobot.IntegrationTests.Models;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.IntegrationTests.Groups;

public sealed class GroupConfigurationMySqlTests(MySqlFixture fixture) : IClassFixture<MySqlFixture>
{
    [Fact]
    public async Task Handoff_pause_policy_is_persisted_with_optimistic_concurrency()
    {
        await using var factory = new MySqlGroupApiFactory(fixture.ConnectionString);
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        await database.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var suffix = Guid.NewGuid().ToString("N");
        var robot = new RobotConfigEntity { Name = $"policy-{suffix}", WorkToolRobotId = $"policy-{suffix}", CallbackSecretHash = "hash" };
        var group = new GroupProfileEntity { RobotConfigId = robot.Id, ExternalGroupId = suffix, Name = $"policy-{suffix}" };
        database.AddRange(robot, group);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var client = factory.CreateClient();

        var saved = await client.PutAsJsonAsync($"/api/groups/{group.Id}/configuration", new
        {
            includeRules = Array.Empty<object>(), excludeRules = Array.Empty<object>(), boundTagIds = Array.Empty<Guid>(),
            context = new { senderIsolated = (bool?)null, historyTurns = (int?)null, idleTimeoutMinutes = (int?)null, tokenCap = (int?)null, summaryEnabled = (bool?)null, includeBotHistory = (bool?)null },
            clearContext = false, handoffPausePolicy = "Sender", expectedConfigurationVersion = 0
        }, TestContext.Current.CancellationToken);

        saved.EnsureSuccessStatusCode();
        var body = await saved.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("Sender", body.GetProperty("handoffPausePolicy").GetString());
        Assert.Equal(1, body.GetProperty("configurationVersion").GetInt32());

        var stale = await client.PutAsJsonAsync($"/api/groups/{group.Id}/configuration", new
        {
            includeRules = Array.Empty<object>(), excludeRules = Array.Empty<object>(), boundTagIds = Array.Empty<Guid>(),
            context = new { senderIsolated = (bool?)null, historyTurns = (int?)null, idleTimeoutMinutes = (int?)null, tokenCap = (int?)null, summaryEnabled = (bool?)null, includeBotHistory = (bool?)null },
            clearContext = false, handoffPausePolicy = "Group", expectedConfigurationVersion = 0
        }, TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.Conflict, stale.StatusCode);
        database.ChangeTracker.Clear();
        var persisted = await database.GroupProfiles.AsNoTracking().SingleAsync(item => item.Id == group.Id, TestContext.Current.CancellationToken);
        Assert.Equal("Sender", persisted.HandoffPausePolicy);
        Assert.Equal(1, persisted.ConfigurationVersion);
    }

    [Fact]
    public async Task Configuration_binds_multiple_tags_on_mysql()
    {
        await using var factory = new MySqlGroupApiFactory(fixture.ConnectionString);
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        await database.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var robot = new RobotConfigEntity { Name = "mysql-group", WorkToolRobotId = Guid.NewGuid().ToString("N"), CallbackSecretHash = "hash" };
        var group = new GroupProfileEntity { RobotConfigId = robot.Id, ExternalGroupId = Guid.NewGuid().ToString("N"), Name = "技术部" };
        var product = new KnowledgeTagEntity { Name = Guid.NewGuid().ToString("N"), NormalizedName = Guid.NewGuid().ToString("N") };
        var support = new KnowledgeTagEntity { Name = Guid.NewGuid().ToString("N"), NormalizedName = Guid.NewGuid().ToString("N") };
        database.AddRange(robot, group, product, support);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);

        using var client = factory.CreateClient();
        var response = await client.PutAsJsonAsync($"/api/groups/{group.Id}/configuration", new
        {
            includeRules = Array.Empty<object>(), excludeRules = Array.Empty<object>(), boundTagIds = new[] { product.Id, support.Id },
            context = new { senderIsolated = (bool?)null, historyTurns = (int?)null, idleTimeoutMinutes = (int?)null, tokenCap = (int?)null, summaryEnabled = (bool?)null, includeBotHistory = (bool?)null },
            clearContext = false,
            expectedConfigurationVersion = 0
        }, TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        database.ChangeTracker.Clear();
        Assert.Equal(2, await database.GroupProfileTags.CountAsync(binding => binding.GroupProfileId == group.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Clear_context_advances_all_group_session_watermarks_and_retains_history_and_audit()
    {
        await using var factory = new MySqlGroupApiFactory(fixture.ConnectionString);
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        await database.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await database.ModelConfigs.Where(item => item.ConfigurationType == "chat" && item.IsDefault)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsDefault, false), TestContext.Current.CancellationToken);
        var now = DateTime.UtcNow.AddMinutes(-2);
        var robot = new RobotConfigEntity { Name = $"clear-{Guid.NewGuid():N}", WorkToolRobotId = Guid.NewGuid().ToString("N"), CallbackSecretHash = "hash" };
        var group = new GroupProfileEntity { RobotConfigId = robot.Id, ExternalGroupId = Guid.NewGuid().ToString("N"), Name = $"clear-{Guid.NewGuid():N}" };
        var other = new GroupProfileEntity { RobotConfigId = robot.Id, ExternalGroupId = Guid.NewGuid().ToString("N"), Name = $"other-{Guid.NewGuid():N}" };
        var stableScope = ConversationScopeResolver.Resolve(true, "stable-user-1", Guid.NewGuid()).ScopeKey;
        var groupSession = Session(group.Id, "group", now);
        var senderSession = Session(group.Id, stableScope, now);
        var otherSession = Session(other.Id, "group", now);
        var groupInbound = Message(robot.Id, group, groupSession, "old group", 1, now);
        var groupOutbound = Message(robot.Id, group, groupSession, "old group answer", 2, now.AddSeconds(1), "assistant", "outbound");
        var senderInbound = Message(robot.Id, group, senderSession, "old sender", 1, now, stableId: "stable-user-1");
        var senderOutbound = Message(robot.Id, group, senderSession, "old sender answer", 2, now.AddSeconds(1), "assistant", "outbound", "stable-user-1");
        var otherInbound = Message(robot.Id, other, otherSession, "other old", 1, now);
        database.AddRange(robot, group, other, groupSession, senderSession, otherSession, groupInbound, groupOutbound, senderInbound, senderOutbound, otherInbound,
            Audit(group.Id, groupInbound.Id, now), Audit(group.Id, senderInbound.Id, now),
            new ModelConfigEntity { Name = $"chat-{Guid.NewGuid():N}", NormalizedName = $"CHAT-{Guid.NewGuid():N}", Provider = "fake", ConfigurationType = "chat", BaseUrl = "https://fake.test", Model = "fake", EncryptedApiKey = "fake", IsDefault = true });
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);

        using var client = factory.CreateClient();
        var response = await client.PutAsJsonAsync($"/api/groups/{group.Id}/configuration", new
        {
            includeRules = Array.Empty<object>(), excludeRules = Array.Empty<object>(), boundTagIds = Array.Empty<Guid>(),
            context = new { senderIsolated = false, historyTurns = 6, idleTimeoutMinutes = 30, tokenCap = 3000, summaryEnabled = true, includeBotHistory = true },
            clearContext = true,
            expectedConfigurationVersion = 0
        }, TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(2, body.GetProperty("clearedContextSessions").GetInt32());
        database.ChangeTracker.Clear();
        Assert.Equal(4, await database.ConversationMessages.CountAsync(item => item.GroupProfileId == group.Id, TestContext.Current.CancellationToken));
        Assert.Equal(2, await database.RetrievalAudits.CountAsync(item => item.GroupProfileId == group.Id, TestContext.Current.CancellationToken));

        var repository = scope.ServiceProvider.GetRequiredService<IGroundedConversationRepository>();
        var groupNew = Message(robot.Id, group, null, "group new", null, DateTime.UtcNow);
        database.ConversationMessages.Add(groupNew);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.Empty((await repository.LoadForProcessingAsync(groupNew.Id, TestContext.Current.CancellationToken)).History);

        database.ChangeTracker.Clear();
        var currentGroup = await database.GroupProfiles.SingleAsync(item => item.Id == group.Id, TestContext.Current.CancellationToken);
        currentGroup.ContextSenderIsolated = true;
        var senderNew = Message(robot.Id, group, null, "sender new", null, DateTime.UtcNow, stableId: "stable-user-1");
        database.ConversationMessages.Add(senderNew);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.Empty((await repository.LoadForProcessingAsync(senderNew.Id, TestContext.Current.CancellationToken)).History);

        var otherNew = Message(robot.Id, other, null, "other new", null, DateTime.UtcNow);
        database.ConversationMessages.Add(otherNew);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.Contains((await repository.LoadForProcessingAsync(otherNew.Id, TestContext.Current.CancellationToken)).History, item => item.Content == "other old");
    }

    private static ConversationSessionEntity Session(Guid groupId, string scope, DateTime at) => new()
    {
        GroupProfileId = groupId, SenderScopeKey = scope, Summary = "old summary", NextSequence = 2, LastActivityAtUtc = at, CreatedAtUtc = at, UpdatedAtUtc = at
    };

    private static ConversationMessageEntity Message(Guid robotId, GroupProfileEntity group, ConversationSessionEntity? session, string text, long? sequence,
        DateTime at, string role = "user", string direction = "inbound", string? stableId = null) => new()
    {
        RobotConfigId = robotId, GroupProfileId = group.Id, ConversationSessionId = session?.Id, SessionSequence = sequence, GroupName = group.Name,
        Direction = direction, Role = role, FallbackHash = Guid.NewGuid().ToString("N"), FallbackWindowStartUtc = at,
        SenderDisplayName = "member", StableSenderId = stableId, Text = text, ReceivedAtUtc = at, CreatedAtUtc = at
    };

    private static RetrievalAuditEntity Audit(Guid groupId, Guid messageId, DateTime at) => new()
    {
        GroupProfileId = groupId, ConversationMessageId = messageId, Decision = "Answer", ConfidenceThreshold = .7,
        ContextPolicy = "test", EvidenceJson = "[]", InputSummaryJson = "{}", CreatedAtUtc = at
    };

    private sealed class MySqlGroupApiFactory : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;

        public MySqlGroupApiFactory(string connectionString)
        {
            _connectionString = connectionString;
            Environment.SetEnvironmentVariable("ConnectionStrings__WechatRobot", connectionString);
            Environment.SetEnvironmentVariable("Cors__AllowedOrigins__0", "https://admin.example.test");
            Environment.SetEnvironmentVariable("Jwt__Issuer", "mysql-group-tests");
            Environment.SetEnvironmentVariable("Jwt__Audience", "mysql-group-tests-api");
            Environment.SetEnvironmentVariable("Jwt__SigningKey", "mysql-group-tests-signing-key-at-least-32-bytes");
            Environment.SetEnvironmentVariable("WECHATROBOT_MASTER_KEY_BASE64", Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.DisableStartupMigrations();
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:WechatRobot"] = _connectionString,
                ["Cors:AllowedOrigins:0"] = "https://admin.example.test",
                ["Jwt:Issuer"] = "mysql-group-tests",
                ["Jwt:Audience"] = "mysql-group-tests-api",
                ["Jwt:SigningKey"] = "mysql-group-tests-signing-key-at-least-32-bytes"
            }));
            builder.ConfigureServices(services =>
            {
                foreach (var descriptor in services.Where(service => service.ServiceType == typeof(DbContextOptions<WechatRobotDbContext>)).ToArray()) services.Remove(descriptor);
                services.AddDbContext<WechatRobotDbContext>(options => options.UseMySQL(_connectionString));
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = "integration-admin";
                        options.DefaultChallengeScheme = "integration-admin";
                        options.DefaultForbidScheme = "integration-admin";
                    })
                    .AddScheme<AuthenticationSchemeOptions, IntegrationAdminAuthenticationHandler>("integration-admin", _ => { });
            });
        }
    }
}
