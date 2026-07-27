using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WechatRobot.Infrastructure.Knowledge;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.IntegrationTests.Knowledge;

public sealed class KnowledgeDocumentAdministrationQueryTests
{
    [Fact]
    public async Task List_filters_and_orders_documents_with_latest_retry_truth()
    {
        await using var database = NewDatabase();
        var now = new DateTime(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);
        var first = Document("00000000-0000-0000-0000-000000000001", "Product Manual", "failed", now);
        var second = Document("00000000-0000-0000-0000-000000000002", "Product FAQ", "uploaded", now);
        var ignored = Document("00000000-0000-0000-0000-000000000003", "Support", "active", now.AddMinutes(1));
        database.KnowledgeDocuments.AddRange(first, second, ignored);
        database.KnowledgeDocumentVersions.AddRange(
            Version(first.Id, 1, "failed", staged: true, failure: "Object storage upload failed; retry is available."),
            Version(second.Id, 1, "uploaded"));
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await new KnowledgeDocumentAdministrationQuery(database).ListAsync(
            "product",
            status: null,
            page: 0,
            pageSize: 200,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Page);
        Assert.Equal(100, result.PageSize);
        Assert.Equal(2, result.Total);
        Assert.Equal([first.Id, second.Id], result.Items.Select(item => item.Id));
        Assert.True(result.Items[0].CanRetryUpload);
        Assert.False(result.Items[1].CanRetryUpload);
        Assert.Equal("failed", result.Items[0].LatestVersionStatus);
    }

    [Fact]
    public async Task Detail_projects_persisted_version_evidence_without_secret_payloads()
    {
        await using var database = NewDatabase();
        var document = Document(
            "10000000-0000-0000-0000-000000000001",
            "Operations",
            "active",
            DateTime.UtcNow);
        var oldVersion = Version(document.Id, 1, "failed", staged: true, failure: "safe failure");
        var currentVersion = Version(document.Id, 2, "active");
        currentVersion.PublicUrl = "https://public.example.test/source.pdf";
        currentVersion.PreviewRevision = 4;
        currentVersion.IsPublished = true;
        document.ActiveVersionId = currentVersion.Id;
        database.KnowledgeDocuments.Add(document);
        database.KnowledgeDocumentVersions.AddRange(oldVersion, currentVersion);
        database.KnowledgeChunkPreviews.AddRange(
            new KnowledgeChunkPreviewEntity { KnowledgeDocumentVersionId = currentVersion.Id, Sequence = 1, Text = "secret document text" },
            new KnowledgeChunkPreviewEntity { KnowledgeDocumentVersionId = currentVersion.Id, Sequence = 2, Text = "more text" });
        database.KnowledgeChunks.Add(new KnowledgeChunkEntity
        {
            KnowledgeDocumentVersionId = currentVersion.Id,
            Sequence = 1,
            Text = "approved secret text",
            Status = "approved"
        });
        database.KnowledgeOcrPages.AddRange(
            new KnowledgeOcrPageEntity { KnowledgeDocumentVersionId = currentVersion.Id, PageNumber = 1, Status = "completed", BlocksJson = """[{"text":"ocr secret"}]""" },
            new KnowledgeOcrPageEntity { KnowledgeDocumentVersionId = currentVersion.Id, PageNumber = 2, Status = "failed", Error = "provider secret" });
        database.DurableJobs.Add(new DurableJobEntity
        {
            JobType = "ParseKnowledgeDocument",
            Status = "retrying",
            AttemptCount = 2,
            PayloadJson = JsonSerializer.Serialize(new
            {
                documentId = document.Id,
                versionId = currentVersion.Id,
                authorization = "Bearer must-not-leak",
                objectKey = "secret/object"
            })
        });
        database.KnowledgeIndexJobs.Add(new KnowledgeIndexJobEntity
        {
            KnowledgeDocumentId = document.Id,
            KnowledgeDocumentVersionId = currentVersion.Id,
            Operation = "index",
            Status = "failed",
            FailureReason = "provider response with api-key",
            CollectionName = "internal-secret-collection"
        });
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);

        var detail = await new KnowledgeDocumentAdministrationQuery(database).GetAsync(
            document.Id,
            TestContext.Current.CancellationToken);

        Assert.NotNull(detail);
        Assert.Equal([2, 1], detail.Versions.Select(version => version.Version));
        var current = detail.Versions[0];
        Assert.Equal(2, current.PreviewCount);
        Assert.Equal(1, current.ApprovedChunkCount);
        Assert.Equal(2, current.OcrPageCount);
        Assert.Equal(1, current.OcrFailedPageCount);
        Assert.True(current.HasPublicObject);
        Assert.Single(current.UploadAndParseJobs);
        Assert.True(Assert.Single(current.IndexJobs).HasFailure);
        var json = JsonSerializer.Serialize(detail);
        Assert.DoesNotContain("must-not-leak", json);
        Assert.DoesNotContain("secret/object", json);
        Assert.DoesNotContain("secret document text", json);
        Assert.DoesNotContain("internal-secret-collection", json);
        Assert.DoesNotContain("api-key", json);
    }

    [Fact]
    public async Task Detail_returns_null_for_missing_document()
    {
        await using var database = NewDatabase();

        Assert.Null(await new KnowledgeDocumentAdministrationQuery(database).GetAsync(
            Guid.NewGuid(),
            TestContext.Current.CancellationToken));
    }

    private static KnowledgeDocumentEntity Document(
        string id,
        string title,
        string status,
        DateTime updatedAt) =>
        new()
        {
            Id = Guid.Parse(id),
            Title = title,
            Status = status,
            CreatedAtUtc = updatedAt.AddHours(-1),
            UpdatedAtUtc = updatedAt
        };

    private static KnowledgeDocumentVersionEntity Version(
        Guid documentId,
        int version,
        string status,
        bool staged = false,
        string? failure = null) =>
        new()
        {
            KnowledgeDocumentId = documentId,
            Version = version,
            OriginalFileName = $"v{version}.pdf",
            SafeFileName = "source.pdf",
            ContentType = "application/pdf",
            Sha256 = Guid.NewGuid().ToString("N").PadRight(64, '0'),
            SizeBytes = 100,
            ObjectKey = $"secret/{Guid.NewGuid():N}",
            Status = status,
            FailureReason = failure,
            StagedContent = staged ? [1, 2, 3] : []
        };

    private static WechatRobotDbContext NewDatabase() =>
        new(new DbContextOptionsBuilder<WechatRobotDbContext>()
            .UseInMemoryDatabase($"document-admin-{Guid.NewGuid():N}")
            .Options);
}
