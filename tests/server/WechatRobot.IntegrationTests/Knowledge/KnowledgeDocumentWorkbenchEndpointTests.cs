using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.IntegrationTests.Knowledge;

public sealed class KnowledgeDocumentWorkbenchEndpointTests(
    DocumentUploadApiFactory factory) : IClassFixture<DocumentUploadApiFactory>
{
    [Fact]
    public async Task Workbench_route_returns_approved_content_and_requires_operator_role()
    {
        var (document, version) = await SeedAsync();

        using var anonymous = factory.CreateClient();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync(
                $"/api/knowledge/documents/{document.Id:D}/versions/{version.Id:D}/workbench",
                TestContext.Current.CancellationToken)).StatusCode);

        using var client = Client(SystemRoles.KnowledgeOperator);
        using var response = await client.GetAsync(
            $"/api/knowledge/documents/{document.Id:D}/versions/{version.Id:D}/workbench",
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(document.Id, json.RootElement.GetProperty("documentId").GetGuid());
        Assert.Equal("批准后的正文", Assert.Single(
            json.RootElement.GetProperty("chunks").EnumerateArray()).GetProperty("text").GetString());
        Assert.DoesNotContain("stagedContent", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("objectKey", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Create_revision_route_returns_created_revision_and_safe_conflicts()
    {
        var (document, version) = await SeedAsync();
        using var client = Client(SystemRoles.KnowledgeOperator);

        using var created = await client.PostAsJsonAsync(
            $"/api/knowledge/documents/{document.Id:D}/versions/{version.Id:D}/revisions",
            new { expectedDocumentStateVersion = document.StateVersion },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var result = await created.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);
        Assert.Equal(2, result.GetProperty("version").GetInt32());
        Assert.Equal(1, result.GetProperty("previewRevision").GetInt32());

        using var conflict = await client.PostAsJsonAsync(
            $"/api/knowledge/documents/{document.Id:D}/versions/{version.Id:D}/revisions",
            new { expectedDocumentStateVersion = document.StateVersion + 1 },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var conflictBody = await conflict.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);
        Assert.Equal(
            "revision-already-editable",
            conflictBody.GetProperty("error").GetString());
        Assert.True(conflictBody.TryGetProperty("existingRevision", out _));
    }

    private async Task<(KnowledgeDocumentEntity Document, KnowledgeDocumentVersionEntity Version)>
        SeedAsync()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var document = new KnowledgeDocumentEntity
        {
            Title = $"Workbench {suffix}",
            Status = "active",
            StateVersion = 4
        };
        var version = new KnowledgeDocumentVersionEntity
        {
            KnowledgeDocumentId = document.Id,
            Version = 1,
            OriginalFileName = "source.txt",
            SafeFileName = "source.txt",
            ContentType = "text/plain",
            Sha256 = suffix.PadRight(64, '0'),
            ObjectKey = "must-not-leak/source.txt",
            Status = "active",
            IsPublished = true,
            SourceKind = "ConversationReview",
            ChangeKind = "New"
        };
        document.ActiveVersionId = version.Id;
        var chunk = new KnowledgeChunkEntity
        {
            KnowledgeDocumentVersionId = version.Id,
            Sequence = 0,
            Text = "批准后的正文",
            Status = "approved"
        };

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        database.AddRange(document, version, chunk);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        database.ChangeTracker.Clear();
        return (document, version);
    }

    private HttpClient Client(string role)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", role);
        return client;
    }
}
