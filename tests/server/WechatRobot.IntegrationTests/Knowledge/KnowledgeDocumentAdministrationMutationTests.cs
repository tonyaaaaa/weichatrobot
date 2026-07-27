using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.IntegrationTests.Knowledge;

public sealed class KnowledgeDocumentAdministrationMutationTests : IClassFixture<DocumentUploadApiFactory>
{
    private readonly DocumentUploadApiFactory _factory;

    public KnowledgeDocumentAdministrationMutationTests(DocumentUploadApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Retry_requires_current_state_version_and_writes_sanitized_audit()
    {
        _factory.Storage.Reset();
        _factory.Storage.FailPut = true;
        using var client = CreateClient(SystemRoles.KnowledgeOperator);
        var documentId = await UploadAsync(client, "retry-audit.txt", UniqueContent(), HttpStatusCode.ServiceUnavailable);
        var currentVersion = await StateVersionAsync(documentId);

        using var stale = await client.PostAsJsonAsync(
            $"/api/knowledge/documents/{documentId}/retry-upload",
            new { expectedStateVersion = currentVersion + 1 },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        await AssertConcurrencyConflictAsync(stale, documentId, currentVersion);
        Assert.Equal(0, await AuditCountAsync(documentId, "knowledge-document.retry-upload"));

        _factory.Storage.FailPut = false;
        using var retried = await client.PostAsJsonAsync(
            $"/api/knowledge/documents/{documentId}/retry-upload",
            new { expectedStateVersion = currentVersion },
            TestContext.Current.CancellationToken);
        retried.EnsureSuccessStatusCode();

        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var document = await database.KnowledgeDocuments.AsNoTracking().SingleAsync(
            item => item.Id == documentId,
            TestContext.Current.CancellationToken);
        Assert.Equal(currentVersion + 1, document.StateVersion);
        var audit = await database.AdministrationAudits.AsNoTracking().SingleAsync(
            item => item.TargetId == documentId.ToString("D") &&
                    item.Action == "knowledge-document.retry-upload",
            TestContext.Current.CancellationToken);
        Assert.Equal("document-user", audit.Actor);
        AssertAuditIsSanitized(audit);
    }

    [Fact]
    public async Task Retry_rejects_a_failed_non_latest_version_without_audit()
    {
        _factory.Storage.Reset();
        _factory.Storage.FailPut = true;
        using var client = CreateClient(SystemRoles.KnowledgeOperator);
        var documentId = await UploadAsync(client, "old-failed.txt", UniqueContent(), HttpStatusCode.ServiceUnavailable);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            database.KnowledgeDocumentVersions.Add(new KnowledgeDocumentVersionEntity
            {
                KnowledgeDocumentId = documentId,
                Version = 2,
                OriginalFileName = "newer.txt",
                SafeFileName = "source.txt",
                ContentType = "text/plain",
                Sha256 = Guid.NewGuid().ToString("N"),
                SizeBytes = 3,
                ObjectKey = $"wechatrobot/knowledge/{documentId:N}/2/source/source.txt",
                Status = "uploaded",
                PublicUrl = "https://public.example.test/newer.txt",
                StagedContent = []
            });
            var document = await database.KnowledgeDocuments.SingleAsync(
                item => item.Id == documentId,
                TestContext.Current.CancellationToken);
            document.Status = "uploaded";
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        _factory.Storage.FailPut = false;
        using var response = await client.PostAsJsonAsync(
            $"/api/knowledge/documents/{documentId}/retry-upload",
            new { expectedStateVersion = await StateVersionAsync(documentId) },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("document-not-retryable", body.GetProperty("error").GetString());
        Assert.Equal(0, await AuditCountAsync(documentId, "knowledge-document.retry-upload"));
    }

    [Fact]
    public async Task Retry_rejects_latest_failed_version_when_staged_content_is_missing()
    {
        _factory.Storage.Reset();
        _factory.Storage.FailPut = true;
        using var client = CreateClient(SystemRoles.KnowledgeOperator);
        var documentId = await UploadAsync(client, "missing-stage.txt", UniqueContent(), HttpStatusCode.ServiceUnavailable);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            var version = await database.KnowledgeDocumentVersions.SingleAsync(
                item => item.KnowledgeDocumentId == documentId,
                TestContext.Current.CancellationToken);
            version.StagedContent = [];
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var response = await client.PostAsJsonAsync(
            $"/api/knowledge/documents/{documentId}/retry-upload",
            new { expectedStateVersion = await StateVersionAsync(documentId) },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("document-not-retryable", body.GetProperty("error").GetString());
        Assert.Equal(0, await AuditCountAsync(documentId, "knowledge-document.retry-upload"));
    }

    [Fact]
    public async Task Failed_retry_audits_only_safe_failure_category()
    {
        _factory.Storage.Reset();
        _factory.Storage.FailPut = true;
        using var client = CreateClient(SystemRoles.KnowledgeOperator);
        var documentId = await UploadAsync(client, "retry-fails.txt", UniqueContent(), HttpStatusCode.ServiceUnavailable);

        using var response = await client.PostAsJsonAsync(
            $"/api/knowledge/documents/{documentId}/retry-upload",
            new { expectedStateVersion = await StateVersionAsync(documentId) },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var audit = await database.AdministrationAudits.AsNoTracking().SingleAsync(
            item => item.TargetId == documentId.ToString("D") &&
                    item.Action == "knowledge-document.retry-upload",
            TestContext.Current.CancellationToken);
        AssertAuditIsSanitized(audit);
        using var detail = JsonDocument.Parse(audit.SanitizedDetailJson);
        Assert.Equal(
            "object-storage-upload-failed",
            detail.RootElement.GetProperty("failureCategory").GetString());
    }

    [Fact]
    public async Task Management_mutation_without_stable_actor_is_rejected_before_change()
    {
        _factory.Storage.Reset();
        _factory.Storage.FailPut = true;
        using var client = CreateClient(SystemRoles.KnowledgeOperator);
        var documentId = await UploadAsync(client, "actor-required.txt", UniqueContent(), HttpStatusCode.ServiceUnavailable);
        var stateVersion = await StateVersionAsync(documentId);
        using var missingActor = CreateClient(SystemRoles.KnowledgeOperator);
        missingActor.DefaultRequestHeaders.Add("X-Test-No-Name", "true");

        using var response = await missingActor.PostAsJsonAsync(
            $"/api/knowledge/documents/{documentId}/retry-upload",
            new { expectedStateVersion = stateVersion },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(stateVersion, await StateVersionAsync(documentId));
        Assert.Equal(0, await AuditCountAsync(documentId, "knowledge-document.retry-upload"));
    }

    [Fact]
    public async Task Disable_is_idempotent_only_at_current_version_and_audits_once()
    {
        _factory.Storage.Reset();
        using var client = CreateClient(SystemRoles.KnowledgeOperator);
        var documentId = await UploadAsync(client, "disable.txt", UniqueContent(), HttpStatusCode.Created);
        var initialVersion = await StateVersionAsync(documentId);

        using var disabled = await client.PostAsJsonAsync(
            $"/api/knowledge/documents/{documentId}/disable",
            new { expectedStateVersion = initialVersion },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, disabled.StatusCode);
        Assert.Equal(initialVersion + 1, await StateVersionAsync(documentId));
        Assert.Equal(1, await AuditCountAsync(documentId, "knowledge-document.disable"));

        using var stale = await client.PostAsJsonAsync(
            $"/api/knowledge/documents/{documentId}/disable",
            new { expectedStateVersion = initialVersion },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        await AssertConcurrencyConflictAsync(stale, documentId, initialVersion + 1);

        using var idempotent = await client.PostAsJsonAsync(
            $"/api/knowledge/documents/{documentId}/disable",
            new { expectedStateVersion = initialVersion + 1 },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, idempotent.StatusCode);
        Assert.Equal(1, await AuditCountAsync(documentId, "knowledge-document.disable"));

        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var document = await database.KnowledgeDocuments.AsNoTracking().SingleAsync(
            item => item.Id == documentId,
            TestContext.Current.CancellationToken);
        Assert.False(document.IsDeleteRequested);
        Assert.Equal("disabled", document.Status);
    }

    [Fact]
    public async Task Physical_delete_requires_current_version_and_records_async_cleanup_audit()
    {
        _factory.Storage.Reset();
        using var operatorClient = CreateClient(SystemRoles.KnowledgeOperator);
        var documentId = await UploadAsync(operatorClient, "physical.txt", UniqueContent(), HttpStatusCode.Created);
        var initialVersion = await StateVersionAsync(documentId);
        using var admin = CreateClient(SystemRoles.Admin);

        using var stale = await admin.DeleteAsync(
            $"/api/knowledge/documents/{documentId}/physical?expectedStateVersion={initialVersion + 1}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        await AssertConcurrencyConflictAsync(stale, documentId, initialVersion);
        Assert.Equal(0, await AuditCountAsync(documentId, "knowledge-document.request-physical-delete"));

        using var accepted = await admin.DeleteAsync(
            $"/api/knowledge/documents/{documentId}/physical?expectedStateVersion={initialVersion}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);

        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var document = await database.KnowledgeDocuments.AsNoTracking().SingleAsync(
            item => item.Id == documentId,
            TestContext.Current.CancellationToken);
        Assert.True(document.IsDeleteRequested);
        Assert.Equal("disabled", document.Status);
        Assert.Equal(initialVersion + 1, document.StateVersion);
        Assert.All(
            await database.KnowledgeDocumentVersions.AsNoTracking()
                .Where(item => item.KnowledgeDocumentId == documentId)
                .ToArrayAsync(TestContext.Current.CancellationToken),
            version => Assert.Equal("disabled", version.Status));
        Assert.Contains(
            await database.DurableJobs.AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken),
            job => job.JobType == "CleanupKnowledgeDocument" &&
                   job.PayloadJson.Contains(documentId.ToString()));
        var audit = await database.AdministrationAudits.AsNoTracking().SingleAsync(
            item => item.TargetId == documentId.ToString("D") &&
                    item.Action == "knowledge-document.request-physical-delete",
            TestContext.Current.CancellationToken);
        AssertAuditIsSanitized(audit);
    }

    private HttpClient CreateClient(string role)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", role);
        return client;
    }

    private static string UniqueContent() => $"{Guid.NewGuid():N}-document-management";

    private static async Task<Guid> UploadAsync(
        HttpClient client,
        string name,
        string content,
        HttpStatusCode expectedStatus)
    {
        using var form = new MultipartFormDataContent();
        using var file = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(file, "file", name);
        using var response = await client.PostAsync(
            "/api/knowledge/documents",
            form,
            TestContext.Current.CancellationToken);
        Assert.Equal(expectedStatus, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return body.GetProperty("documentId").GetGuid();
    }

    private async Task<int> StateVersionAsync(Guid documentId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        return await database.KnowledgeDocuments.AsNoTracking()
            .Where(item => item.Id == documentId)
            .Select(item => item.StateVersion)
            .SingleAsync(TestContext.Current.CancellationToken);
    }

    private async Task<int> AuditCountAsync(Guid documentId, string action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        return await database.AdministrationAudits.AsNoTracking().CountAsync(
            item => item.TargetId == documentId.ToString("D") && item.Action == action,
            TestContext.Current.CancellationToken);
    }

    private static async Task AssertConcurrencyConflictAsync(
        HttpResponseMessage response,
        Guid documentId,
        int stateVersion)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("document-concurrency-conflict", body.GetProperty("error").GetString());
        var current = body.GetProperty("current");
        Assert.Equal(documentId, current.GetProperty("id").GetGuid());
        Assert.Equal(stateVersion, current.GetProperty("stateVersion").GetInt32());
    }

    private static void AssertAuditIsSanitized(AdministrationAuditEntity audit)
    {
        Assert.DoesNotContain(
            new[]
            {
                "objectKey",
                "publicUrl",
                "stagedContent",
                "payloadJson",
                "authorization",
                "credential",
                "secret"
            },
            value => audit.SanitizedDetailJson.Contains(value, StringComparison.OrdinalIgnoreCase));
        using var detail = JsonDocument.Parse(audit.SanitizedDetailJson);
        Assert.Equal(JsonValueKind.Object, detail.RootElement.ValueKind);
    }
}
