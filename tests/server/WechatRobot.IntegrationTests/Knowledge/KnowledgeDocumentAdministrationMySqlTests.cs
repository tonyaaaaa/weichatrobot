using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Knowledge;
using WechatRobot.Infrastructure.Knowledge;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.IntegrationTests.Infrastructure;

namespace WechatRobot.IntegrationTests.Knowledge;

public sealed class KnowledgeDocumentAdministrationMySqlTests(MySqlFixture fixture) : IClassFixture<MySqlFixture>
{
    [Fact]
    public async Task Physical_delete_state_queries_translate_on_mysql()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var pendingDocument = new KnowledgeDocumentEntity
        {
            Title = $"Pending Delete {suffix}",
            Status = "disabled",
            IsDeleteRequested = true
        };
        var failedDocument = new KnowledgeDocumentEntity
        {
            Title = $"Failed Delete {suffix}",
            Status = "disabled",
            IsDeleteRequested = true
        };
        var normalDocument = new KnowledgeDocumentEntity
        {
            Title = $"Normal Document {suffix}",
            Status = "uploaded"
        };
        var pendingVersion = Version(
            pendingDocument.Id,
            1,
            "pending-delete.txt",
            "disabled");
        var failedVersion = Version(
            failedDocument.Id,
            1,
            "failed-delete.txt",
            "disabled");
        var normalVersion = Version(
            normalDocument.Id,
            1,
            "normal.txt",
            "uploaded");
        pendingDocument.ActiveVersionId = null;
        failedDocument.ActiveVersionId = null;
        normalDocument.ActiveVersionId = normalVersion.Id;
        await using (var setup = CreateDatabase())
        {
            await setup.Database.MigrateAsync(
                TestContext.Current.CancellationToken);
            setup.AddRange(
                pendingDocument,
                failedDocument,
                normalDocument,
                pendingVersion,
                failedVersion,
                normalVersion,
                CleanupJob(pendingDocument.Id, "pending"),
                CleanupJob(failedDocument.Id, "deadLetter"));
            await setup.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var database = CreateDatabase();
        var page = await new KnowledgeDocumentAdministrationQuery(database)
            .ListAsync(
                suffix,
                null,
                null,
                null,
                1,
                20,
                TestContext.Current.CancellationToken);
        var workbench = await new KnowledgeDocumentWorkbenchQuery(database)
            .GetAsync(
                failedDocument.Id,
                failedVersion.Id,
                TestContext.Current.CancellationToken);

        Assert.Equal(3, page.Items.Count);
        Assert.False(page.Items.Single(item => item.Id == pendingDocument.Id).CanRetryPhysicalDelete);
        Assert.True(page.Items.Single(item => item.Id == failedDocument.Id).CanRetryPhysicalDelete);
        Assert.False(page.Items.Single(item => item.Id == normalDocument.Id).IsDeleteRequested);
        Assert.True(workbench!.DocumentIsDeleteRequested);
        Assert.True(workbench.CanRetryPhysicalDelete);
    }

    private static KnowledgeDocumentVersionEntity Version(
        Guid documentId,
        int version,
        string fileName,
        string status) => new()
    {
        KnowledgeDocumentId = documentId,
        Version = version,
        OriginalFileName = fileName,
        SafeFileName = fileName,
        ContentType = "text/plain",
        Sha256 = Guid.NewGuid().ToString("N").PadRight(64, '0'),
        ObjectKey = $"test/physical-delete-list/{Guid.NewGuid():N}/{fileName}",
        Status = status
    };

    private static DurableJobEntity CleanupJob(Guid documentId, string status) => new()
    {
        Id = KnowledgeDocumentCleanupJobIdentity.Create(documentId),
        JobType = "CleanupKnowledgeDocument",
        Status = status,
        PayloadJson = "{}"
    };

    [Fact]
    public async Task List_and_detail_queries_translate_on_mysql()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var document = new KnowledgeDocumentEntity
        {
            Title = $"Query Translation {suffix}",
            Status = "uploaded"
        };
        var version = new KnowledgeDocumentVersionEntity
        {
            KnowledgeDocumentId = document.Id,
            Version = 1,
            OriginalFileName = "source.txt",
            SafeFileName = "source.txt",
            ContentType = "text/plain",
            Sha256 = Guid.NewGuid().ToString("N").PadRight(64, '0'),
            ObjectKey = $"test/{suffix}",
            Status = "uploaded",
            SourceKind = "PrivateChatDirect",
            SourceActorDisplayName = "MySQL 测试成员"
        };
        document.ActiveVersionId = version.Id;
        var tag = new KnowledgeTagEntity
        {
            Name = $"签证 {suffix}",
            NormalizedName = $"签证 {suffix}".ToUpperInvariant()
        };
        var chunk = new KnowledgeChunkEntity
        {
            KnowledgeDocumentVersionId = version.Id,
            Sequence = 1,
            Text = "MySQL tag translation",
            Status = "approved"
        };
        await using (var setup = CreateDatabase())
        {
            await setup.Database.MigrateAsync(TestContext.Current.CancellationToken);
            setup.KnowledgeDocuments.Add(document);
            setup.KnowledgeDocumentVersions.Add(version);
            setup.KnowledgeTags.Add(tag);
            setup.KnowledgeChunks.Add(chunk);
            setup.KnowledgeChunkTags.Add(new KnowledgeChunkTagEntity
            {
                KnowledgeChunkId = chunk.Id,
                KnowledgeTagId = tag.Id
            });
            await setup.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var database = CreateDatabase();
        var query = new KnowledgeDocumentAdministrationQuery(database);
        var page = await query.ListAsync(
            suffix,
            "uploaded",
            "PrivateChatDirect",
            tag.Id,
            1,
            20,
            TestContext.Current.CancellationToken);
        var detail = await query.GetAsync(
            document.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(document.Id, Assert.Single(page.Items).Id);
        Assert.Equal("PrivateChatDirect", Assert.Single(page.Items).SourceKind);
        Assert.Equal(tag.Id, Assert.Single(Assert.Single(page.Items).Tags).Id);
        Assert.Equal(version.Id, Assert.Single(detail!.Versions).Id);
    }

    [Fact]
    public async Task Workbench_query_and_revision_transaction_execute_on_mysql()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var document = new KnowledgeDocumentEntity
        {
            Title = $"Workbench MySQL {suffix}",
            Status = "active",
            StateVersion = 3
        };
        var version = new KnowledgeDocumentVersionEntity
        {
            KnowledgeDocumentId = document.Id,
            Version = 1,
            OriginalFileName = "private-chat.txt",
            SafeFileName = "private-chat.txt",
            ContentType = "text/plain",
            Sha256 = suffix.PadRight(64, '0'),
            ObjectKey = $"test/workbench/{suffix}",
            Status = "active",
            IsPublished = true,
            SourceKind = "PrivateChatDirect",
            ChangeKind = "New"
        };
        document.ActiveVersionId = version.Id;
        var chunk = new KnowledgeChunkEntity
        {
            KnowledgeDocumentVersionId = version.Id,
            Sequence = 0,
            Text = "MySQL 已批准内容",
            Status = "approved"
        };

        await using (var setup = CreateDatabase())
        {
            await setup.Database.MigrateAsync(TestContext.Current.CancellationToken);
            setup.AddRange(document, version, chunk);
            await setup.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Guid revisionId;
        await using (var database = CreateDatabase())
        {
            var workbench = await new KnowledgeDocumentWorkbenchQuery(database)
                .GetAsync(
                    document.Id,
                    version.Id,
                    TestContext.Current.CancellationToken);
            Assert.Equal("MySQL 已批准内容", Assert.Single(workbench!.Chunks).Text);

            var revision = await new KnowledgeDocumentRevisionService(
                database,
                TimeProvider.System).CreateAsync(
                new(
                    document.Id,
                    version.Id,
                    document.StateVersion,
                    "mysql-test",
                    "MySQL 测试管理员"),
                TestContext.Current.CancellationToken);
            revisionId = revision.VersionId;
        }

        await using var verify = CreateDatabase();
        Assert.Equal(
            "preview",
            (await verify.KnowledgeDocumentVersions.AsNoTracking()
                .SingleAsync(
                    item => item.Id == revisionId,
                    TestContext.Current.CancellationToken)).Status);
        Assert.Single(await verify.KnowledgeChunkPreviews.AsNoTracking()
            .Where(item => item.KnowledgeDocumentVersionId == revisionId)
            .ToListAsync(TestContext.Current.CancellationToken));
    }

    private WechatRobotDbContext CreateDatabase() =>
        new(new DbContextOptionsBuilder<WechatRobotDbContext>()
            .UseMySQL(fixture.ConnectionString)
            .Options);
}
