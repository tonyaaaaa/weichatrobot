using System.Security.Cryptography;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WechatRobot.Application.Models;
using WechatRobot.Application.WorkTool;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.IntegrationTests.Infrastructure;

namespace WechatRobot.IntegrationTests.Models;

public sealed class ModelConfigurationEndpointTests : IClassFixture<ModelConfigurationApiFactory>
{
    private readonly ModelConfigurationApiFactory _factory;

    public ModelConfigurationEndpointTests(ModelConfigurationApiFactory factory) => _factory = factory;

    [Fact]
    public void Model_configuration_routes_are_mapped_and_require_admin_policy()
    {
        var routes = _factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/admin/model-configurations", StringComparison.Ordinal) == true)
            .ToArray();

        Assert.Contains(routes, endpoint => endpoint.RoutePattern.RawText == "/api/admin/model-configurations/");
        Assert.Contains(routes, endpoint => endpoint.RoutePattern.RawText == "/api/admin/model-configurations/{id:guid}");
        Assert.Contains(routes, endpoint => endpoint.RoutePattern.RawText == "/api/admin/model-configurations/{name}");
        Assert.All(routes, endpoint => Assert.Contains(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(), data => data.Policy == SystemRoles.Admin));
    }

    [Fact]
    public async Task Blank_key_update_preserves_ciphertext_and_returns_only_safe_key_metadata()
    {
        const string plaintextKey = "provider-secret-9876";
        string storedCiphertext;
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            var service = scope.ServiceProvider.GetRequiredService<ModelConfigurationService>();
            storedCiphertext = service.ProtectSubmittedApiKey(plaintextKey, null)!;
            database.ModelConfigs.Add(new()
            {
                Name = "chat-primary",
                Provider = "openai-compatible",
                ConfigurationType = "chat",
                BaseUrl = "https://provider.example.test",
                Model = "old-model",
                EncryptedApiKey = storedCiphertext
            });
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var client = _factory.CreateClient();
        var response = await client.PutAsJsonAsync("/api/admin/model-configurations/chat-primary", new
        {
            provider = "openai-compatible",
            configurationType = "chat",
            baseUrl = "https://provider.example.test",
            model = "new-model",
            apiKey = "",
            timeoutSeconds = 30,
            maxRetries = 1,
            isEnabled = true,
            isDefault = true
        }, TestContext.Current.CancellationToken);
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.DoesNotContain(plaintextKey, json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.GetProperty("hasApiKey").GetBoolean());
        Assert.Equal("9876", document.RootElement.GetProperty("lastFour").GetString());

        using var verifyScope = _factory.Services.CreateScope();
        var databaseAfterUpdate = verifyScope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        Assert.Equal(storedCiphertext, (await databaseAfterUpdate.ModelConfigs.SingleAsync(config => config.Name == "chat-primary", TestContext.Current.CancellationToken)).EncryptedApiKey);
    }

    [Fact]
    public async Task Short_api_key_update_returns_masked_metadata_without_disclosing_the_key()
    {
        const string plaintextKey = "abc";
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            var service = scope.ServiceProvider.GetRequiredService<ModelConfigurationService>();
            database.ModelConfigs.Add(new()
            {
                Name = "chat-short-key",
                Provider = "openai-compatible",
                ConfigurationType = "chat",
                BaseUrl = "https://provider.example.test",
                Model = "model",
                EncryptedApiKey = service.ProtectSubmittedApiKey(plaintextKey, null)
            });
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var client = _factory.CreateClient();
        var response = await client.PutAsJsonAsync("/api/admin/model-configurations/chat-short-key", new
        {
            provider = "openai-compatible",
            configurationType = "chat",
            baseUrl = "https://provider.example.test",
            model = "model",
            apiKey = "",
            timeoutSeconds = 30,
            maxRetries = 0,
            isEnabled = true,
            isDefault = false
        }, TestContext.Current.CancellationToken);
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.DoesNotContain(plaintextKey, json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.GetProperty("hasApiKey").GetBoolean());
        Assert.Equal("****", document.RootElement.GetProperty("lastFour").GetString());
    }

    [Fact]
    public async Task Create_trims_name_and_forces_disabled_untested_state()
    {
        var suffix = Guid.NewGuid().ToString("N");
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/admin/model-configurations", new
        {
            name = $"  Local Chat {suffix}  ",
            provider = "OpenAI compatible",
            configurationType = "CHAT",
            baseUrl = "http://127.0.0.1:11434/",
            model = "qwen",
            apiKey = (string?)null,
            timeoutSeconds = 30,
            maxRetries = 0,
            isEnabled = true,
            isDefault = true
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var root = document.RootElement;
        var id = root.GetProperty("id").GetGuid();
        Assert.Equal($"Local Chat {suffix}", root.GetProperty("name").GetString());
        Assert.Equal("chat", root.GetProperty("configurationType").GetString());
        Assert.False(root.GetProperty("isEnabled").GetBoolean());
        Assert.False(root.GetProperty("isDefault").GetBoolean());
        Assert.Equal(ModelConnectionStatus.Untested, root.GetProperty("connectionStatus").GetString());
        Assert.Equal(0, root.GetProperty("version").GetInt32());

        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var stored = await database.ModelConfigs.SingleAsync(
            item => item.Id == id,
            TestContext.Current.CancellationToken);
        Assert.Equal($"LOCAL CHAT {suffix.ToUpperInvariant()}", stored.NormalizedName);
    }

    [Fact]
    public async Task Update_by_id_can_rename_without_changing_identity()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var id = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            database.ModelConfigs.Add(new()
            {
                Id = id,
                Name = $"before-{suffix}",
                NormalizedName = $"BEFORE-{suffix.ToUpperInvariant()}",
                Provider = "OpenAI compatible",
                ConfigurationType = "chat",
                BaseUrl = "https://provider.example.test",
                Model = "old-model",
                Version = 3
            });
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var client = _factory.CreateClient();
        var response = await client.PutAsJsonAsync($"/api/admin/model-configurations/{id}", new
        {
            name = $"Renamed {suffix}",
            provider = "OpenAI compatible",
            configurationType = "chat",
            baseUrl = "https://provider.example.test",
            model = "new-model",
            apiKey = (string?)null,
            timeoutSeconds = 45,
            maxRetries = 1,
            version = 3
        }, TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(id, document.RootElement.GetProperty("id").GetGuid());
        Assert.Equal($"Renamed {suffix}", document.RootElement.GetProperty("name").GetString());
        Assert.Equal(4, document.RootElement.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Create_rejects_normalized_name_conflict()
    {
        var suffix = Guid.NewGuid().ToString("N");
        using var client = _factory.CreateClient();
        var first = await client.PostAsJsonAsync("/api/admin/model-configurations", CreateRequest($"Primary {suffix}"),
            TestContext.Current.CancellationToken);
        first.EnsureSuccessStatusCode();

        var conflict = await client.PostAsJsonAsync("/api/admin/model-configurations", CreateRequest($" primary {suffix} "),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using var document = JsonDocument.Parse(
            await conflict.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("model_name_conflict", document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Create_rejects_blank_name()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/admin/model-configurations",
            CreateRequest(" "),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_rejects_name_longer_than_128_characters()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/admin/model-configurations",
            CreateRequest(new string('x', 129)),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Update_rejects_stale_version()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var id = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            database.ModelConfigs.Add(new()
            {
                Id = id,
                Name = $"stale-{suffix}",
                NormalizedName = $"STALE-{suffix.ToUpperInvariant()}",
                Provider = "OpenAI compatible",
                ConfigurationType = "chat",
                BaseUrl = "https://provider.example.test",
                Model = "model",
                Version = 2
            });
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var client = _factory.CreateClient();
        var response = await client.PutAsJsonAsync($"/api/admin/model-configurations/{id}", new
        {
            name = $"stale-{suffix}",
            provider = "OpenAI compatible",
            configurationType = "chat",
            baseUrl = "https://provider.example.test",
            model = "model",
            apiKey = (string?)null,
            timeoutSeconds = 30,
            maxRetries = 0,
            version = 1
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("model_concurrency_conflict", document.RootElement.GetProperty("code").GetString());
    }

    private static object CreateRequest(string name) => new
    {
        name,
        provider = "OpenAI compatible",
        configurationType = "chat",
        baseUrl = "https://provider.example.test",
        model = "model",
        apiKey = (string?)null,
        timeoutSeconds = 30,
        maxRetries = 0
    };

}

public sealed class ModelConfigurationApiFactory : WebApplicationFactory<Program>
{
    private static readonly ServiceProvider InMemoryProvider = new ServiceCollection()
        .AddEntityFrameworkInMemoryDatabase()
        .BuildServiceProvider();
    private readonly string _databaseName = Guid.NewGuid().ToString();

    public ModelConfigurationApiFactory()
    {
        Environment.SetEnvironmentVariable("WECHATROBOT_MASTER_KEY_BASE64", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        Environment.SetEnvironmentVariable("Jwt__Issuer", "model-config-tests");
        Environment.SetEnvironmentVariable("Jwt__Audience", "model-config-tests-api");
        Environment.SetEnvironmentVariable("Jwt__SigningKey", "model-config-tests-signing-key-must-be-at-least-32-bytes");
        Environment.SetEnvironmentVariable("ConnectionStrings__WechatRobot", "Server=localhost;Port=3306;Database=wechatrobot_tests;User Id=wechatrobot;Password=wechatrobot-tests-password");
        Environment.SetEnvironmentVariable("Cors__AllowedOrigins__0", "https://admin.example.test");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.DisableStartupMigrations();
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Issuer"] = "model-config-tests",
            ["Jwt:Audience"] = "model-config-tests-api",
            ["Jwt:SigningKey"] = "model-config-tests-signing-key-must-be-at-least-32-bytes",
            ["ConnectionStrings:WechatRobot"] = "Server=localhost;Port=3306;Database=wechatrobot_tests;User Id=wechatrobot;Password=wechatrobot-tests-password",
            ["Cors:AllowedOrigins:0"] = "https://admin.example.test"
        }));
        builder.ConfigureServices(services =>
        {
            var descriptor = services.Single(service => service.ServiceType == typeof(DbContextOptions<WechatRobotDbContext>));
            services.Remove(descriptor);
            services.AddDbContext<WechatRobotDbContext>(options => options
                .UseInMemoryDatabase(_databaseName)
                .UseInternalServiceProvider(InMemoryProvider));
            foreach (var workTool in services.Where(service => service.ServiceType == typeof(IWorkToolClient)).ToArray()) services.Remove(workTool);
            services.AddSingleton<RecordingWorkToolClient>();
            services.AddSingleton<IWorkToolClient>(provider => provider.GetRequiredService<RecordingWorkToolClient>());
            services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "integration-admin";
                    options.DefaultChallengeScheme = "integration-admin";
                    options.DefaultForbidScheme = "integration-admin";
                })
                .AddScheme<AuthenticationSchemeOptions, IntegrationAdminAuthenticationHandler>("integration-admin", _ => { });
        });
    }
}

public sealed class RecordingWorkToolClient : IWorkToolClient
{
    public int GroupOperationCalls { get; private set; }
    public WorkToolSendResult NextGroupOperationResult { get; set; } = WorkToolSendResult.Success();
    public Task<WorkToolSendResult> SendTextAsync(WorkToolSendRequest request, CancellationToken cancellationToken) => Task.FromResult(WorkToolSendResult.Success());
    public Task<WorkToolSendResult> TestConnectionAsync(Guid robotConfigId, CancellationToken cancellationToken) => Task.FromResult(WorkToolSendResult.Success());
    public Task<WorkToolSendResult> BindCallbackAsync(Guid robotConfigId, int type, Uri callbackUrl, CancellationToken cancellationToken) => Task.FromResult(WorkToolSendResult.Success());
    public Task<WorkToolSendResult> ExecuteGroupOperationAsync(WorkToolGroupOperationRequest request, CancellationToken cancellationToken) { GroupOperationCalls++; return Task.FromResult(NextGroupOperationResult); }
    public void Reset(WorkToolSendResult? next = null) { GroupOperationCalls = 0; NextGroupOperationResult = next ?? WorkToolSendResult.Success(); }
}

public sealed class IntegrationAdminAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public IntegrationAdminAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, System.Text.Encodings.Web.UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var nameClaims = Request.Headers.ContainsKey("X-Test-No-Name")
            ? Array.Empty<Claim>()
            : [new Claim(ClaimTypes.Name, "model-admin")];
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            nameClaims.Append(new Claim(ClaimTypes.Role, SystemRoles.Admin)),
            Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}
