using System.Net;
using WechatRobot.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.IntegrationTests.Knowledge;

public sealed class ChunkPreviewEndpointTests : IClassFixture<DocumentUploadApiFactory>
{
    private readonly DocumentUploadApiFactory _factory;
    public ChunkPreviewEndpointTests(DocumentUploadApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Preview_endpoints_enforce_knowledge_operator_policy_server_side()
    {
        var url = $"/api/knowledge/versions/{Guid.NewGuid()}/previews";
        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(url, TestContext.Current.CancellationToken)).StatusCode);
        using var human = Client(SystemRoles.HumanAgent);
        Assert.Equal(HttpStatusCode.Forbidden, (await human.GetAsync(url, TestContext.Current.CancellationToken)).StatusCode);
        using var knowledge = Client(SystemRoles.KnowledgeOperator);
        Assert.Equal(HttpStatusCode.NotFound, (await knowledge.GetAsync(url, TestContext.Current.CancellationToken)).StatusCode);
        using var admin = Client(SystemRoles.Admin);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(url, TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Unsupported_non_backtracking_regex_is_a_validation_response()
    {
        Guid versionId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            var document = new KnowledgeDocumentEntity { Id = Guid.NewGuid(), Title = "regex", Status = "uploaded" };
            versionId = Guid.NewGuid();
            db.KnowledgeDocuments.Add(document);
            db.KnowledgeDocumentVersions.Add(new KnowledgeDocumentVersionEntity { Id = versionId, KnowledgeDocumentId = document.Id, Version = 1,
                OriginalFileName = "regex.txt", SafeFileName = "source.txt", ContentType = "text/plain", Sha256 = Guid.NewGuid().ToString("N").PadRight(64, '0'),
                ObjectKey = "regex", PublicUrl = "https://example.test/regex", Status = "uploaded" });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        using var client = Client(SystemRoles.KnowledgeOperator);
        using var response = await client.PostAsJsonAsync($"/api/knowledge/versions/{versionId}/previews/generate", new
        {
            expectedRevision = 0,
            policy = new { kind = 2, targetTokens = 10, overlapTokens = 0, maximumTokens = 10, regexPattern = "(?=a)" }
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Chunk_policy_boundary_accepts_each_discriminator_and_rejects_mixed_or_unknown_fields()
    {
        using var client = Client(SystemRoles.KnowledgeOperator);
        var versionId = Guid.NewGuid();
        var url = $"/api/knowledge/versions/{versionId:D}/previews/generate";

        foreach (var policy in new object[]
        {
            new { kind = "smart", targetTokens = 800, overlapTokens = 120, maximumTokens = 1000 },
            new { kind = "separator", targetTokens = 800, overlapTokens = 120, maximumTokens = 1000, separator = "\n---\n" },
            new { kind = "regex", targetTokens = 800, overlapTokens = 120, maximumTokens = 1000, regexPattern = "\\n#{1,3}\\s" },
            new { kind = "qa", targetTokens = 800, overlapTokens = 120, maximumTokens = 1000,
                qaEntries = new[] { new { question = "如何退款？", synonyms = new[] { "怎么退" }, answer = "联系人工客服。" } } }
        })
        {
            var response = await client.PostAsJsonAsync(url,
                new { expectedRevision = 0, policy }, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        var unknown = await client.PostAsJsonAsync(url, new
        {
            expectedRevision = 0,
            policy = new { kind = "invented", targetTokens = 800, overlapTokens = 120, maximumTokens = 1000 }
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        var mixed = await client.PostAsJsonAsync(url, new
        {
            expectedRevision = 0,
            policy = new { kind = "separator", targetTokens = 800, overlapTokens = 120, maximumTokens = 1000,
                separator = "\n", regexPattern = "\\n+" }
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, mixed.StatusCode);
    }

    private HttpClient Client(string role)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", role);
        return client;
    }
}
