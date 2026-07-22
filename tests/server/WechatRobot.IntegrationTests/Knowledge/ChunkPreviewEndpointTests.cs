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

    private HttpClient Client(string role)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", role);
        return client;
    }
}
