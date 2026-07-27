using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WechatRobot.Api.Security;
using WechatRobot.Application.WorkTool;
using WechatRobot.Infrastructure.Health;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.IntegrationTests.Infrastructure;

namespace WechatRobot.IntegrationTests.Operations;

public sealed class DashboardSummaryEndpointTests
{
    [Fact]
    public async Task Summary_requires_admin_authentication()
    {
        await using var factory = new DashboardApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Anonymous", "true");

        var response = await client.GetAsync(
            "/api/admin/dashboard/summary",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Summary_returns_safe_database_counts_and_status_groups()
    {
        var enabledRobotId = Guid.NewGuid();
        await using var factory = new DashboardApiFactory();
        await SeedAsync(factory, enabledRobotId, includeFailingRobot: false);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            "/api/admin/dashboard/summary",
            TestContext.Current.CancellationToken);
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var payload = JsonDocument.Parse(json);
        var root = payload.RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, root.GetProperty("robots").GetProperty("total").GetInt32());
        Assert.Equal(1, root.GetProperty("robots").GetProperty("enabled").GetInt32());
        Assert.Equal(1, root.GetProperty("knowledge").GetProperty("documents").GetInt32());
        Assert.Equal(2, root.GetProperty("knowledge").GetProperty("versions").GetInt32());
        Assert.Equal(1, root.GetProperty("knowledge").GetProperty("pendingCandidates").GetInt32());
        Assert.Equal(2, root.GetProperty("knowledge").GetProperty("failedTasks").GetInt32());
        Assert.Equal(1, root.GetProperty("operations").GetProperty("deadLetters").GetInt32());
        Assert.Equal(2, root.GetProperty("operations").GetProperty("durableJobs").GetProperty("pending").GetInt32());
        Assert.Equal(1, root.GetProperty("operations").GetProperty("sendCommands").GetProperty("retrying").GetInt32());
        Assert.DoesNotContain("handoff", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("payloadJson", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("robot-secret", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Summary_preserves_database_counts_when_robot_or_required_component_probe_fails()
    {
        var healthyRobotId = Guid.NewGuid();
        var failingRobotId = Guid.NewGuid();
        await using var factory = new DashboardApiFactory(
            failingRobotId,
            [
                new FixedProbe("MySQL", required: true, ComponentHealthState.Healthy),
                new FixedProbe("Qdrant", required: true, ComponentHealthState.Failed, "unavailable"),
                new FixedProbe("OCR", required: false, ComponentHealthState.Healthy)
            ]);
        await SeedAsync(factory, healthyRobotId, includeFailingRobot: true, failingRobotId);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            "/api/admin/dashboard/summary",
            TestContext.Current.CancellationToken);
        using var payload = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var root = payload.RootElement;
        var robots = root.GetProperty("robots");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, robots.GetProperty("enabled").GetInt32());
        Assert.Equal(1, robots.GetProperty("reachable").GetInt32());
        Assert.Equal(1, robots.GetProperty("online").GetInt32());
        Assert.Equal(1, robots.GetProperty("messageCallbackConfigured").GetInt32());
        Assert.Equal(1, robots.GetProperty("commandResultCallbackConfigured").GetInt32());
        Assert.Equal(1, robots.GetProperty("failedChecks").GetInt32());
        Assert.Equal("failed", root.GetProperty("readiness").GetProperty("status").GetString());
        Assert.Equal(1, root.GetProperty("operations").GetProperty("deadLetters").GetInt32());
        Assert.Contains(
            root.GetProperty("readiness").GetProperty("components").EnumerateArray(),
            component =>
                component.GetProperty("name").GetString() == "Qdrant" &&
                component.GetProperty("status").GetString() == "failed" &&
                component.GetProperty("detail").GetString() == "unavailable");
    }

    private static async Task SeedAsync(
        DashboardApiFactory factory,
        Guid enabledRobotId,
        bool includeFailingRobot,
        Guid? failingRobotId = null)
    {
        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        await database.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        database.RobotConfigs.AddRange(
            Robot(enabledRobotId, enabled: true),
            Robot(Guid.NewGuid(), enabled: includeFailingRobot));
        if (includeFailingRobot && failingRobotId.HasValue)
        {
            database.RobotConfigs.Remove(database.RobotConfigs.Local.Last());
            database.RobotConfigs.Add(Robot(failingRobotId.Value, enabled: true));
        }

        var documentId = Guid.NewGuid();
        database.KnowledgeDocuments.Add(new KnowledgeDocumentEntity
        {
            Id = documentId,
            Title = "运营手册",
            Status = "active"
        });
        database.KnowledgeDocumentVersions.AddRange(
            new KnowledgeDocumentVersionEntity
            {
                KnowledgeDocumentId = documentId,
                Version = 1,
                OriginalFileName = "v1.pdf",
                SafeFileName = "v1.pdf",
                Sha256 = new string('1', 64),
                Status = "failed"
            },
            new KnowledgeDocumentVersionEntity
            {
                KnowledgeDocumentId = documentId,
                Version = 2,
                OriginalFileName = "v2.pdf",
                SafeFileName = "v2.pdf",
                Sha256 = new string('2', 64),
                Status = "indexed"
            });
        database.KnowledgeCandidates.Add(new KnowledgeCandidateEntity
        {
            HandoffCaseId = Guid.NewGuid(),
            QuestionMessageId = Guid.NewGuid(),
            Question = "问题",
            Answer = "答案",
            Status = "pending",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });
        database.KnowledgeIndexJobs.Add(new KnowledgeIndexJobEntity
        {
            KnowledgeDocumentId = documentId,
            KnowledgeDocumentVersionId = Guid.NewGuid(),
            CollectionName = "failed-index",
            Dimension = 2,
            Status = "failed"
        });
        database.DurableJobs.AddRange(
            new DurableJobEntity { JobType = "one", PayloadJson = "{}", Status = "pending" },
            new DurableJobEntity { JobType = "two", PayloadJson = "{}", Status = "pending" });
        database.SendCommands.Add(new SendCommandEntity
        {
            RobotConfigId = enabledRobotId,
            IdempotencyKey = $"send-{Guid.NewGuid():N}",
            PayloadJson = "{\"secret\":\"payload\"}",
            Status = "retrying"
        });
        database.DeadLetters.Add(new DeadLetterEntity
        {
            Reason = "provider failed",
            PayloadJson = "{\"secret\":\"dead-letter\"}"
        });
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static RobotConfigEntity Robot(Guid id, bool enabled) => new()
    {
        Id = id,
        Name = $"robot-{id:N}",
        WorkToolRobotId = $"robot-secret-{id:N}",
        CallbackSecretHash = $"callback-secret-{id:N}",
        IsEnabled = enabled
    };

    private sealed class FixedProbe(
        string name,
        bool required,
        ComponentHealthState state,
        string? detail = null) : IComponentHealthProbe
    {
        public string Name => name;
        public bool Required => required;
        public Task<ComponentHealthResult> CheckAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ComponentHealthResult(name, state, required, detail));
    }
}

public sealed class DashboardApiFactory(
    Guid? failingRobotId = null,
    IReadOnlyCollection<IComponentHealthProbe>? probes = null) : WebApplicationFactory<Program>
{
    private static readonly ServiceProvider InMemoryProvider = new ServiceCollection()
        .AddEntityFrameworkInMemoryDatabase()
        .BuildServiceProvider();
    private readonly string _databaseName = Guid.NewGuid().ToString("N");
    private readonly IReadOnlyCollection<IComponentHealthProbe> _probes = probes ??
        [new DashboardHealthyProbe("MySQL", true), new DashboardHealthyProbe("Qdrant", true)];

    public DashboardApiFactory() : this(null, null)
    {
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Environment.SetEnvironmentVariable(
            "WECHATROBOT_MASTER_KEY_BASE64",
            Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        Environment.SetEnvironmentVariable("ConnectionStrings__WechatRobot", "Server=localhost;Database=unused");
        Environment.SetEnvironmentVariable("Cors__AllowedOrigins__0", "https://admin.example.test");
        Environment.SetEnvironmentVariable("Jwt__Issuer", "dashboard-tests");
        Environment.SetEnvironmentVariable("Jwt__Audience", "dashboard-tests-api");
        Environment.SetEnvironmentVariable("Jwt__SigningKey", "dashboard-tests-signing-key-must-be-at-least-32-bytes");

        builder.UseEnvironment("Testing");
        builder.DisableStartupMigrations();
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:WechatRobot"] = "Server=localhost;Database=unused",
                ["Cors:AllowedOrigins:0"] = "https://admin.example.test",
                ["Jwt:Issuer"] = "dashboard-tests",
                ["Jwt:Audience"] = "dashboard-tests-api",
                ["Jwt:SigningKey"] = "dashboard-tests-signing-key-must-be-at-least-32-bytes",
                ["Health:ProbeTimeoutMilliseconds"] = "100",
                ["Dashboard:RobotProbeTimeoutMilliseconds"] = "100"
            }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<WechatRobotDbContext>();
            services.RemoveAll<DbContextOptions<WechatRobotDbContext>>();
            services.RemoveAll<IDbContextFactory<WechatRobotDbContext>>();
            services.AddDbContextFactory<WechatRobotDbContext>(options => options
                .UseInMemoryDatabase(_databaseName)
                .UseInternalServiceProvider(InMemoryProvider));

            services.RemoveAll<IComponentHealthProbe>();
            foreach (var probe in _probes)
                services.AddSingleton(probe);

            services.RemoveAll<IWorkToolClient>();
            services.AddSingleton<IWorkToolClient>(new DashboardWorkToolClient(failingRobotId));
            services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "dashboard-admin";
                    options.DefaultChallengeScheme = "dashboard-admin";
                    options.DefaultForbidScheme = "dashboard-admin";
                })
                .AddScheme<AuthenticationSchemeOptions, DashboardAuthenticationHandler>(
                    "dashboard-admin",
                    _ => { });
        });
    }

    private sealed class DashboardHealthyProbe(string name, bool required) : IComponentHealthProbe
    {
        public string Name => name;
        public bool Required => required;
        public Task<ComponentHealthResult> CheckAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ComponentHealthResult(name, ComponentHealthState.Healthy, required));
    }
}

internal sealed class DashboardWorkToolClient(Guid? failingRobotId) : IWorkToolClient
{
    public Task<WorkToolRobotSnapshot> GetRobotAsync(Guid robotConfigId, CancellationToken cancellationToken) =>
        robotConfigId == failingRobotId
            ? Task.FromException<WorkToolRobotSnapshot>(new HttpRequestException("secret provider failure"))
            : Task.FromResult(new WorkToolRobotSnapshot(true, "must-not-leak", true, true, null));

    public Task<WorkToolOnlineSnapshot> GetOnlineAsync(Guid robotConfigId, CancellationToken cancellationToken) =>
        Task.FromResult(new WorkToolOnlineSnapshot(true, null));

    public Task<IReadOnlyList<WorkToolEventCallbackRegistration>> ListEventCallbacksAsync(
        Guid robotConfigId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WorkToolEventCallbackRegistration>>(
            [new(1, "https://example.test/api/worktool/command-results/redacted")]);

    public Task<WorkToolCommandSubmission> SendTextAsync(
        WorkToolSendRequest request,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<WorkToolCommandSubmission> ExecuteGroupOperationAsync(
        WorkToolGroupOperationRequest request,
        CancellationToken cancellationToken) => throw new NotSupportedException();

#pragma warning disable CS0618
    public Task<WorkToolSendResult> TestConnectionAsync(
        Guid robotConfigId,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<WorkToolSendResult> BindCallbackAsync(
        Guid robotConfigId,
        int type,
        Uri callbackUrl,
        CancellationToken cancellationToken) => throw new NotSupportedException();
#pragma warning restore CS0618
}

internal sealed class DashboardAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers.ContainsKey("X-Test-Anonymous"))
            return Task.FromResult(AuthenticateResult.NoResult());

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, "dashboard-admin"),
                new Claim(ClaimTypes.Role, SystemRoles.Admin)
            ],
            Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(principal, Scheme.Name)));
    }
}
