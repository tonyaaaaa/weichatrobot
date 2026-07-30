using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Knowledge;
using WechatRobot.Infrastructure.Knowledge;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.IntegrationTests.Knowledge;

public sealed class KnowledgeDocumentRevisionServiceTests
{
    [Fact]
    public async Task Create_revision_copies_approved_chunks_without_switching_active_version()
    {
        await using var database = NewDatabase();
        var (document, source) = SeedActiveDocument(database);
        var originalStateVersion = document.StateVersion;
        var chunk = new KnowledgeChunkEntity
        {
            KnowledgeDocumentVersionId = source.Id,
            Sequence = 3,
            PageNumber = 2,
            Text = "问题：签证多久出？\n答案：出签后会通知。",
            HeadingsJson = """["签证进度"]""",
            IsTable = true,
            TableRows = 2,
            TableColumns = 3,
            Question = "签证多久出？",
            SynonymsJson = """["签证出了吗"]""",
            Answer = "出签后会通知。",
            Status = "approved"
        };
        database.KnowledgeChunks.Add(chunk);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await new KnowledgeDocumentRevisionService(
            database,
            TimeProvider.System).CreateAsync(
            new(
                document.Id,
                source.Id,
                document.StateVersion,
                "admin-user-id",
                "系统管理员"),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Version);
        Assert.Equal(1, result.PreviewRevision);
        var reloadedDocument = await database.KnowledgeDocuments.AsNoTracking()
            .SingleAsync(x => x.Id == document.Id, TestContext.Current.CancellationToken);
        Assert.Equal(source.Id, reloadedDocument.ActiveVersionId);
        Assert.Equal(originalStateVersion + 1, reloadedDocument.StateVersion);
        var revision = await database.KnowledgeDocumentVersions.AsNoTracking()
            .SingleAsync(x => x.Id == result.VersionId, TestContext.Current.CancellationToken);
        Assert.Equal("preview", revision.Status);
        Assert.Equal("AdministrationRevision", revision.SourceKind);
        Assert.Equal("Correction", revision.ChangeKind);
        Assert.Equal(source.Id, revision.SupersedesVersionId);
        Assert.Equal("系统管理员", revision.SourceActorDisplayName);
        Assert.False(revision.IsPublished);
        var preview = await database.KnowledgeChunkPreviews.AsNoTracking()
            .SingleAsync(x => x.KnowledgeDocumentVersionId == revision.Id, TestContext.Current.CancellationToken);
        Assert.Equal(chunk.Sequence, preview.Sequence);
        Assert.Equal(chunk.PageNumber, preview.PageNumber);
        Assert.Equal(chunk.Text, preview.Text);
        Assert.Equal(chunk.HeadingsJson, preview.HeadingsJson);
        Assert.Equal(chunk.IsTable, preview.IsTable);
        Assert.Equal(chunk.TableRows, preview.TableRows);
        Assert.Equal(chunk.TableColumns, preview.TableColumns);
        Assert.Equal(chunk.Question, preview.Question);
        Assert.Equal(chunk.SynonymsJson, preview.SynonymsJson);
        Assert.Equal(chunk.Answer, preview.Answer);
        var audit = await database.AdministrationAudits.AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("knowledge.document.revision.create", audit.Action);
        Assert.Equal("admin-user-id", audit.Actor);
        Assert.DoesNotContain(chunk.Text, audit.SanitizedDetailJson);
    }

    [Fact]
    public async Task Create_revision_rejects_an_existing_mutable_revision()
    {
        await using var database = NewDatabase();
        var (document, source) = SeedActiveDocument(database);
        database.KnowledgeChunks.Add(ApprovedChunk(source.Id));
        var existing = new KnowledgeDocumentVersionEntity
        {
            KnowledgeDocumentId = document.Id,
            Version = 2,
            OriginalFileName = "revision-2.txt",
            SafeFileName = "revision-2.txt",
            ContentType = "text/plain",
            Sha256 = new string('b', 64),
            ObjectKey = "administration-revision/2",
            Status = "preview",
            PreviewRevision = 4,
            SourceKind = "AdministrationRevision",
            ChangeKind = "Correction",
            SupersedesVersionId = source.Id
        };
        database.KnowledgeDocumentVersions.Add(existing);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<KnowledgeRevisionConflictException>(() =>
            new KnowledgeDocumentRevisionService(database, TimeProvider.System).CreateAsync(
                new(
                    document.Id,
                    source.Id,
                    document.StateVersion,
                    "admin",
                    "管理员"),
                TestContext.Current.CancellationToken));

        Assert.Equal("revision-already-editable", exception.Error);
        Assert.Equal(existing.Id, exception.ExistingRevision?.VersionId);
        Assert.Equal(4, exception.ExistingRevision?.PreviewRevision);
    }

    [Theory]
    [InlineData("disabled", false, "document-disabled")]
    [InlineData("active", true, "document-delete-requested")]
    public async Task Create_revision_rejects_unwritable_documents(
        string status,
        bool deleteRequested,
        string expectedError)
    {
        await using var database = NewDatabase();
        var (document, source) = SeedActiveDocument(database);
        document.Status = status;
        document.IsDeleteRequested = deleteRequested;
        database.KnowledgeChunks.Add(ApprovedChunk(source.Id));
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<KnowledgeRevisionStateException>(() =>
            new KnowledgeDocumentRevisionService(database, TimeProvider.System).CreateAsync(
                new(
                    document.Id,
                    source.Id,
                    document.StateVersion,
                    "admin",
                    "管理员"),
                TestContext.Current.CancellationToken));

        Assert.Equal(expectedError, exception.Error);
    }

    [Fact]
    public async Task Create_revision_rejects_document_state_version_conflict()
    {
        await using var database = NewDatabase();
        var (document, source) = SeedActiveDocument(database);
        database.KnowledgeChunks.Add(ApprovedChunk(source.Id));
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<DocumentConcurrencyException>(() =>
            new KnowledgeDocumentRevisionService(database, TimeProvider.System).CreateAsync(
                new(
                    document.Id,
                    source.Id,
                    document.StateVersion - 1,
                    "admin",
                    "管理员"),
                TestContext.Current.CancellationToken));

        Assert.Equal(document.StateVersion, exception.Current.StateVersion);
    }

    private static (KnowledgeDocumentEntity Document, KnowledgeDocumentVersionEntity Version)
        SeedActiveDocument(WechatRobotDbContext database)
    {
        var document = new KnowledgeDocumentEntity
        {
            Title = "签证进度",
            Status = "active",
            StateVersion = 7,
            CreatedAtUtc = UtcNow,
            UpdatedAtUtc = UtcNow
        };
        var version = new KnowledgeDocumentVersionEntity
        {
            KnowledgeDocumentId = document.Id,
            Version = 1,
            OriginalFileName = "private-chat.txt",
            SafeFileName = "private-chat.txt",
            ContentType = "text/plain",
            Sha256 = new string('a', 64),
            SizeBytes = 64,
            ObjectKey = "private-chat/source",
            Status = "active",
            IsPublished = true,
            SourceKind = "PrivateChatDirect",
            ChangeKind = "New",
            CreatedAtUtc = UtcNow,
            UpdatedAtUtc = UtcNow
        };
        document.ActiveVersionId = version.Id;
        database.AddRange(document, version);
        return (document, version);
    }

    private static KnowledgeChunkEntity ApprovedChunk(Guid versionId) =>
        new()
        {
            KnowledgeDocumentVersionId = versionId,
            Sequence = 0,
            Text = "批准内容",
            Status = "approved",
            CreatedAtUtc = UtcNow,
            UpdatedAtUtc = UtcNow
        };

    private static WechatRobotDbContext NewDatabase() =>
        new(new DbContextOptionsBuilder<WechatRobotDbContext>()
            .UseInMemoryDatabase($"knowledge-revision-{Guid.NewGuid():N}")
            .Options);

    private static readonly DateTime UtcNow =
        new(2026, 7, 30, 9, 0, 0, DateTimeKind.Utc);
}
