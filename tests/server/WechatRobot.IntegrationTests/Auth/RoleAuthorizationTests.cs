using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.IntegrationTests.Infrastructure;

namespace WechatRobot.IntegrationTests.Auth;

public sealed class RoleAuthorizationTests : IClassFixture<RoleAuthorizationApiFactory>
{
    private const string SigningKey = "integration-tests-signing-key-must-be-at-least-32-bytes";
    private readonly RoleAuthorizationApiFactory _factory;

    public RoleAuthorizationTests(RoleAuthorizationApiFactory factory) => _factory = factory;

    [Fact]
    public void Auth_probe_route_is_mapped()
    {
        var routes = _factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToArray();

        Assert.Contains("/api/auth/probe/knowledge", routes);
        Assert.Contains("/api/handoffs/manual", routes);
        Assert.Contains("/api/handoffs/", routes);
        Assert.Contains("/api/handoffs/{id:guid}", routes);
        Assert.Contains("/api/handoffs/{id:guid}/messages", routes);
        Assert.Contains("/api/handoffs/{id:guid}/transitions", routes);
        Assert.Contains("/api/knowledge/candidates/", routes);
        Assert.Contains("/api/knowledge/candidates/{id:guid}", routes);
        Assert.Contains("/api/knowledge/candidates/{id:guid}/reviews", routes);
    }

    [Fact]
    public async Task Anonymous_request_to_protected_probe_is_unauthorized()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/auth/probe/knowledge", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Human_agent_request_to_knowledge_probe_is_forbidden()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(SystemRoles.HumanAgent));

        var response = await client.GetAsync("/api/auth/probe/knowledge", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Knowledge_operator_request_to_knowledge_probe_succeeds()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(SystemRoles.KnowledgeOperator));

        var response = await client.GetAsync("/api/auth/probe/knowledge", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Handoff_and_review_boundaries_enforce_distinct_authenticated_roles()
    {
        using var human = _factory.CreateClient();
        human.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(SystemRoles.HumanAgent));
        using var knowledge = _factory.CreateClient();
        knowledge.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(SystemRoles.KnowledgeOperator));

        Assert.Equal(HttpStatusCode.Forbidden, (await human.PostAsJsonAsync($"/api/knowledge/candidates/{Guid.NewGuid():D}/reviews", new { }, TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await knowledge.PostAsJsonAsync("/api/handoffs/manual", new { }, TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await human.GetAsync("/api/knowledge/candidates/", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await knowledge.GetAsync("/api/handoffs/", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Review_with_null_tags_is_bad_request()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(SystemRoles.KnowledgeOperator));
        var response = await client.PostAsJsonAsync($"/api/knowledge/candidates/{Guid.NewGuid():D}/reviews",
            new { decision = "approve", tagIds = (Guid[]?)null, revisedAnswer = (string?)null, idempotencyKey = "null-tags", expectedVersion = 0 },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_handoff_request_with_invalid_subject_returns_unauthorized()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(SystemRoles.HumanAgent, "not-a-guid"));

        var response = await client.PostAsJsonAsync($"/api/handoffs/{Guid.NewGuid():D}/resolve",
            new { finalAnswer = "answer", expectedVersion = 0 }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var assign = await client.PostAsJsonAsync($"/api/handoffs/{Guid.NewGuid():D}/assign",
            new { assigneeUserId = Guid.NewGuid(), expectedVersion = 0 }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, assign.StatusCode);
    }

    private static string CreateToken(string role, string? subject = null)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = "integration-tests",
            Audience = "integration-tests-api",
            Subject = new ClaimsIdentity(new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, subject ?? Guid.NewGuid().ToString()),
                new Claim(ClaimTypes.Name, "integration-user"),
                new Claim(ClaimTypes.Role, role)
            }),
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(SigningKey)),
                SecurityAlgorithms.HmacSha256)
        };

        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityTokenHandler().CreateToken(descriptor));
    }
}

public sealed class RoleAuthorizationApiFactory : WebApplicationFactory<Program>
{
    public RoleAuthorizationApiFactory()
    {
        Environment.SetEnvironmentVariable("WECHATROBOT_MASTER_KEY_BASE64", Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        Environment.SetEnvironmentVariable("Jwt__Issuer", "integration-tests");
        Environment.SetEnvironmentVariable("Jwt__Audience", "integration-tests-api");
        Environment.SetEnvironmentVariable("Jwt__SigningKey", "integration-tests-signing-key-must-be-at-least-32-bytes");
        Environment.SetEnvironmentVariable("ConnectionStrings__WechatRobot", "Server=localhost;Port=3306;Database=wechatrobot_tests;User Id=wechatrobot;Password=wechatrobot-tests-password");
        Environment.SetEnvironmentVariable("Cors__AllowedOrigins__0", "https://admin.example.test");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.DisableStartupMigrations();
    }
}
