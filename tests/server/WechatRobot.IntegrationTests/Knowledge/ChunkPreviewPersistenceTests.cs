using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Knowledge.Chunking;
using WechatRobot.Application.Knowledge.Parsing;
using WechatRobot.Infrastructure.Knowledge;
using WechatRobot.Infrastructure.Knowledge.Parsing;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.IntegrationTests.Knowledge;

public sealed class ChunkPreviewPersistenceTests
{
    [Fact]
    public async Task Draft_mutations_do_not_publish_and_approval_is_atomic_idempotent_and_concurrency_safe()
    {
        var options = new DbContextOptionsBuilder<WechatRobotDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new WechatRobotDbContext(options);
        var documentId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        db.KnowledgeDocuments.Add(new KnowledgeDocumentEntity { Id = documentId, Title = "文档", Status = "uploaded" });
        db.KnowledgeDocumentVersions.Add(new KnowledgeDocumentVersionEntity { Id = versionId, KnowledgeDocumentId = documentId, Version = 1,
            OriginalFileName = "a.txt", SafeFileName = "source.txt", ContentType = "text/plain", Sha256 = new string('a', 64), ObjectKey = "key", PublicUrl = "https://example.test/key", Status = "uploaded" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new ChunkPreviewRepository(db);
        var generated = await repository.ReplaceAsync(versionId,
            [new ChunkPreview(Guid.NewGuid(), 0, "第一段", 1, ["标题"], false, null, null), new ChunkPreview(Guid.NewGuid(), 1, "第二段", 2, [], false, null, null)],
            0, TestContext.Current.CancellationToken);
        Assert.Empty(await db.KnowledgeChunks.ToArrayAsync(TestContext.Current.CancellationToken));

        var edited = await repository.EditAsync(versionId, generated.Items[0].Id, "修改后", generated.Revision, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ChunkPreviewConcurrencyException>(() => repository.DeleteAsync(versionId, generated.Items[1].Id, generated.Revision, TestContext.Current.CancellationToken));
        var approved = await repository.ApproveAsync(versionId, edited.Revision, TestContext.Current.CancellationToken);
        Assert.Equal(2, approved.Count);
        Assert.Equal(["修改后", "第二段"], approved.Select(item => item.Text));
        Assert.Equal([1, 2], approved.Select(item => item.PageNumber));
        Assert.All(approved, item => Assert.Equal("approved", item.Status));
        Assert.Equal(approved.Select(item => item.Id), (await repository.ApproveAsync(versionId, edited.Revision, TestContext.Current.CancellationToken)).Select(item => item.Id));
    }

    [Fact]
    public async Task Only_successfully_uploaded_versions_accept_previews()
    {
        var options = new DbContextOptionsBuilder<WechatRobotDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new WechatRobotDbContext(options);
        var documentId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        db.KnowledgeDocuments.Add(new KnowledgeDocumentEntity { Id = documentId, Title = "失败文档", Status = "failed" });
        db.KnowledgeDocumentVersions.Add(new KnowledgeDocumentVersionEntity { Id = versionId, KnowledgeDocumentId = documentId, Version = 1,
            OriginalFileName = "a.txt", SafeFileName = "source.txt", ContentType = "text/plain", Sha256 = new string('b', 64), ObjectKey = "key", Status = "failed" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ChunkPreviewStateException>(() => new ChunkPreviewRepository(db).ReplaceAsync(versionId,
            [new ChunkPreview(Guid.NewGuid(), 0, "内容", null, [], false, null, null)], 0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Parse_job_contract_generates_the_same_ordered_preview_twice()
    {
        var options = new DbContextOptionsBuilder<WechatRobotDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new WechatRobotDbContext(options);
        var documentId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        db.KnowledgeDocuments.Add(new KnowledgeDocumentEntity { Id = documentId, Title = "文档", Status = "uploaded" });
        db.KnowledgeDocumentVersions.Add(new KnowledgeDocumentVersionEntity { Id = versionId, KnowledgeDocumentId = documentId, Version = 1,
            OriginalFileName = "a.md", SafeFileName = "source.md", ContentType = "text/markdown", Sha256 = new string('c', 64), ObjectKey = "key", PublicUrl = "https://example.test/key", Status = "uploaded" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new ChunkPreviewRepository(db);
        var service = new KnowledgePreviewService(db, new MemorySourceReader("# 标题\n第一段\n\n第二段"u8.ToArray()),
            new DocumentParserSelector([new MarkdownTextParser(), new DocxParser(), new PdfTextParser()]), new ChunkingService(), repository,
            new DocumentParsingOptions { MaximumSourceBytes = 1024, MaximumMemoryBytes = 4096, MaximumPages = 10, ExecutionTimeoutSeconds = 5 }, TimeProvider.System);
        var payload = System.Text.Json.JsonSerializer.Serialize(new { documentId, versionId });
        Assert.True(await service.GenerateFromJobAsync(payload, TestContext.Current.CancellationToken));
        var first = (await repository.GetAsync(versionId, TestContext.Current.CancellationToken)).Items.Select(Signature).ToArray();
        Assert.True(await service.GenerateFromJobAsync(payload, TestContext.Current.CancellationToken));
        var second = (await repository.GetAsync(versionId, TestContext.Current.CancellationToken)).Items.Select(Signature).ToArray();
        Assert.Equal(first, second);
    }

    private static string Signature(ChunkPreview item) => $"{item.Sequence}|{item.Text}|{item.PageNumber}|{string.Join('/', item.Headings)}";

    private sealed class MemorySourceReader(byte[] content) : IDocumentSourceReader
    {
        public Task<Stream> OpenReadAsync(Uri publicUrl, DocumentProcessingContext context)
        {
            context.ReserveSource(content.Length);
            return Task.FromResult<Stream>(new MemoryStream(content, 0, content.Length, writable: false, publiclyVisible: true));
        }
    }
}
