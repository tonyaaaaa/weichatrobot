using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.IntegrationTests.Knowledge;

public sealed class KnowledgeDocumentAdministrationEndpointTests : IClassFixture<DocumentUploadApiFactory>
{
    private readonly DocumentUploadApiFactory _factory;

    public KnowledgeDocumentAdministrationEndpointTests(DocumentUploadApiFactory factory) =>
        _factory = factory;

    [Fact]
    public async Task Query_routes_enforce_knowledge_operator_policy()
    {
        using var anonymous = _factory.CreateClient();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync(
                "/api/knowledge/documents",
                TestContext.Current.CancellationToken)).StatusCode);

        using var human = Client(SystemRoles.HumanAgent);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await human.GetAsync(
                "/api/knowledge/documents",
                TestContext.Current.CancellationToken)).StatusCode);

        using var knowledge = Client(SystemRoles.KnowledgeOperator);
        Assert.Equal(
            HttpStatusCode.OK,
            (await knowledge.GetAsync(
                "/api/knowledge/documents",
                TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await knowledge.GetAsync(
                $"/api/knowledge/documents/{Guid.NewGuid():D}",
                TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task List_detail_and_versions_return_safe_persisted_metadata()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var document = new KnowledgeDocumentEntity
        {
            Title = $"Endpoint {suffix}",
            Status = "failed",
            StateVersion = 2
        };
        var version = new KnowledgeDocumentVersionEntity
        {
            KnowledgeDocumentId = document.Id,
            Version = 1,
            OriginalFileName = "source.txt",
            SafeFileName = "source.txt",
            ContentType = "text/plain",
            Sha256 = Guid.NewGuid().ToString("N").PadRight(64, '0'),
            ObjectKey = "secret/object-key",
            PublicUrl = "https://public.example.test/source.txt?signature=must-not-leak",
            Status = "failed",
            FailureReason = "Object storage upload failed; retry is available.",
            StagedContent = "secret file bytes"u8.ToArray(),
            SourceKind = "PrivateChatDirect",
            SourceActorDisplayName = "接口测试成员"
        };
        document.ActiveVersionId = version.Id;
        var tag = new KnowledgeTagEntity
        {
            Name = $"接口标签 {suffix}",
            NormalizedName = $"接口标签 {suffix}".ToUpperInvariant()
        };
        var chunk = new KnowledgeChunkEntity
        {
            KnowledgeDocumentVersionId = version.Id,
            Sequence = 1,
            Text = "endpoint tag binding",
            Status = "approved"
        };
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            database.KnowledgeDocuments.Add(document);
            database.KnowledgeDocumentVersions.Add(version);
            database.KnowledgeTags.Add(tag);
            database.KnowledgeChunks.Add(chunk);
            database.KnowledgeChunkTags.Add(new KnowledgeChunkTagEntity
            {
                KnowledgeChunkId = chunk.Id,
                KnowledgeTagId = tag.Id
            });
            database.DurableJobs.Add(new DurableJobEntity
            {
                JobType = "UploadKnowledgeDocument",
                Status = "retrying",
                PayloadJson = JsonSerializer.Serialize(new
                {
                    documentId = document.Id,
                    versionId = version.Id,
                    authorization = "Bearer must-not-leak"
                })
            });
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var client = Client(SystemRoles.KnowledgeOperator);
        var list = await client.GetFromJsonAsync<JsonElement>(
            $"/api/knowledge/documents?query={suffix}&status=failed" +
            $"&sourceKind=PrivateChatDirect&tagId={tag.Id:D}&page=0&pageSize=200",
            TestContext.Current.CancellationToken);
        Assert.Equal(1, list.GetProperty("page").GetInt32());
        Assert.Equal(100, list.GetProperty("pageSize").GetInt32());
        var listItem = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal(document.Id, listItem.GetProperty("id").GetGuid());
        Assert.Equal("PrivateChatDirect", listItem.GetProperty("sourceKind").GetString());
        Assert.Equal("接口测试成员", listItem.GetProperty("sourceActorDisplayName").GetString());
        var listTag = Assert.Single(listItem.GetProperty("tags").EnumerateArray());
        Assert.Equal(tag.Id, listTag.GetProperty("id").GetGuid());
        Assert.Equal(tag.Name, listTag.GetProperty("name").GetString());

        var excluded = await client.GetFromJsonAsync<JsonElement>(
            $"/api/knowledge/documents?query={suffix}&sourceKind=DocumentUpload" +
            $"&tagId={tag.Id:D}&page=1&pageSize=20",
            TestContext.Current.CancellationToken);
        Assert.Equal(0, excluded.GetProperty("total").GetInt32());
        Assert.Empty(excluded.GetProperty("items").EnumerateArray());

        using var detailResponse = await client.GetAsync(
            $"/api/knowledge/documents/{document.Id:D}",
            TestContext.Current.CancellationToken);
        detailResponse.EnsureSuccessStatusCode();
        var detailJson = await detailResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        using var detail = JsonDocument.Parse(detailJson);
        Assert.Equal(document.Id, detail.RootElement.GetProperty("document").GetProperty("id").GetGuid());
        var detailVersion = Assert.Single(detail.RootElement.GetProperty("versions").EnumerateArray());
        Assert.Equal(version.Id, detailVersion.GetProperty("id").GetGuid());
        var detailTag = Assert.Single(detailVersion.GetProperty("tags").EnumerateArray());
        Assert.Equal(tag.Id, detailTag.GetProperty("id").GetGuid());
        Assert.Equal(tag.Name, detailTag.GetProperty("name").GetString());

        using var versionsResponse = await client.GetAsync(
            $"/api/knowledge/documents/{document.Id:D}/versions",
            TestContext.Current.CancellationToken);
        versionsResponse.EnsureSuccessStatusCode();
        var versionItem = Assert.Single((await versionsResponse.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken)).EnumerateArray());
        var versionTag = Assert.Single(versionItem.GetProperty("tags").EnumerateArray());
        Assert.Equal(tag.Id, versionTag.GetProperty("id").GetGuid());

        var combined = detailJson + await versionsResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain("stagedContent", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("objectKey", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("payloadJson", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("must-not-leak", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret file bytes", combined, StringComparison.OrdinalIgnoreCase);
    }

    private HttpClient Client(string role)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", role);
        return client;
    }
}
