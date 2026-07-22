using System.Net;
using WechatRobot.Infrastructure.Identity;

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

    private HttpClient Client(string role)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", role);
        return client;
    }
}
