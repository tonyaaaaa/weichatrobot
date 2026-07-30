using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WechatRobot.IntegrationTests.Models;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.IntegrationTests.Groups;

public sealed class GroupConfigurationTests : IClassFixture<ModelConfigurationApiFactory>
{
    private readonly ModelConfigurationApiFactory _factory;

    public GroupConfigurationTests(ModelConfigurationApiFactory factory) => _factory = factory;

    [Fact]
    public void Group_configuration_routes_are_mapped_and_require_admin_policy()
    {
        var routes = _factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/groups", StringComparison.Ordinal) == true
                || endpoint.RoutePattern.RawText == "/api/group-rules/preview")
            .ToArray();

        Assert.Contains(routes, endpoint => endpoint.RoutePattern.RawText == "/api/groups/{id:guid}/configuration");
        Assert.Contains(routes, endpoint => endpoint.RoutePattern.RawText == "/api/group-rules/preview");
        Assert.All(routes, endpoint => Assert.Contains(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(), data => data.Policy == SystemRoles.Admin));
    }

    [Fact]
    public async Task Preview_applies_exclude_precedence_and_rejects_invalid_or_expensive_regexes()
    {
        using var client = _factory.CreateClient();
        var valid = await client.PostAsJsonAsync("/api/group-rules/preview", new
        {
            includeRules = new[] { new { pattern = "技术", patternKind = "contains", ignoreCase = true } },
            excludeRules = new[] { new { pattern = "技术测试群", patternKind = "exact", ignoreCase = true } },
            groupNames = new[] { "技术支持群", "技术测试群", "行政群" }
        }, TestContext.Current.CancellationToken);

        valid.EnsureSuccessStatusCode();
        using (var document = JsonDocument.Parse(await valid.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)))
        {
            var results = document.RootElement.GetProperty("results").EnumerateArray().ToArray();
            Assert.Equal("技术支持群", results.Single(result => result.GetProperty("isMatch").GetBoolean()).GetProperty("groupName").GetString());
            Assert.Equal("技术测试群", results.Single(result => result.GetProperty("isExcluded").GetBoolean()).GetProperty("groupName").GetString());
        }

        var invalid = await client.PostAsJsonAsync("/api/group-rules/preview", new
        {
            includeRules = new[] { new { pattern = "(", patternKind = "regex", ignoreCase = true } },
            excludeRules = Array.Empty<object>(),
            groupNames = new[] { "技术支持群" }
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task Configuration_returns_nullable_overrides_effective_defaults_and_bound_or_global_public_tags()
    {
        var groupId = await SeedGroupAndTagsAsync();
        using var client = _factory.CreateClient();
        var configured = await client.PutAsJsonAsync($"/api/groups/{groupId}/configuration", new
        {
            includeRules = new[] { new { pattern = "技术", patternKind = "contains", ignoreCase = true } },
            excludeRules = Array.Empty<object>(),
            boundTagIds = new[] { ScopedTagId },
            context = new { senderIsolated = (bool?)null, historyTurns = (int?)null, idleTimeoutMinutes = (int?)null, tokenCap = (int?)null, summaryEnabled = (bool?)null, includeBotHistory = (bool?)null },
            clearContext = false,
            expectedConfigurationVersion = 0
        }, TestContext.Current.CancellationToken);

        configured.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await configured.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var context = document.RootElement.GetProperty("context");
        Assert.Equal(6, context.GetProperty("effective").GetProperty("historyTurns").GetInt32());
        Assert.Equal(30, context.GetProperty("effective").GetProperty("idleTimeoutMinutes").GetInt32());
        Assert.False(context.GetProperty("effective").GetProperty("senderIsolated").GetBoolean());
        Assert.Equal("any-bound-tag-or-global-public", document.RootElement.GetProperty("tagVisibility").GetString());
        var allowedTagIds = document.RootElement.GetProperty("allowedTagIds").EnumerateArray().Select(value => value.GetGuid()).ToHashSet();
        Assert.Contains(ScopedTagId, allowedTagIds);
        Assert.Contains(GlobalPublicTagId, allowedTagIds);
        Assert.DoesNotContain(UnboundPrivateTagId, allowedTagIds);
        Assert.Equal(JsonValueKind.Null, context.GetProperty("configured").GetProperty("historyTurns").ValueKind);
    }

    [Fact]
    public async Task Configuration_does_not_expose_a_redundant_global_public_tag_as_group_bound()
    {
        var groupId = await SeedGroupAndTagsAsync();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            database.GroupProfileTags.Add(new GroupProfileTagEntity
            {
                GroupProfileId = groupId,
                KnowledgeTagId = GlobalPublicTagId
            });
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var client = _factory.CreateClient();
        var configuration = await client.GetFromJsonAsync<JsonElement>(
            $"/api/groups/{groupId}/configuration",
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain(
            GlobalPublicTagId,
            configuration.GetProperty("boundTagIds").EnumerateArray().Select(value => value.GetGuid()));
        var globalTag = configuration.GetProperty("availableTags").EnumerateArray()
            .Single(tag => tag.GetProperty("id").GetGuid() == GlobalPublicTagId);
        Assert.False(globalTag.GetProperty("isBound").GetBoolean());
        Assert.Contains(
            GlobalPublicTagId,
            configuration.GetProperty("allowedTagIds").EnumerateArray().Select(value => value.GetGuid()));
    }

    [Fact]
    public async Task Configuration_returns_authoritative_read_only_group_identity()
    {
        var groupId = await SeedGroupAndTagsAsync();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            var group = await database.GroupProfiles.SingleAsync(
                item => item.Id == groupId,
                TestContext.Current.CancellationToken);
            group.WorkToolGroupRemark = "售后支持";
            group.RegistrationSource = "WorkToolImport";
            group.IsEnabled = false;
            group.ArchivedAtUtc = DateTime.UtcNow;
            group.StateVersion = 3;
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var client = _factory.CreateClient();
        var response = await client.GetFromJsonAsync<JsonElement>(
            $"/api/groups/{groupId}/configuration",
            TestContext.Current.CancellationToken);
        var identity = response.GetProperty("identity");

        Assert.Equal("groups-test-robot", identity.GetProperty("robotName").GetString());
        Assert.Equal("售后支持", identity.GetProperty("workToolGroupRemark").GetString());
        Assert.Equal("WorkToolImport", identity.GetProperty("registrationSource").GetString());
        Assert.Equal("archived", identity.GetProperty("state").GetString());
        Assert.False(identity.GetProperty("isEnabled").GetBoolean());
        Assert.Equal(3, identity.GetProperty("stateVersion").GetInt32());
    }

    [Fact]
    public async Task Configuration_returns_default_chat_capability_and_bounded_group_memory_summary()
    {
        var groupId = await SeedGroupAndTagsAsync();
        var otherGroupId = await SeedGroupAndTagsAsync();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            var existingDefaults = await database.ModelConfigs
                .Where(model => model.ConfigurationType == "chat" && model.IsDefault)
                .ToArrayAsync(TestContext.Current.CancellationToken);
            foreach (var existing in existingDefaults) existing.IsDefault = false;

            var suffix = Guid.NewGuid().ToString("N");
            database.ModelConfigs.Add(new ModelConfigEntity
            {
                Name = $"group-chat-{suffix}",
                NormalizedName = $"GROUP-CHAT-{suffix}".ToUpperInvariant(),
                Provider = "test",
                ConfigurationType = "chat",
                BaseUrl = "https://model.example.test",
                Model = "test-chat",
                IsEnabled = true,
                IsDefault = true,
                ConnectionStatus = ModelConnectionStatus.Succeeded,
                WebSearchMode = "ZaiChatCompletions"
            });
            database.MemoryEntries.AddRange(
                new MemoryEntryEntity
                {
                    GroupProfileId = groupId, ScopeType = "Group", Status = "active",
                    Content = "group", NormalizedKey = "group", ValidFromUtc = DateTime.UtcNow
                },
                new MemoryEntryEntity
                {
                    GroupProfileId = groupId, ScopeType = "User", Status = "active",
                    Content = "member", NormalizedKey = "member", ValidFromUtc = DateTime.UtcNow
                },
                new MemoryEntryEntity
                {
                    GroupProfileId = groupId, ScopeType = "Group", Status = "forgotten",
                    Content = "forgotten", NormalizedKey = "forgotten", ValidFromUtc = DateTime.UtcNow
                },
                new MemoryEntryEntity
                {
                    GroupProfileId = otherGroupId, ScopeType = "Group", Status = "active",
                    Content = "other", NormalizedKey = "other", ValidFromUtc = DateTime.UtcNow
                });
            database.MemoryCandidates.AddRange(
                new MemoryCandidateEntity
                {
                    GroupProfileId = groupId, ScopeType = "User", Status = "pending",
                    Content = "pending", NormalizedKey = "pending", Fingerprint = $"pending-{suffix}",
                    ScopeHash = $"scope-{suffix}", CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow
                },
                new MemoryCandidateEntity
                {
                    GroupProfileId = groupId, ScopeType = "User", Status = "rejected",
                    Content = "rejected", NormalizedKey = "rejected", Fingerprint = $"rejected-{suffix}",
                    ScopeHash = $"scope-rejected-{suffix}", CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow
                });
            database.DurableJobs.AddRange(
                new DurableJobEntity
                {
                    GroupProfileId = groupId, JobType = "ExtractConversationMemory",
                    PayloadJson = "{}", Status = "pending"
                },
                new DurableJobEntity
                {
                    GroupProfileId = groupId, JobType = "MaintainLongTermMemory",
                    PayloadJson = "{}", Status = "retrying"
                },
                new DurableJobEntity
                {
                    GroupProfileId = groupId, JobType = "IndexMemoryEntry",
                    PayloadJson = "{}", Status = "completed"
                },
                new DurableJobEntity
                {
                    GroupProfileId = otherGroupId, JobType = "ExtractConversationMemory",
                    PayloadJson = "{}", Status = "pending"
                });
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var client = _factory.CreateClient();
        var response = await client.GetFromJsonAsync<JsonElement>(
            $"/api/groups/{groupId}/configuration",
            TestContext.Current.CancellationToken);

        var model = response.GetProperty("defaultChatModel");
        Assert.True(model.GetProperty("isConfigured").GetBoolean());
        Assert.True(model.GetProperty("canUseWebSearch").GetBoolean());
        Assert.Equal("none", model.GetProperty("unavailableReason").GetString());
        Assert.Equal("ZaiChatCompletions", model.GetProperty("webSearchMode").GetString());

        var memory = response.GetProperty("memorySummary");
        Assert.Equal(1, memory.GetProperty("activeGroupMemoryCount").GetInt32());
        Assert.Equal(1, memory.GetProperty("activeMemberMemoryCount").GetInt32());
        Assert.Equal(1, memory.GetProperty("pendingCandidateCount").GetInt32());
        Assert.Equal(2, memory.GetProperty("pendingOrRunningJobCount").GetInt32());
    }

    [Fact]
    public async Task Configuration_reports_disabled_web_search_mode_as_not_enabled()
    {
        var groupId = await SeedGroupAndTagsAsync();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            var existingDefaults = await database.ModelConfigs
                .Where(model => model.ConfigurationType == "chat" && model.IsDefault)
                .ToArrayAsync(TestContext.Current.CancellationToken);
            foreach (var existing in existingDefaults) existing.IsDefault = false;

            var suffix = Guid.NewGuid().ToString("N");
            database.ModelConfigs.Add(new ModelConfigEntity
            {
                Name = $"group-chat-none-{suffix}",
                NormalizedName = $"GROUP-CHAT-NONE-{suffix}".ToUpperInvariant(),
                Provider = "test",
                ConfigurationType = "chat",
                BaseUrl = "https://model.example.test",
                Model = "test-chat",
                IsEnabled = true,
                IsDefault = true,
                ConnectionStatus = ModelConnectionStatus.Succeeded,
                WebSearchMode = "None"
            });
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var client = _factory.CreateClient();
        var response = await client.GetFromJsonAsync<JsonElement>(
            $"/api/groups/{groupId}/configuration",
            TestContext.Current.CancellationToken);
        var model = response.GetProperty("defaultChatModel");

        Assert.False(model.GetProperty("canUseWebSearch").GetBoolean());
        Assert.Equal("not_enabled", model.GetProperty("unavailableReason").GetString());
    }

    [Fact]
    public async Task Disabled_bound_tag_is_exposed_for_removal_but_cannot_be_newly_or_still_bound_on_save()
    {
        var groupId = await SeedGroupAndTagsAsync();
        using var client = _factory.CreateClient();
        var initiallyBound = await client.PutAsJsonAsync($"/api/groups/{groupId}/configuration", new
        {
            includeRules = Array.Empty<object>(), excludeRules = Array.Empty<object>(), boundTagIds = new[] { ScopedTagId },
            context = new { senderIsolated = (bool?)null, historyTurns = (int?)null, idleTimeoutMinutes = (int?)null, tokenCap = (int?)null, summaryEnabled = (bool?)null, includeBotHistory = (bool?)null }, clearContext = false,
            expectedConfigurationVersion = 0
        }, TestContext.Current.CancellationToken);
        initiallyBound.EnsureSuccessStatusCode();

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            (await database.KnowledgeTags.SingleAsync(tag => tag.Id == ScopedTagId, TestContext.Current.CancellationToken)).IsEnabled = false;
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var configuration = await client.GetFromJsonAsync<JsonElement>($"/api/groups/{groupId}/configuration", TestContext.Current.CancellationToken);
        var disabledBoundTag = configuration.GetProperty("availableTags").EnumerateArray().Single(tag => tag.GetProperty("id").GetGuid() == ScopedTagId);
        Assert.False(disabledBoundTag.GetProperty("isEnabled").GetBoolean());
        Assert.True(disabledBoundTag.GetProperty("isBound").GetBoolean());

        var removeDisabledBinding = await client.PutAsJsonAsync($"/api/groups/{groupId}/configuration", new
        {
            includeRules = Array.Empty<object>(), excludeRules = Array.Empty<object>(), boundTagIds = Array.Empty<Guid>(),
            context = new { senderIsolated = (bool?)null, historyTurns = (int?)null, idleTimeoutMinutes = (int?)null, tokenCap = (int?)null, summaryEnabled = (bool?)null, includeBotHistory = (bool?)null }, clearContext = false,
            expectedConfigurationVersion = 1
        }, TestContext.Current.CancellationToken);
        removeDisabledBinding.EnsureSuccessStatusCode();

        var addDisabledTag = await client.PutAsJsonAsync($"/api/groups/{groupId}/configuration", new
        {
            includeRules = Array.Empty<object>(), excludeRules = Array.Empty<object>(), boundTagIds = new[] { ScopedTagId },
            context = new { senderIsolated = (bool?)null, historyTurns = (int?)null, idleTimeoutMinutes = (int?)null, tokenCap = (int?)null, summaryEnabled = (bool?)null, includeBotHistory = (bool?)null }, clearContext = false,
            expectedConfigurationVersion = 2
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, addDisabledTag.StatusCode);
    }

    [Fact]
    public async Task Every_configuration_write_requires_the_current_version_and_returns_the_incremented_version()
    {
        var groupId = await SeedGroupAndTagsAsync();
        using var client = _factory.CreateClient();
        var body = new
        {
            includeRules = Array.Empty<object>(),
            excludeRules = Array.Empty<object>(),
            boundTagIds = Array.Empty<Guid>(),
            context = new { senderIsolated = (bool?)null, historyTurns = (int?)null, idleTimeoutMinutes = (int?)null,
                tokenCap = (int?)null, summaryEnabled = (bool?)null, includeBotHistory = (bool?)null },
            clearContext = false
        };

        var missing = await client.PutAsJsonAsync(
            $"/api/groups/{groupId:D}/configuration", body, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        var saved = await client.PutAsJsonAsync($"/api/groups/{groupId:D}/configuration", new
        {
            body.includeRules, body.excludeRules, body.boundTagIds, body.context, body.clearContext,
            expectedConfigurationVersion = 0
        }, TestContext.Current.CancellationToken);
        saved.EnsureSuccessStatusCode();
        Assert.Equal(1, (await saved.Content.ReadFromJsonAsync<JsonElement>(
            cancellationToken: TestContext.Current.CancellationToken)).GetProperty("configurationVersion").GetInt32());

        var stale = await client.PutAsJsonAsync($"/api/groups/{groupId:D}/configuration", new
        {
            body.includeRules, body.excludeRules, body.boundTagIds, body.context, body.clearContext,
            expectedConfigurationVersion = 0
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(1, (await stale.Content.ReadFromJsonAsync<JsonElement>(
            cancellationToken: TestContext.Current.CancellationToken)).GetProperty("currentVersion").GetInt32());
    }

    private static readonly Guid RobotId = Guid.Parse("00000000-0000-0000-0000-000000000801");
    private static readonly Guid ScopedTagId = Guid.Parse("00000000-0000-0000-0000-000000000802");
    private static readonly Guid GlobalPublicTagId = Guid.Parse("00000000-0000-0000-0000-000000000803");
    private static readonly Guid UnboundPrivateTagId = Guid.Parse("00000000-0000-0000-0000-000000000804");

    private async Task<Guid> SeedGroupAndTagsAsync()
    {
        var groupId = Guid.NewGuid();
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        if (!await database.RobotConfigs.AnyAsync(robot => robot.Id == RobotId, TestContext.Current.CancellationToken))
        {
            database.RobotConfigs.Add(new RobotConfigEntity { Id = RobotId, Name = "groups-test-robot", WorkToolRobotId = "groups-test-robot", CallbackSecretHash = "hash" });
        }

        foreach (var tag in new[]
        {
            new KnowledgeTagEntity { Id = ScopedTagId, Name = "技术", NormalizedName = "技术" },
            new KnowledgeTagEntity { Id = GlobalPublicTagId, Name = "公开", NormalizedName = "公开", IsGlobalPublic = true },
            new KnowledgeTagEntity { Id = UnboundPrivateTagId, Name = "内部", NormalizedName = "内部" }
        })
        {
            var existing = await database.KnowledgeTags.SingleOrDefaultAsync(existing => existing.Id == tag.Id, TestContext.Current.CancellationToken);
            if (existing is null)
            {
                database.KnowledgeTags.Add(tag);
            }
            else
            {
                existing.Name = tag.Name;
                existing.NormalizedName = tag.NormalizedName;
                existing.IsEnabled = tag.IsEnabled;
                existing.IsGlobalPublic = tag.IsGlobalPublic;
            }
        }

        database.GroupProfiles.Add(new GroupProfileEntity { Id = groupId, RobotConfigId = RobotId, ExternalGroupId = groupId.ToString("N"), Name = "技术支持群" });
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        return groupId;
    }
}
