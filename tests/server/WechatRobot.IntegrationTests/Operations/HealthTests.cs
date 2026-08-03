using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using WechatRobot.Infrastructure.Health;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.IntegrationTests.Infrastructure;
using WechatRobot.Api.Security;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WechatRobot.Infrastructure.Persistence;

namespace WechatRobot.IntegrationTests.Operations;

public sealed class HealthTests
{
    [Fact]
    public async Task Liveness_is_anonymous_and_does_not_reveal_components_or_configuration()
    {
        await using var factory = new HealthApiFactory([Failed("Qdrant", required: true, "https://provider.invalid?apiKey=secret")]);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("Qdrant", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provider", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Readiness_requires_admin_authentication()
    {
        await using var factory = new HealthApiFactory([Healthy("MySQL", required: true)]);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/admin/health/ready", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Root_route_requires_authentication()
    {
        await using var factory = new HealthApiFactory([Healthy("MySQL", required: true)]);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync("/", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Ocr_health_reports_only_sanitized_configuration_state()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ocr:Provider"] = "Aliyun",
            ["Ocr:Action"] = "",
            ["Ocr:Endpoint"] = "ocr-api.cn-hangzhou.aliyuncs.com"
        }).Build();
        var probe = new OcrHealthProbe(configuration);
        var result = await probe.CheckAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ComponentHealthState.Failed, result.State);
        Assert.Equal("unavailable", result.Detail);
    }

    [Fact]
    public async Task Qdrant_probe_uses_readyz_and_propagates_request_cancellation()
    {
        var handler = new ProbeHandler(HttpStatusCode.OK);
        var probe = new QdrantHealthProbe(new SingleClientFactory(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:6333/")
        }));
        var result = await probe.CheckAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ComponentHealthState.Healthy, result.State);
        Assert.Equal("/readyz", handler.LastPath);

        var hanging = new QdrantHealthProbe(new SingleClientFactory(new HttpClient(new HangingHandler())
        {
            BaseAddress = new Uri("http://127.0.0.1:6333/")
        }));
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hanging.CheckAsync(cancelled.Token));
    }

    [Fact]
    public async Task Oss_probe_requires_https_public_url_and_explicit_public_read_acceptance()
    {
        var values = new Dictionary<string, string?>
        {
            ["Oss:AccessKeyId"] = "id",
            ["Oss:AccessKeySecret"] = "secret",
            ["Oss:Bucket"] = "bucket",
            ["Oss:Endpoint"] = "oss-cn-shenzhen.aliyuncs.com",
            ["Oss:PublicBaseUrl"] = "https://bucket.example.test/",
            ["Oss:PublicReadRiskAccepted"] = "true"
        };
        var healthy = new OssConfigurationHealthProbe(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        Assert.Equal(ComponentHealthState.Healthy,
            (await healthy.CheckAsync(TestContext.Current.CancellationToken)).State);

        values["Oss:PublicBaseUrl"] = "";
        var generatedPublicUrlProbe = new OssConfigurationHealthProbe(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        Assert.Equal(ComponentHealthState.Healthy,
            (await generatedPublicUrlProbe.CheckAsync(TestContext.Current.CancellationToken)).State);

        values["Oss:PublicBaseUrl"] = "http://bucket.example.test/";
        var unsafeProbe = new OssConfigurationHealthProbe(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        Assert.Equal(ComponentHealthState.Failed,
            (await unsafeProbe.CheckAsync(TestContext.Current.CancellationToken)).State);
    }

    [Fact]
    public async Task Loopback_object_storage_probe_uses_runtime_section_and_strict_policy()
    {
        var healthyValues = new Dictionary<string, string?>
        {
            ["ObjectStorage:Provider"] = "loopback",
            ["LoopbackObjectStorage:BaseUrl"] = "http://127.0.0.1:49001/storage/"
        };
        var healthy = new OssConfigurationHealthProbe(new ConfigurationBuilder().AddInMemoryCollection(healthyValues).Build());
        Assert.Equal(ComponentHealthState.Healthy,
            (await healthy.CheckAsync(TestContext.Current.CancellationToken)).State);

        var legacyWrongSection = new Dictionary<string, string?>
        {
            ["ObjectStorage:Provider"] = "loopback",
            ["ObjectStorage:Loopback:BaseUrl"] = "http://127.0.0.1:49001/storage/"
        };
        var missingRuntimeValue = new OssConfigurationHealthProbe(new ConfigurationBuilder().AddInMemoryCollection(legacyWrongSection).Build());
        Assert.Equal(ComponentHealthState.Failed,
            (await missingRuntimeValue.CheckAsync(TestContext.Current.CancellationToken)).State);

        healthyValues["LoopbackObjectStorage:BaseUrl"] = "http://127.0.0.1.evil.example/storage/";
        var unsafeHost = new OssConfigurationHealthProbe(new ConfigurationBuilder().AddInMemoryCollection(healthyValues).Build());
        Assert.Equal(ComponentHealthState.Failed,
            (await unsafeHost.CheckAsync(TestContext.Current.CancellationToken)).State);
    }

    [Fact]
    public async Task Readiness_is_healthy_when_all_components_are_healthy()
    {
        await AssertReadyAsync(
            [Healthy("MySQL", true), Healthy("Qdrant", true), Healthy("OCR", false), Healthy("OSS configuration", true), Healthy("Worker heartbeat", true)],
            HttpStatusCode.OK, "healthy");
    }

    [Fact]
    public async Task Readiness_is_degraded_when_optional_component_fails()
    {
        await AssertReadyAsync(
            [Healthy("MySQL", true), Failed("OCR", false, "unavailable")],
            HttpStatusCode.OK, "degraded");
    }

    [Fact]
    public async Task Readiness_fails_when_required_component_fails()
    {
        await AssertReadyAsync(
            [Healthy("OCR", false), Failed("MySQL", true, "unavailable")],
            HttpStatusCode.ServiceUnavailable, "failed");
    }

    [Fact]
    public async Task Readiness_fails_when_worker_heartbeat_is_stale()
    {
        await AssertReadyAsync(
            [Healthy("MySQL", true), Failed("Worker heartbeat", true, "stale")],
            HttpStatusCode.ServiceUnavailable, "failed");
    }

    [Fact]
    public async Task Readiness_applies_one_short_linked_deadline_to_all_probes()
    {
        await using var factory = new HealthApiFactory([new HangingProbe("MySQL", required: true)]);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateAdminToken());
        var stopwatch = Stopwatch.StartNew();

        var response = await client.GetAsync("/api/admin/health/ready", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Readiness took {stopwatch.Elapsed}.");
    }

    [Theory]
    [InlineData("POST", "/api/auth/login", RateLimitPolicies.Login)]
    [InlineData("POST", "/api/worktool/callback/{robotCode}", RateLimitPolicies.Callback)]
    [InlineData("POST", "/api/knowledge/documents", RateLimitPolicies.Upload)]
    [InlineData("POST", "/api/admin/worktool/group-operations/execute", RateLimitPolicies.WorkToolCommands)]
    [InlineData("GET", "/api/admin/health/ready", RateLimitPolicies.Ordinary)]
    [InlineData("GET", "/api/admin/dashboard/summary", RateLimitPolicies.Ordinary)]
    public void Sensitive_endpoint_families_have_distinct_rate_limit_policies(
        string method,
        string route,
        string policy)
    {
        using var factory = new HealthApiFactory([Healthy("MySQL", true)]);
        var endpoint = factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(value =>
                value.RoutePattern.RawText?.TrimEnd('/') == route.TrimEnd('/') &&
                value.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(method) == true);

        Assert.Equal(policy, endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName);
    }

    [Theory]
    [InlineData("POST", "/api/knowledge/documents", RateLimitPolicies.Upload)]
    [InlineData("GET", "/api/knowledge/documents", RateLimitPolicies.Ordinary)]
    [InlineData("GET", "/api/knowledge/documents/id/versions", RateLimitPolicies.Ordinary)]
    public void Global_rate_limit_classification_reserves_upload_limits_for_uploads(
        string method,
        string path,
        string expectedPolicy)
    {
        var classify = typeof(RateLimitPolicies).GetMethod(
            "Classify",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var result = ((string Policy, int Permits))classify.Invoke(
            null,
            [new Microsoft.AspNetCore.Http.PathString(path), method])!;

        Assert.Equal(expectedPolicy, result.Policy);
    }

    private static async Task AssertReadyAsync(
        IReadOnlyCollection<IComponentHealthProbe> probes,
        HttpStatusCode expectedCode,
        string expectedStatus)
    {
        await using var factory = new HealthApiFactory(probes);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateAdminToken());

        var response = await client.GetAsync("/api/admin/health/ready", TestContext.Current.CancellationToken);
        var payload = await response.Content.ReadFromJsonAsync<HealthPayload>(TestContext.Current.CancellationToken);

        Assert.Equal(expectedCode, response.StatusCode);
        Assert.Equal(expectedStatus, payload?.Status);
        Assert.Equal(probes.Count, payload?.Components.Count);
    }

    private static IComponentHealthProbe Healthy(string name, bool required) =>
        new FakeProbe(name, required, ComponentHealthState.Healthy, null);

    private static IComponentHealthProbe Failed(string name, bool required, string detail) =>
        new FakeProbe(name, required, ComponentHealthState.Failed, detail);

    private static string CreateAdminToken()
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = HealthApiFactory.Issuer,
            Audience = HealthApiFactory.Audience,
            Subject = new ClaimsIdentity([
                new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()),
                new Claim(ClaimTypes.Name, "health-admin"),
                new Claim(ClaimTypes.Role, SystemRoles.Admin)
            ]),
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(HealthApiFactory.SigningKey)),
                SecurityAlgorithms.HmacSha256)
        };
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityTokenHandler().CreateToken(descriptor));
    }

    private sealed record HealthPayload(string Status, IReadOnlyCollection<ComponentPayload> Components);
    private sealed record ComponentPayload(string Name, string Status, bool Required, string? Detail);

    private sealed class FakeProbe(string name, bool required, ComponentHealthState state, string? detail) : IComponentHealthProbe
    {
        public string Name => name;
        public bool Required => required;
        public Task<ComponentHealthResult> CheckAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ComponentHealthResult(name, state, required, detail));
    }

    private sealed class HangingProbe(string name, bool required) : IComponentHealthProbe
    {
        public string Name => name;
        public bool Required => required;
        public async Task<ComponentHealthResult> CheckAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new(Name, ComponentHealthState.Healthy, Required);
        }
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class ProbeHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public string? LastPath { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri?.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}

public sealed class HealthApiFactory : WebApplicationFactory<Program>
{
    internal const string Issuer = "health-tests";
    internal const string Audience = "health-tests-api";
    internal const string SigningKey = "health-tests-signing-key-must-be-at-least-32-bytes";
    private readonly IReadOnlyCollection<IComponentHealthProbe> _probes;

    public HealthApiFactory(IReadOnlyCollection<IComponentHealthProbe> probes)
    {
        _probes = probes;
        Environment.SetEnvironmentVariable("WECHATROBOT_MASTER_KEY_BASE64",
            Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        Environment.SetEnvironmentVariable("ConnectionStrings__WechatRobot", "Server=localhost;Database=unused");
        Environment.SetEnvironmentVariable("Cors__AllowedOrigins__0", "https://admin.example.test");
        Environment.SetEnvironmentVariable("Jwt__Issuer", Issuer);
        Environment.SetEnvironmentVariable("Jwt__Audience", Audience);
        Environment.SetEnvironmentVariable("Jwt__SigningKey", SigningKey);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.DisableStartupMigrations();
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:WechatRobot"] = "Server=localhost;Database=unused",
            ["Cors:AllowedOrigins:0"] = "https://admin.example.test",
            ["Jwt:Issuer"] = Issuer,
            ["Jwt:Audience"] = Audience,
            ["Jwt:SigningKey"] = SigningKey,
            ["Health:ProbeTimeoutMilliseconds"] = "100"
        }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IComponentHealthProbe>();
            foreach (var probe in _probes) services.AddSingleton(probe);
        });
    }
}
