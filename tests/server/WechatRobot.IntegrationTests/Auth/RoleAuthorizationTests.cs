using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using WechatRobot.Infrastructure.Identity;

namespace WechatRobot.IntegrationTests.Auth;

public sealed class RoleAuthorizationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string SigningKey = "integration-tests-signing-key-must-be-at-least-32-bytes";
    private readonly WebApplicationFactory<Program> _factory;

    public RoleAuthorizationTests(WebApplicationFactory<Program> factory)
    {
        Environment.SetEnvironmentVariable("WECHATROBOT_MASTER_KEY_BASE64", Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        Environment.SetEnvironmentVariable("Jwt__Issuer", "integration-tests");
        Environment.SetEnvironmentVariable("Jwt__Audience", "integration-tests-api");
        Environment.SetEnvironmentVariable("Jwt__SigningKey", SigningKey);
        Environment.SetEnvironmentVariable("ConnectionStrings__WechatRobot", "Server=localhost;Port=3306;Database=wechatrobot_tests;User Id=wechatrobot;Password=wechatrobot-tests-password");
        Environment.SetEnvironmentVariable("Cors__AllowedOrigins__0", "https://admin.example.test");
        _factory = factory;
    }

    [Fact]
    public void Auth_probe_route_is_mapped()
    {
        var routes = _factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToArray();

        Assert.Contains("/api/auth/probe/knowledge", routes);
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

    private static string CreateToken(string role)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = "integration-tests",
            Audience = "integration-tests-api",
            Subject = new ClaimsIdentity(new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()),
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
