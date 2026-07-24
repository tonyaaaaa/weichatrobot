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
            StagedContent = "secret file bytes"u8.ToArray()
        };
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            database.KnowledgeDocuments.Add(document);
            database.KnowledgeDocumentVersions.Add(version);
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
            $"/api/knowledge/documents?query={suffix}&status=failed&page=0&pageSize=200",
            TestContext.Current.CancellationToken);
        Assert.Equal(1, list.GetProperty("page").GetInt32());
        Assert.Equal(100, list.GetProperty("pageSize").GetInt32());
        Assert.Equal(document.Id, Assert.Single(list.GetProperty("items").EnumerateArray())
            .GetProperty("id").GetGuid());

        using var detailResponse = await client.GetAsync(
            $"/api/knowledge/documents/{document.Id:D}",
            TestContext.Current.CancellationToken);
        detailResponse.EnsureSuccessStatusCode();
        var detailJson = await detailResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        using var detail = JsonDocument.Parse(detailJson);
        Assert.Equal(document.Id, detail.RootElement.GetProperty("document").GetProperty("id").GetGuid());
        Assert.Equal(version.Id, Assert.Single(detail.RootElement.GetProperty("versions").EnumerateArray())
            .GetProperty("id").GetGuid());

        using var versionsResponse = await client.GetAsync(
            $"/api/knowledge/documents/{document.Id:D}/versions",
            TestContext.Current.CancellationToken);
        versionsResponse.EnsureSuccessStatusCode();
        Assert.Single((await versionsResponse.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken)).EnumerateArray());

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
