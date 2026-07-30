using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.IntegrationTests.Infrastructure;

namespace WechatRobot.IntegrationTests.Conversations;

public sealed class ConversationAuditEndpointTests : IClassFixture<ConversationAuditApiFactory>
{
    private readonly ConversationAuditApiFactory _factory;
    public ConversationAuditEndpointTests(ConversationAuditApiFactory factory) => _factory = factory;

    [Theory]
    [InlineData(SystemRoles.Admin, HttpStatusCode.OK)]
    [InlineData(SystemRoles.KnowledgeOperator, HttpStatusCode.OK)]
    [InlineData(SystemRoles.HumanAgent, HttpStatusCode.Forbidden)]
    public async Task Audit_read_enforces_roles(string role, HttpStatusCode expected)
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", role);

        var response = await client.GetAsync("/api/audit/conversations", TestContext.Current.CancellationToken);

        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData(SystemRoles.Admin, HttpStatusCode.OK)]
    [InlineData(SystemRoles.KnowledgeOperator, HttpStatusCode.OK)]
    [InlineData(SystemRoles.HumanAgent, HttpStatusCode.Forbidden)]
    public async Task Audit_group_options_enforce_roles(string role, HttpStatusCode expected)
    {
        await _factory.SeedAsync();
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", role);

        var response = await client.GetAsync(
            "/api/audit/group-options",
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData(SystemRoles.Admin, HttpStatusCode.OK)]
    [InlineData(SystemRoles.KnowledgeOperator, HttpStatusCode.OK)]
    [InlineData(SystemRoles.HumanAgent, HttpStatusCode.Forbidden)]
    public async Task Shared_group_options_enforce_roles(string role, HttpStatusCode expected)
    {
        await _factory.SeedAsync();
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", role);

        var response = await client.GetAsync(
            "/api/group-options",
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Audit_group_options_include_disabled_groups_without_secrets()
    {
        await _factory.SeedAsync();
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", SystemRoles.KnowledgeOperator);

        var response = await client.GetAsync(
            "/api/audit/group-options",
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var options = document.RootElement.EnumerateArray().ToArray();
        Assert.Equal(2, options.Length);
        Assert.Equal(["技术部", "停用群"], options.Select(item => item.GetProperty("name").GetString()));
        Assert.Contains(options, item => item.GetProperty("isEnabled").GetBoolean());
        Assert.Contains(options, item => !item.GetProperty("isEnabled").GetBoolean());
        Assert.All(options, item => Assert.Equal(
            ["id", "isEnabled", "name", "robotName", "state", "workToolGroupRemark"],
            item.EnumerateObject().Select(property => property.Name).Order().ToArray()));
    }

    [Fact]
    public async Task Shared_group_options_include_lifecycle_state_without_secrets()
    {
        await _factory.SeedAsync();
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", SystemRoles.KnowledgeOperator);

        var response = await client.GetAsync(
            "/api/group-options",
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var options = document.RootElement.EnumerateArray().ToArray();
        Assert.Equal(["技术部", "停用群"], options.Select(item => item.GetProperty("name").GetString()));
        Assert.Equal(["enabled", "disabled"], options.Select(item => item.GetProperty("state").GetString()));
        Assert.All(options, item => Assert.Equal(
            ["id", "isEnabled", "name", "robotName", "state", "workToolGroupRemark"],
            item.EnumerateObject().Select(property => property.Name).Order().ToArray()));
    }

    [Fact]
    public async Task Audit_read_correlates_complete_evidence_and_redacts_secrets_and_signed_urls()
    {
        var seeded = await _factory.SeedAsync();
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", SystemRoles.KnowledgeOperator);

        var response = await client.GetAsync($"/api/audit/conversations?groupId={seeded.GroupId:D}&fromUtc=2026-07-23T00:00:00Z&toUtc=2026-07-24T00:00:00Z",
            TestContext.Current.CancellationToken);
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.DoesNotContain("provider-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-signature", json, StringComparison.Ordinal);
        Assert.DoesNotContain("WorkToolRobotId", json, StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(json);
        var item = Assert.Single(document.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("如何重置密码？", item.GetProperty("question").GetString());
        Assert.Equal("请使用安全重置页面。", item.GetProperty("answer").GetString());
        Assert.Equal("completed", item.GetProperty("send").GetProperty("status").GetString());
        Assert.Equal("web_search", item.GetProperty("answerSource").GetString());
        Assert.Equal("web_search_no_sources", item.GetProperty("webSearchFailureCode").GetString());
        Assert.Equal(
            "https://example.com/public",
            Assert.Single(item.GetProperty("webSearchSources").EnumerateArray()).GetProperty("url").GetString());
        Assert.Equal(
            "00000000-0000-0000-0000-000000000456",
            item.GetProperty("modelConfigurationId").GetGuid().ToString("D"));
        Assert.False(item.TryGetProperty("handoff", out _));
        Assert.Equal("approved_pending_index", item.GetProperty("knowledgeCandidate").GetProperty("status").GetString());
        Assert.NotEmpty(item.GetProperty("sources").EnumerateArray());
        Assert.Contains("00000000-0000-0000-0000-000000000123",
            item.GetProperty("sources").EnumerateArray().Select(source => source.GetString()));
        Assert.Contains("[REDACTED]", json, StringComparison.Ordinal);
    }
}

public sealed class ConversationAuditApiFactory : WebApplicationFactory<Program>
{
    private static readonly ServiceProvider InMemoryProvider = new ServiceCollection()
        .AddEntityFrameworkInMemoryDatabase().BuildServiceProvider();
    private readonly string _databaseName = Guid.NewGuid().ToString("N");
    private bool _seeded;
    private SeededAudit _seed = default!;

    public ConversationAuditApiFactory()
    {
        Environment.SetEnvironmentVariable("WECHATROBOT_MASTER_KEY_BASE64", Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        Environment.SetEnvironmentVariable("Jwt__Issuer", "audit-tests");
        Environment.SetEnvironmentVariable("Jwt__Audience", "audit-tests-api");
        Environment.SetEnvironmentVariable("Jwt__SigningKey", "audit-tests-signing-key-must-be-at-least-32-bytes");
        Environment.SetEnvironmentVariable("ConnectionStrings__WechatRobot", "Server=localhost;Database=audit-tests;User=test;Password=test");
        Environment.SetEnvironmentVariable("Cors__AllowedOrigins__0", "https://admin.example.test");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.DisableStartupMigrations();
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<WechatRobotDbContext>>();
            services.RemoveAll<WechatRobotDbContext>();
            services.AddDbContext<WechatRobotDbContext>(options => options.UseInMemoryDatabase(_databaseName).UseInternalServiceProvider(InMemoryProvider));
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = "audit-test";
                options.DefaultChallengeScheme = "audit-test";
                options.DefaultForbidScheme = "audit-test";
            }).AddScheme<AuthenticationSchemeOptions, AuditTestAuthenticationHandler>("audit-test", _ => { });
        });
    }

    public async Task<SeededAudit> SeedAsync()
    {
        if (_seeded) return _seed;
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var at = new DateTime(2026, 7, 23, 1, 2, 3, DateTimeKind.Utc);
        var robot = new RobotConfigEntity { Name = "audit-robot", WorkToolRobotId = "secret-robot-id", CallbackSecretHash = "hash" };
        var group = new GroupProfileEntity { RobotConfigId = robot.Id, ExternalGroupId = "audit-group", Name = "技术部" };
        var disabledGroup = new GroupProfileEntity
        {
            RobotConfigId = robot.Id,
            ExternalGroupId = "disabled-audit-group",
            Name = "停用群",
            WorkToolGroupRemark = "disabled-remark",
            IsEnabled = false
        };
        var inbound = new ConversationMessageEntity { RobotConfigId = robot.Id, GroupProfileId = group.Id, Direction = "inbound", Role = "user",
            WorkToolMessageId = "audit-message", FallbackHash = "audit-in", FallbackWindowStartUtc = at, GroupName = group.Name,
            SenderDisplayName = "测试用户", Text = "如何重置密码？", ReceivedAtUtc = at, CreatedAtUtc = at };
        var outbound = new ConversationMessageEntity { RobotConfigId = robot.Id, GroupProfileId = group.Id, Direction = "outbound", Role = "assistant",
            InReplyToMessageId = inbound.Id, FallbackHash = "audit-out", FallbackWindowStartUtc = at, GroupName = group.Name,
            SenderDisplayName = "机器人", Text = "请使用安全重置页面。", ReceivedAtUtc = at.AddSeconds(1), CreatedAtUtc = at.AddSeconds(1) };
        var audit = new RetrievalAuditEntity { ConversationMessageId = inbound.Id, GroupProfileId = group.Id,
            ModelConfigurationId = Guid.Parse("00000000-0000-0000-0000-000000000456"), Decision = "Answer",
            ConfidenceThreshold = .7, ConfidenceValue = .9, ContextPolicy = "group", CreatedAtUtc = at.AddSeconds(1),
            AnswerSource = "web_search", WebSearchFailureCode = "web_search_no_sources",
            WebSearchSourcesJson = """[{"title":"公开来源","url":"https://example.com/public","site":"Example","index":1},{"title":"秘密来源","url":"https://example.com/private?Signature=raw-signature","index":2}]""",
            EvidenceJson = """[{"documentId":"d1","chunkId":"c1","title":"安全手册","url":"https://oss.test/doc?Signature=raw-signature","apiKey":"provider-secret"},{"documentId":"00000000-0000-0000-0000-000000000123","chunkId":"c2"}]""",
            InputSummaryJson = """{"modelConfigurationId":"m1","authorization":"Bearer provider-secret"}""" };
        var send = new SendCommandEntity { RobotConfigId = robot.Id, GroupProfileId = group.Id, IdempotencyKey = $"grounded-reply:{inbound.Id:D}",
            PayloadJson = """{"Text":"请使用安全重置页面。","WorkToolRobotId":"secret-robot-id"}""", Status = "completed", AttemptCount = 1,
            SentAtUtc = at.AddSeconds(2), CompletedAtUtc = at.AddSeconds(2), CreatedAtUtc = at.AddSeconds(1) };
        var handoff = new HandoffCaseEntity { QuestionMessageId = inbound.Id, RobotConfigId = robot.Id, GroupProfileId = group.Id,
            State = "Resolved", ReasonCode = "explicit_transfer", EvidenceJson = """{"token":"provider-secret"}""",
            CreatedAtUtc = at.AddSeconds(3), UpdatedAtUtc = at.AddSeconds(5) };
        var transition = new HandoffTransitionEntity { HandoffCaseId = handoff.Id, Sequence = 1, FromState = "AIActive", ToState = "WaitingHuman",
            ReasonCode = "explicit_transfer", IdempotencyKey = "transition-audit", CreatedAtUtc = at.AddSeconds(3) };
        var candidate = new KnowledgeCandidateEntity { HandoffCaseId = handoff.Id, QuestionMessageId = inbound.Id, Question = inbound.Text,
            Answer = "人工答案", EvidenceJson = "{}", Status = "approved_pending_index", CreatedAtUtc = at.AddSeconds(5), UpdatedAtUtc = at.AddSeconds(6) };
        db.AddRange(robot, group, disabledGroup, inbound, outbound, audit, send, handoff, transition, candidate);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        _seed = new(group.Id);
        _seeded = true;
        return _seed;
    }
}

public sealed record SeededAudit(Guid GroupId);

public sealed class AuditTestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    System.Text.Encodings.Web.UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var role = Request.Headers["X-Test-Role"].ToString();
        if (string.IsNullOrWhiteSpace(role)) return Task.FromResult(AuthenticateResult.NoResult());
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "audit-user"), new Claim(ClaimTypes.Role, role)], Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
