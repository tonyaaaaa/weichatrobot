using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WechatRobot.Application.Conversations;
using WechatRobot.IntegrationTests.Infrastructure;
using WechatRobot.IntegrationTests.Models;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.IntegrationTests.Groups;

public sealed class GroupConfigurationMySqlTests(MySqlFixture fixture) : IClassFixture<MySqlFixture>
{
    [Fact]
    public async Task Lifecycle_endpoints_enforce_versions_and_archive_blockers()
    {
        await using var factory = new MySqlGroupApiFactory(fixture.ConnectionString);
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        await database.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var robot = new RobotConfigEntity
        {
            Name = $"lifecycle-{Guid.NewGuid():N}",
            WorkToolRobotId = $"lifecycle-{Guid.NewGuid():N}",
            CallbackSecretHash = "test"
        };
        var group = new GroupProfileEntity { RobotConfigId = robot.Id, Name = "生命周期群" };
        database.AddRange(robot, group);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var client = factory.CreateClient();

        var disabled = await client.PostAsJsonAsync(
            $"/api/groups/{group.Id:D}/disable",
            new { expectedStateVersion = 0 },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        var disabledJson = await disabled.Content.ReadFromJsonAsync<JsonElement>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("disabled", disabledJson.GetProperty("state").GetString());
        Assert.Equal(1, disabledJson.GetProperty("stateVersion").GetInt32());

        database.SendCommands.Add(new SendCommandEntity
        {
            RobotConfigId = robot.Id,
            GroupProfileId = group.Id,
            IdempotencyKey = $"block-{Guid.NewGuid():N}",
            PayloadJson = "{}",
            Status = "pending"
        });
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        var blocked = await client.PostAsJsonAsync(
            $"/api/groups/{group.Id:D}/archive",
            new { expectedStateVersion = 1 },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        var blockedJson = await blocked.Content.ReadFromJsonAsync<JsonElement>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("group-active-work", blockedJson.GetProperty("error").GetString());
        Assert.Equal(1, blockedJson.GetProperty("blockers").GetProperty("activeSendCommands").GetInt32());

        database.SendCommands.Remove(database.SendCommands.Local.Single(command => command.GroupProfileId == group.Id));
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        var archived = await client.PostAsJsonAsync(
            $"/api/groups/{group.Id:D}/archive",
            new { expectedStateVersion = 1 },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);

        var currentGroups = await client.GetFromJsonAsync<JsonElement[]>(
            "/api/admin/worktool/groups",
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain(currentGroups!, item => item.GetProperty("id").GetGuid() == group.Id);
        var archivedGroups = await client.GetFromJsonAsync<JsonElement[]>(
            "/api/admin/worktool/groups?status=archived",
            TestContext.Current.CancellationToken);
        var archivedGroup = Assert.Single(
            archivedGroups!,
            item => item.GetProperty("id").GetGuid() == group.Id);
        Assert.Equal("archived", archivedGroup.GetProperty("state").GetString());
        Assert.Equal(2, archivedGroup.GetProperty("stateVersion").GetInt32());
        Assert.Equal(0, archivedGroup.GetProperty("configurationVersion").GetInt32());

        var stale = await client.PostAsJsonAsync(
            $"/api/groups/{group.Id:D}/restore",
            new { expectedStateVersion = 1 },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task Retired_human_agent_routes_return_not_found()
    {
        await using var factory = new MySqlGroupApiFactory(fixture.ConnectionString);
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        await database.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var suffix = Guid.NewGuid().ToString("N");
        var robot = new RobotConfigEntity
        {
            Name = $"eligible-agent-{suffix}",
            WorkToolRobotId = $"eligible-agent-{suffix}",
            CallbackSecretHash = "test"
        };
        var group = new GroupProfileEntity
        {
            RobotConfigId = robot.Id,
            Name = $"eligible-group-{suffix}"
        };
        database.AddRange(robot, group);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);

        using var client = factory.CreateClient();

        using var eligible = await client.GetAsync(
            $"/api/groups/{group.Id:D}/eligible-human-agents",
            TestContext.Current.CancellationToken);
        using var update = await client.PutAsJsonAsync(
            $"/api/groups/{group.Id:D}/human-agents",
            new { userIds = Array.Empty<Guid>() },
            TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, eligible.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, update.StatusCode);
    }

    [Fact]
    public async Task Handoff_pause_policy_is_not_part_of_the_group_configuration_contract()
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
        Assert.False(body.TryGetProperty("handoffPausePolicy", out _));
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
        Assert.Equal("Group", persisted.HandoffPausePolicy);
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
    public async Task Answer_fallback_defaults_are_safe_and_domain_filters_are_normalized()
    {
        await using var factory = new MySqlGroupApiFactory(fixture.ConnectionString);
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        await database.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var robot = new RobotConfigEntity
        {
            Name = $"fallback-{Guid.NewGuid():N}",
            WorkToolRobotId = Guid.NewGuid().ToString("N"),
            CallbackSecretHash = "hash"
        };
        var group = new GroupProfileEntity
        {
            RobotConfigId = robot.Id,
            Name = $"fallback-{Guid.NewGuid():N}"
        };
        database.AddRange(robot, group);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var client = factory.CreateClient();

        var defaults = await client.GetFromJsonAsync<JsonElement>(
            $"/api/groups/{group.Id:D}/configuration",
            TestContext.Current.CancellationToken);
        var defaultFallback = defaults.GetProperty("answerFallback");
        Assert.False(defaultFallback.GetProperty("webSearchEnabled").GetBoolean());
        Assert.False(defaultFallback.GetProperty("modelKnowledgeFallbackEnabled").GetBoolean());
        Assert.Equal("InsufficientEvidence", defaultFallback.GetProperty("finalNoEvidencePolicy").GetString());

        var updated = await client.PutAsJsonAsync(
            $"/api/groups/{group.Id:D}/configuration",
            new
            {
                includeRules = Array.Empty<object>(),
                excludeRules = Array.Empty<object>(),
                boundTagIds = Array.Empty<Guid>(),
                context = new { },
                clearContext = false,
                expectedConfigurationVersion = 0,
                answerFallback = new
                {
                    webSearchEnabled = true,
                    modelKnowledgeFallbackEnabled = true,
                    webSearchShowSources = true,
                    webSearchResultCount = 8,
                    webSearchRecency = "OneWeek",
                    webSearchDomainFilter = "Example.COM; news.example.com",
                    webSearchContentSize = "High",
                    finalNoEvidencePolicy = "Clarification"
                }
            },
            TestContext.Current.CancellationToken);
        updated.EnsureSuccessStatusCode();
        var body = await updated.Content.ReadFromJsonAsync<JsonElement>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(
            "example.com,news.example.com",
            body.GetProperty("answerFallback").GetProperty("webSearchDomainFilter").GetString());

        var invalid = await client.PutAsJsonAsync(
            $"/api/groups/{group.Id:D}/configuration",
            new
            {
                includeRules = Array.Empty<object>(),
                excludeRules = Array.Empty<object>(),
                boundTagIds = Array.Empty<Guid>(),
                context = new { },
                clearContext = false,
                expectedConfigurationVersion = 1,
                answerFallback = new
                {
                    webSearchEnabled = true,
                    modelKnowledgeFallbackEnabled = true,
                    webSearchShowSources = false,
                    webSearchResultCount = 5,
                    webSearchRecency = "NoLimit",
                    webSearchDomainFilter = "https://example.com/path",
                    webSearchContentSize = "Medium",
                    finalNoEvidencePolicy = "InsufficientEvidence"
                }
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
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
