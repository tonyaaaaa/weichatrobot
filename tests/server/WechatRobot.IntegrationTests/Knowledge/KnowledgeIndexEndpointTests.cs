using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.IntegrationTests.Models;

namespace WechatRobot.IntegrationTests.Knowledge;

public sealed class KnowledgeIndexEndpointTests : IClassFixture<ModelConfigurationApiFactory>
{
    private readonly ModelConfigurationApiFactory _factory;
    public KnowledgeIndexEndpointTests(ModelConfigurationApiFactory factory) => _factory = factory;

    [Fact]
    public void Every_index_operation_route_requires_server_side_knowledge_operator_policy()
    {
        var routes = _factory.Services.GetServices<EndpointDataSource>().SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.Contains("/api/knowledge/", StringComparison.Ordinal) == true &&
                (endpoint.RoutePattern.RawText.Contains("index", StringComparison.Ordinal) || endpoint.RoutePattern.RawText.Contains("disable", StringComparison.Ordinal)))
            .ToArray();

        Assert.Contains(routes, endpoint => endpoint.RoutePattern.RawText!.EndsWith("/index", StringComparison.Ordinal));
        Assert.Contains(routes, endpoint => endpoint.RoutePattern.RawText!.EndsWith("/reindex", StringComparison.Ordinal));
        Assert.Contains(routes, endpoint => endpoint.RoutePattern.RawText!.EndsWith("/retry", StringComparison.Ordinal));
        Assert.Contains(routes, endpoint => endpoint.RoutePattern.RawText!.EndsWith("/disable", StringComparison.Ordinal));
        Assert.Contains(routes, endpoint => endpoint.RoutePattern.RawText!.EndsWith("/index-status", StringComparison.Ordinal));
        Assert.All(routes, endpoint => Assert.Contains(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(), data => data.Policy == SystemRoles.KnowledgeOperator));
    }
}
