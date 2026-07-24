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
using WechatRobot.Application.Knowledge;
using WechatRobot.Application.Knowledge.Parsing;
using WechatRobot.IntegrationTests.Infrastructure;
using WechatRobot.Application.Jobs;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Worker.Jobs;

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

    [Fact]
    public async Task Worker_recovers_stage_before_oss_cancellation_and_activates_one_parse_job()
    {
        _factory.Storage.Reset();
        _factory.Storage.CancelBeforePut = true;
        Guid documentId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<DocumentUploadService>();
            var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => service.UploadAsync(null, "cancel.txt", "text/plain",
                new MemoryStream("cancel-before-provider"u8.ToArray()), TestContext.Current.CancellationToken));
            Assert.NotNull(exception);
            var db = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            documentId = (await db.KnowledgeDocuments.OrderByDescending(item => item.CreatedAtUtc).FirstAsync(TestContext.Current.CancellationToken)).Id;
            Assert.Contains(await db.DurableJobs.ToArrayAsync(TestContext.Current.CancellationToken), job => job.JobType == "ParseKnowledgeDocument" && job.Status == "blocked");
            var uploadJob = await db.DurableJobs.SingleAsync(job => job.JobType == "UploadKnowledgeDocument" && job.PayloadJson.Contains(documentId.ToString()), TestContext.Current.CancellationToken);
            uploadJob.Status = "leased";
            uploadJob.LeaseOwner = "crashed-api";
            uploadJob.LeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        _factory.Storage.CancelBeforePut = false;
        var worker = new KnowledgeUploadWorker(_factory.Services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
        Assert.True(await worker.ProcessOnceAsync(TestContext.Current.CancellationToken));

        await AssertUploadedWithOneParseJobAsync(documentId);
        Assert.Equal(1, _factory.Storage.SuccessfulPuts);
    }

    [Fact]
    public async Task Worker_reclaims_retryable_failed_upload_when_delay_is_due()
    {
        _factory.Storage.Reset();
        _factory.Storage.FailPut = true;
        using var client = CreateClient(SystemRoles.KnowledgeOperator);
        var failed = await UploadTextAsync(client, "worker-retry.txt", "worker-retry-content");
        var documentId = (await failed.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("documentId").GetGuid();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            var uploadJob = await db.DurableJobs.SingleAsync(job => job.JobType == "UploadKnowledgeDocument" && job.PayloadJson.Contains(documentId.ToString()), TestContext.Current.CancellationToken);
            Assert.Equal("retrying", uploadJob.Status);
            uploadJob.NextAttemptAtUtc = DateTime.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        _factory.Storage.FailPut = false;
        var worker = new KnowledgeUploadWorker(_factory.Services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
        Assert.True(await worker.ProcessOnceAsync(TestContext.Current.CancellationToken));
        await AssertUploadedWithOneParseJobAsync(documentId);
    }

    [Fact]
    public async Task Worker_replays_same_key_after_oss_side_effect_before_database_mark()
    {
        _factory.Storage.Reset();
        _factory.Storage.CancelAfterPut = true;
        Guid documentId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<DocumentUploadService>();
            await Assert.ThrowsAsync<OperationCanceledException>(() => service.UploadAsync(null, "provider-crash.txt", "text/plain",
                new MemoryStream("provider-side-effect"u8.ToArray()), TestContext.Current.CancellationToken));
            var db = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            documentId = (await db.KnowledgeDocuments.OrderByDescending(item => item.CreatedAtUtc).FirstAsync(TestContext.Current.CancellationToken)).Id;
        }

        _factory.Storage.CancelAfterPut = false;
        var worker = new KnowledgeUploadWorker(_factory.Services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
        Assert.True(await worker.ProcessOnceAsync(TestContext.Current.CancellationToken));

        await AssertUploadedWithOneParseJobAsync(documentId);
        Assert.Equal(2, _factory.Storage.PutCalls);
        Assert.Single(_factory.Storage.ObjectKeys.Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Failed_upload_cannot_parse_and_delete_cancels_jobs_blocks_retry_and_is_idempotent()
    {
        _factory.Storage.Reset();
        _factory.Storage.FailPut = true;
        using var operatorClient = CreateClient(SystemRoles.KnowledgeOperator);
        using var failed = await UploadTextAsync(operatorClient, "race.txt", "delete-race");
        var body = await failed.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var documentId = body.GetProperty("documentId").GetGuid();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IDurableJobRepository>();
            Assert.Null(await repository.LeaseNextJobAsync("ParseKnowledgeDocument", "parser", DateTime.UtcNow.AddMinutes(1), TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken));
        }

        using var adminClient = CreateClient(SystemRoles.Admin);
        Assert.Equal(HttpStatusCode.Accepted, (await adminClient.DeleteAsync($"/api/knowledge/documents/{documentId}/physical", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await adminClient.DeleteAsync($"/api/knowledge/documents/{documentId}/physical", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await operatorClient.PostAsync($"/api/knowledge/documents/{documentId}/retry-upload", null, TestContext.Current.CancellationToken)).StatusCode);

        await using var verify = _factory.Services.CreateAsyncScope();
        var db = verify.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var jobs = await db.DurableJobs.Where(job => job.PayloadJson.Contains(documentId.ToString())).ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Single(jobs, job => job.JobType == "CleanupKnowledgeDocument" && job.Status != "completed");
        Assert.All(jobs.Where(job => job.JobType is "UploadKnowledgeDocument" or "ParseKnowledgeDocument"), job => Assert.Equal("cancelled", job.Status));
    }

    [Fact]
    public async Task Delete_wins_when_provider_put_finished_before_database_activation()
    {
        _factory.Storage.Reset();
        _factory.Storage.CancelBeforePut = true;
        await using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<DocumentUploadService>();
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.UploadAsync(null, "delete-wins.txt", "text/plain",
            new MemoryStream("delete-wins-content"u8.ToArray()), TestContext.Current.CancellationToken));
        var db = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var document = await db.KnowledgeDocuments.OrderByDescending(item => item.CreatedAtUtc).FirstAsync(TestContext.Current.CancellationToken);
        var version = await db.KnowledgeDocumentVersions.SingleAsync(item => item.KnowledgeDocumentId == document.Id, TestContext.Current.CancellationToken);
        var store = scope.ServiceProvider.GetRequiredService<IKnowledgeDocumentStore>();
        var pending = await store.GetRecoverableAsync(version.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(pending);

        await service.RequestPhysicalDeleteAsync(document.Id, TestContext.Current.CancellationToken);
        var activated = await store.MarkUploadedAsync(pending!, new StoredObject(pending!.ObjectKey, new Uri($"https://public.example.test/{pending.ObjectKey}")), TestContext.Current.CancellationToken);
        Assert.False(activated);
        db.ChangeTracker.Clear();
        Assert.Equal("disabled", (await db.KnowledgeDocumentVersions.SingleAsync(item => item.Id == version.Id, TestContext.Current.CancellationToken)).Status);
        Assert.Equal("cancelled", (await db.DurableJobs.SingleAsync(job => job.JobType == "ParseKnowledgeDocument" && job.PayloadJson.Contains(document.Id.ToString()), TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task Stale_worker_failure_cannot_regress_an_already_completed_upload()
    {
        _factory.Storage.Reset();
        _factory.Storage.CancelBeforePut = true;
        await using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<DocumentUploadService>();
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.UploadAsync(null, "stale-worker.txt", "text/plain",
            new MemoryStream("stale-worker-content"u8.ToArray()), TestContext.Current.CancellationToken));
        var db = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var version = await db.KnowledgeDocumentVersions.OrderByDescending(item => item.CreatedAtUtc).FirstAsync(TestContext.Current.CancellationToken);
        var store = scope.ServiceProvider.GetRequiredService<IKnowledgeDocumentStore>();
        var pending = await store.GetRecoverableAsync(version.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(pending);
        Assert.True(await store.MarkUploadedAsync(pending!, new StoredObject(pending!.ObjectKey, new Uri($"https://public.example.test/{pending.ObjectKey}")), TestContext.Current.CancellationToken));

        await store.MarkFailedAsync(pending, TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        Assert.Equal("uploaded", (await db.KnowledgeDocumentVersions.SingleAsync(item => item.Id == version.Id, TestContext.Current.CancellationToken)).Status);
        Assert.Equal("completed", (await db.DurableJobs.SingleAsync(job => job.JobType == "UploadKnowledgeDocument" && job.PayloadJson.Contains(version.Id.ToString()), TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task Upload_retry_and_delete_enforce_the_role_matrix()
    {
        _factory.Storage.Reset();
        using var anonymous = _factory.CreateClient();
        using var human = CreateClient(SystemRoles.HumanAgent);
        using var admin = CreateClient(SystemRoles.Admin);
        using var knowledgeOperator = CreateClient(SystemRoles.KnowledgeOperator);
        Assert.Equal(HttpStatusCode.Unauthorized, (await UploadTextAsync(anonymous, "anonymous.txt", "auth-anon")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await UploadTextAsync(human, "human.txt", "auth-human")).StatusCode);
        Assert.True((await UploadTextAsync(admin, "admin.txt", "auth-admin")).IsSuccessStatusCode);
        Assert.True((await UploadTextAsync(knowledgeOperator, "operator.txt", "auth-operator")).IsSuccessStatusCode);

        _factory.Storage.FailPut = true;
        var adminFailed = await UploadTextAsync(admin, "admin-retry.txt", "auth-admin-retry");
        var adminId = (await adminFailed.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("documentId").GetGuid();
        var operatorFailed = await UploadTextAsync(knowledgeOperator, "operator-retry.txt", "auth-operator-retry");
        var operatorId = (await operatorFailed.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("documentId").GetGuid();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync($"/api/knowledge/documents/{adminId}/retry-upload", null, TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await human.PostAsync($"/api/knowledge/documents/{adminId}/retry-upload", null, TestContext.Current.CancellationToken)).StatusCode);
        _factory.Storage.FailPut = false;
        Assert.True((await admin.PostAsync($"/api/knowledge/documents/{adminId}/retry-upload", null, TestContext.Current.CancellationToken)).IsSuccessStatusCode);
        Assert.True((await knowledgeOperator.PostAsync($"/api/knowledge/documents/{operatorId}/retry-upload", null, TestContext.Current.CancellationToken)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await knowledgeOperator.DeleteAsync($"/api/knowledge/documents/{operatorId}/physical", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await admin.DeleteAsync($"/api/knowledge/documents/{operatorId}/physical", TestContext.Current.CancellationToken)).StatusCode);
    }

    private async Task AssertUploadedWithOneParseJobAsync(Guid documentId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        Assert.Equal("uploaded", (await db.KnowledgeDocumentVersions.SingleAsync(item => item.KnowledgeDocumentId == documentId, TestContext.Current.CancellationToken)).Status);
        var parseJobs = await db.DurableJobs.Where(job => job.JobType == "ParseKnowledgeDocument" && job.PayloadJson.Contains(documentId.ToString())).ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Single(parseJobs);
        Assert.Equal("pending", parseJobs[0].Status);
        Assert.Equal("completed", (await db.DurableJobs.SingleAsync(job => job.JobType == "UploadKnowledgeDocument" && job.PayloadJson.Contains(documentId.ToString()), TestContext.Current.CancellationToken)).Status);
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
        builder.DisableStartupMigrations();
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
            foreach (var item in services.Where(service => service.ServiceType == typeof(IDurableJobRepository)).ToArray()) services.Remove(item);
            foreach (var item in services.Where(service => service.ServiceType == typeof(IDocumentSourceReader)).ToArray()) services.Remove(item);
            services.AddSingleton(Storage);
            services.AddSingleton<IObjectStorage>(Storage);
            services.AddScoped<IDurableJobRepository, InMemoryKnowledgeJobRepository>();
            services.AddSingleton<IDocumentSourceReader, FakeDocumentSourceReader>();
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = "document-tests";
                options.DefaultChallengeScheme = "document-tests";
                options.DefaultForbidScheme = "document-tests";
            }).AddScheme<AuthenticationSchemeOptions, RoleHeaderAuthenticationHandler>("document-tests", _ => { });
        });
    }
}

public sealed class FakeDocumentSourceReader : IDocumentSourceReader
{
    public Task<Stream> OpenReadAsync(Uri publicUrl, DocumentProcessingContext context)
    {
        context.Checkpoint("source-http");
        var bytes = "alpha beta"u8.ToArray();
        context.ReserveSource(bytes.Length);
        return Task.FromResult<Stream>(new MemoryStream(bytes, 0, bytes.Length, writable: false, publiclyVisible: true));
    }
}

public sealed class InMemoryKnowledgeJobRepository(WechatRobotDbContext database) : IDurableJobRepository
{
    public async Task<LeasedDurableJob?> LeaseNextJobAsync(string jobType, string leaseOwner, DateTime nowUtc, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        var job = await database.DurableJobs.Where(item => item.JobType == jobType &&
                (((item.Status == "pending" || item.Status == "retrying") && item.NextAttemptAtUtc <= nowUtc) ||
                 (item.Status == "leased" && item.LeaseExpiresAtUtc <= nowUtc)))
            .OrderBy(item => item.NextAttemptAtUtc).ThenBy(item => item.CreatedAtUtc).FirstOrDefaultAsync(cancellationToken);
        if (job is null) return null;
        job.Status = "leased"; job.LeaseOwner = leaseOwner; job.LeaseExpiresAtUtc = nowUtc.Add(leaseDuration); job.Version++;
        await database.SaveChangesAsync(cancellationToken);
        return new LeasedDurableJob(job.Id, job.JobType, job.PayloadJson, job.AttemptCount, leaseOwner);
    }
    public async Task CompleteJobAsync(Guid jobId, string leaseOwner, DateTime completedAtUtc, CancellationToken cancellationToken)
    {
        var job = await database.DurableJobs.SingleAsync(item => item.Id == jobId, cancellationToken);
        if (job.Status != "leased" || job.LeaseOwner != leaseOwner) return;
        job.Status = "completed"; job.CompletedAtUtc = completedAtUtc; job.LeaseOwner = null; job.LeaseExpiresAtUtc = null;
        await database.SaveChangesAsync(cancellationToken);
    }
    public async Task FailJobAsync(LeasedDurableJob leased, string reason, DateTime failedAtUtc, CancellationToken cancellationToken)
    {
        var job = await database.DurableJobs.SingleAsync(item => item.Id == leased.Id, cancellationToken);
        if (job.Status != "leased" || job.LeaseOwner != leased.LeaseOwner) return;
        job.Status = "retrying"; job.AttemptCount++; job.NextAttemptAtUtc = failedAtUtc; job.LeaseOwner = null; job.LeaseExpiresAtUtc = null;
        await database.SaveChangesAsync(cancellationToken);
    }
    public Task<InboundMessageIngestResult> IngestInboundMessageAsync(InboundMessageIngestRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<EnqueueSendCommandResult> EnqueueSendCommandAsync(EnqueueSendCommandRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<LeasedSendCommand?> LeaseNextSendCommandAsync(string leaseOwner, DateTime nowUtc, TimeSpan leaseDuration, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<bool> MarkSendDispatchingAsync(LeasedSendCommand command, DateTime dispatchedAtUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task MarkSendDeliveryUnknownAsync(LeasedSendCommand command, string reason, DateTime failedAtUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task MarkSendAcceptedAsync(LeasedSendCommand command, string workToolMessageId, DateTime acceptedAtUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task MarkSendRejectedAsync(LeasedSendCommand command, string reason, DateTime rejectedAtUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task FailSendCommandAsync(LeasedSendCommand command, string reason, DateTime failedAtUtc, TimeSpan? retryDelay, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<bool> RenewSendLeasesAsync(LeasedSendCommand command, DateTime nowUtc, TimeSpan leaseDuration, CancellationToken cancellationToken) => throw new NotSupportedException();
}

public sealed class FakeObjectStorage : IObjectStorage
{
    public bool FailPut { get; set; }
    public bool CancelBeforePut { get; set; }
    public bool CancelAfterPut { get; set; }
    public int SuccessfulPuts { get; private set; }
    public int PutCalls { get; private set; }
    public int Deletes { get; private set; }
    public List<string> ObjectKeys { get; } = [];
    public Task<StoredObject> PutAsync(string objectKey, Stream content, string contentType, CancellationToken cancellationToken)
    {
        PutCalls++;
        if (CancelBeforePut) throw new OperationCanceledException("simulated cancellation before provider side effect");
        if (FailPut) throw new IOException("simulated provider failure");
        SuccessfulPuts++;
        ObjectKeys.Add(objectKey);
        if (CancelAfterPut) throw new OperationCanceledException("simulated cancellation after provider side effect");
        return Task.FromResult(new StoredObject(objectKey, new Uri($"https://public.example.test/{objectKey}")));
    }
    public Task DeleteAsync(string objectKey, CancellationToken cancellationToken) { Deletes++; return Task.CompletedTask; }
    public void Reset() { FailPut = false; CancelBeforePut = false; CancelAfterPut = false; SuccessfulPuts = 0; PutCalls = 0; Deletes = 0; ObjectKeys.Clear(); }
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
