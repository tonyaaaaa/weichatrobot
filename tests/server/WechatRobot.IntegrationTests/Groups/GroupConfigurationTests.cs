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
