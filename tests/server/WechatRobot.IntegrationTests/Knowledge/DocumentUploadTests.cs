using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WechatRobot.Application.Storage;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.Infrastructure.Persistence;

namespace WechatRobot.IntegrationTests.Knowledge;

public sealed class DocumentUploadTests : IClassFixture<DocumentUploadApiFactory>
{
    private readonly DocumentUploadApiFactory _factory;
    public DocumentUploadTests(DocumentUploadApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Failed_storage_upload_is_retryable_and_never_published()
    {
        _factory.Storage.Reset();
        _factory.Storage.FailPut = true;
        using var client = CreateClient(SystemRoles.KnowledgeOperator);
        using var response = await UploadTextAsync(client, "failure.txt", "retry me");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var documentId = body.GetProperty("documentId").GetGuid();
        Assert.Equal("failed", body.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("publicUrl").ValueKind);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            var version = await db.KnowledgeDocumentVersions.SingleAsync(item => item.KnowledgeDocumentId == documentId, TestContext.Current.CancellationToken);
            Assert.Equal("failed", version.Status);
            Assert.NotEmpty(version.StagedContent);
            Assert.Equal(2, await db.DurableJobs.CountAsync(job => job.PayloadJson.Contains(documentId.ToString()), TestContext.Current.CancellationToken));
            Assert.DoesNotContain(await db.KnowledgeDocumentVersions.ToArrayAsync(TestContext.Current.CancellationToken), item => item.IsPublished);
        }

        _factory.Storage.FailPut = false;
        var retried = await client.PostAsync($"/api/knowledge/documents/{documentId}/retry-upload", null, TestContext.Current.CancellationToken);
        retried.EnsureSuccessStatusCode();
        var retryBody = await retried.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("uploaded", retryBody.GetProperty("state").GetString());
        Assert.StartsWith("https://public.example.test/wechatrobot/knowledge/", retryBody.GetProperty("publicUrl").GetString());
        Assert.True(retryBody.GetProperty("publicReadRiskAccepted").GetBoolean());
        Assert.Equal(1, _factory.Storage.SuccessfulPuts);
    }

    [Fact]
    public async Task Upload_creates_version_jobs_safe_key_and_rejects_duplicate_hash()
    {
        _factory.Storage.Reset();
        _factory.Storage.FailPut = false;
        using var client = CreateClient(SystemRoles.KnowledgeOperator);
        using var first = await UploadTextAsync(client, "../../client supplied.txt", "same-content");
        first.EnsureSuccessStatusCode();
        var body = await first.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var key = body.GetProperty("objectKey").GetString()!;
        Assert.Matches(@"^wechatrobot/knowledge/[0-9a-f]{32}/1/source/source\.txt$", key);
        Assert.DoesNotContain("client", key, StringComparison.OrdinalIgnoreCase);

        using var duplicate = await UploadTextAsync(client, "different.txt", "same-content");
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task Physical_delete_is_admin_only_and_enqueues_cleanup_without_inline_delete()
    {
        _factory.Storage.Reset();
        _factory.Storage.FailPut = false;
        using var operatorClient = CreateClient(SystemRoles.KnowledgeOperator);
        using var uploaded = await UploadTextAsync(operatorClient, "delete.txt", "delete later");
        uploaded.EnsureSuccessStatusCode();
        var body = await uploaded.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var documentId = body.GetProperty("documentId").GetGuid();

        var forbidden = await operatorClient.DeleteAsync($"/api/knowledge/documents/{documentId}/physical", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(0, _factory.Storage.Deletes);

        using var adminClient = CreateClient(SystemRoles.Admin);
        var accepted = await adminClient.DeleteAsync($"/api/knowledge/documents/{documentId}/physical", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Equal(0, _factory.Storage.Deletes);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        Assert.Contains(await db.DurableJobs.ToArrayAsync(TestContext.Current.CancellationToken), job => job.JobType == "CleanupKnowledgeDocument" && job.PayloadJson.Contains(documentId.ToString()));
    }

    private HttpClient CreateClient(string role)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", role);
        return client;
    }

    private static Task<HttpResponseMessage> UploadTextAsync(HttpClient client, string name, string text)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(file, "file", name);
        return client.PostAsync("/api/knowledge/documents", form, TestContext.Current.CancellationToken);
    }
}

public sealed class DocumentUploadApiFactory : WebApplicationFactory<Program>
{
    private static readonly ServiceProvider InMemoryProvider = new ServiceCollection().AddEntityFrameworkInMemoryDatabase().BuildServiceProvider();
    private readonly string _databaseName = Guid.NewGuid().ToString("N");
    public FakeObjectStorage Storage { get; } = new();

    public DocumentUploadApiFactory()
    {
        Environment.SetEnvironmentVariable("WECHATROBOT_MASTER_KEY_BASE64", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        Environment.SetEnvironmentVariable("ConnectionStrings__WechatRobot", "Server=localhost;Database=unused");
        Environment.SetEnvironmentVariable("Cors__AllowedOrigins__0", "https://admin.example.test");
        Environment.SetEnvironmentVariable("Jwt__Issuer", "document-tests");
        Environment.SetEnvironmentVariable("Jwt__Audience", "document-tests-api");
        Environment.SetEnvironmentVariable("Jwt__SigningKey", "document-tests-signing-key-must-be-at-least-32-bytes");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Issuer"] = "document-tests", ["Jwt:Audience"] = "document-tests-api",
            ["Jwt:SigningKey"] = "document-tests-signing-key-must-be-at-least-32-bytes",
            ["ConnectionStrings:WechatRobot"] = "Server=localhost;Database=unused", ["Cors:AllowedOrigins:0"] = "https://admin.example.test",
            ["DocumentUpload:MaximumBytes"] = "1024", ["Oss:PublicReadRiskAccepted"] = "true"
        }));
        builder.ConfigureServices(services =>
        {
            var descriptor = services.Single(service => service.ServiceType == typeof(DbContextOptions<WechatRobotDbContext>));
            services.Remove(descriptor);
            services.AddDbContext<WechatRobotDbContext>(options => options.UseInMemoryDatabase(_databaseName).UseInternalServiceProvider(InMemoryProvider));
            foreach (var item in services.Where(service => service.ServiceType == typeof(IObjectStorage)).ToArray()) services.Remove(item);
            services.AddSingleton(Storage);
            services.AddSingleton<IObjectStorage>(Storage);
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = "document-tests";
                options.DefaultChallengeScheme = "document-tests";
                options.DefaultForbidScheme = "document-tests";
            }).AddScheme<AuthenticationSchemeOptions, RoleHeaderAuthenticationHandler>("document-tests", _ => { });
        });
    }
}

public sealed class FakeObjectStorage : IObjectStorage
{
    public bool FailPut { get; set; }
    public int SuccessfulPuts { get; private set; }
    public int Deletes { get; private set; }
    public Task<StoredObject> PutAsync(string objectKey, Stream content, string contentType, CancellationToken cancellationToken)
    {
        if (FailPut) throw new IOException("simulated provider failure");
        SuccessfulPuts++;
        return Task.FromResult(new StoredObject(objectKey, new Uri($"https://public.example.test/{objectKey}")));
    }
    public Task DeleteAsync(string objectKey, CancellationToken cancellationToken) { Deletes++; return Task.CompletedTask; }
    public void Reset() { FailPut = false; SuccessfulPuts = 0; Deletes = 0; }
}

public sealed class RoleHeaderAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, System.Text.Encodings.Web.UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var role = Request.Headers["X-Test-Role"].ToString();
        if (string.IsNullOrWhiteSpace(role)) return Task.FromResult(AuthenticateResult.NoResult());
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "document-user"), new Claim(ClaimTypes.Role, role)], Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}
