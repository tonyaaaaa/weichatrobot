using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.IntegrationTests.Knowledge;

public sealed class KnowledgeTagEndpointTests : IClassFixture<DocumentUploadApiFactory>
{
    private readonly DocumentUploadApiFactory _factory;

    public KnowledgeTagEndpointTests(DocumentUploadApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Knowledge_operator_can_manage_tags_but_only_admin_can_delete()
    {
        using var anonymous = _factory.CreateClient();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync(
                "/api/knowledge/tags",
                TestContext.Current.CancellationToken)).StatusCode);

        using var human = Client(SystemRoles.HumanAgent);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await human.GetAsync(
                "/api/knowledge/tags",
                TestContext.Current.CancellationToken)).StatusCode);

        var suffix = Guid.NewGuid().ToString("N");
        using var knowledge = Client(SystemRoles.KnowledgeOperator);
        using var created = await knowledge.PostAsJsonAsync(
            "/api/knowledge/tags",
            new { name = $" Tag {suffix} ", isGlobalPublic = false },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var createdTag = await created.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);
        var id = createdTag.GetProperty("id").GetGuid();
        Assert.Equal($"Tag {suffix}", createdTag.GetProperty("name").GetString());

        using var listed = await knowledge.GetAsync(
            $"/api/knowledge/tags?query={suffix}&page=1&pageSize=10",
            TestContext.Current.CancellationToken);
        listed.EnsureSuccessStatusCode();
        var page = await listed.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);
        Assert.Equal(id, Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());

        using var updated = await knowledge.PutAsJsonAsync(
            $"/api/knowledge/tags/{id:D}",
            new { name = $"Renamed {suffix}", isGlobalPublic = true, expectedVersion = 0 },
            TestContext.Current.CancellationToken);
        updated.EnsureSuccessStatusCode();
        var updatedTag = await updated.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);
        Assert.Equal(1, updatedTag.GetProperty("version").GetInt32());

        using var disabled = await knowledge.PatchAsJsonAsync(
            $"/api/knowledge/tags/{id:D}/enabled",
            new { isEnabled = false, expectedVersion = 1 },
            TestContext.Current.CancellationToken);
        disabled.EnsureSuccessStatusCode();
        var disabledTag = await disabled.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);
        Assert.False(disabledTag.GetProperty("isEnabled").GetBoolean());
        Assert.Equal(2, disabledTag.GetProperty("version").GetInt32());

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await knowledge.DeleteAsync(
                $"/api/knowledge/tags/{id:D}?expectedVersion=2",
                TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Mutation_without_stable_actor_is_unauthorized_before_write()
    {
        var suffix = Guid.NewGuid().ToString("N");
        using var client = Client(SystemRoles.KnowledgeOperator, withoutName: true);

        using var response = await client.PostAsJsonAsync(
            "/api/knowledge/tags",
            new { name = $"No Actor {suffix}", isGlobalPublic = false },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        Assert.False(await database.KnowledgeTags.AnyAsync(
            tag => tag.NormalizedName.Contains(suffix.ToUpperInvariant()),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Admin_delete_requires_current_version_and_writes_sanitized_audit()
    {
        var tag = await SeedTagAsync();
        using var admin = Client(SystemRoles.Admin);

        using var stale = await admin.DeleteAsync(
            $"/api/knowledge/tags/{tag.Id:D}?expectedVersion={tag.Version + 1}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var staleBody = await stale.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);
        Assert.Equal(
            "knowledge-tag-concurrency-conflict",
            staleBody.GetProperty("error").GetString());
        Assert.Equal(tag.Version, staleBody.GetProperty("current").GetProperty("version").GetInt32());

        using var deleted = await admin.DeleteAsync(
            $"/api/knowledge/tags/{tag.Id:D}?expectedVersion={tag.Version}",
            TestContext.Current.CancellationToken);
        deleted.EnsureSuccessStatusCode();

        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        Assert.False(await database.KnowledgeTags.AnyAsync(
            item => item.Id == tag.Id,
            TestContext.Current.CancellationToken));
        var audit = await database.AdministrationAudits.SingleAsync(
            item => item.Action == "knowledge-tag.delete" &&
                    item.TargetId == tag.Id.ToString("D"),
            TestContext.Current.CancellationToken);
        using var detail = JsonDocument.Parse(audit.SanitizedDetailJson);
        Assert.Equal(tag.Name, detail.RootElement.GetProperty("before").GetProperty("name").GetString());
        Assert.Equal(tag.Version, detail.RootElement.GetProperty("before").GetProperty("version").GetInt32());
    }

    [Theory]
    [InlineData("group")]
    [InlineData("chunk")]
    [InlineData("review")]
    [InlineData("index-job")]
    public async Task Referenced_tag_cannot_be_physically_deleted(string referenceKind)
    {
        var tag = await SeedTagAsync(referenceKind);
        using var admin = Client(SystemRoles.Admin);

        using var response = await admin.DeleteAsync(
            $"/api/knowledge/tags/{tag.Id:D}?expectedVersion={tag.Version}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);
        Assert.Equal("knowledge-tag-referenced", body.GetProperty("error").GetString());
        Assert.Contains(
            body.GetProperty("references").EnumerateObject(),
            property => property.Value.ValueKind == JsonValueKind.Number &&
                        property.Value.GetInt32() > 0);
    }

    private HttpClient Client(string role, bool withoutName = false)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", role);
        if (withoutName)
        {
            client.DefaultRequestHeaders.Add("X-Test-No-Name", "true");
        }

        return client;
    }

    private async Task<KnowledgeTagEntity> SeedTagAsync(string? referenceKind = null)
    {
        var name = $"Delete {Guid.NewGuid():N}";
        var tag = new KnowledgeTagEntity
        {
            Name = name,
            NormalizedName = name.ToUpperInvariant(),
            Version = 3
        };
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        database.KnowledgeTags.Add(tag);
        switch (referenceKind)
        {
            case "group":
                database.GroupProfileTags.Add(new GroupProfileTagEntity
                {
                    GroupProfileId = Guid.NewGuid(),
                    KnowledgeTagId = tag.Id
                });
                break;
            case "chunk":
                database.KnowledgeChunkTags.Add(new KnowledgeChunkTagEntity
                {
                    KnowledgeChunkId = Guid.NewGuid(),
                    KnowledgeTagId = tag.Id
                });
                break;
            case "review":
                database.KnowledgeReviews.Add(new KnowledgeReviewEntity
                {
                    KnowledgeCandidateId = Guid.NewGuid(),
                    Decision = "approved",
                    TagIdsJson = JsonSerializer.Serialize(new[] { tag.Id }),
                    IdempotencyKey = Guid.NewGuid().ToString("N")
                });
                break;
            case "index-job":
                database.KnowledgeIndexJobs.Add(new KnowledgeIndexJobEntity
                {
                    PendingTagIdsJson = JsonSerializer.Serialize(new[] { tag.Id })
                });
                break;
        }

        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        return tag;
    }
}
