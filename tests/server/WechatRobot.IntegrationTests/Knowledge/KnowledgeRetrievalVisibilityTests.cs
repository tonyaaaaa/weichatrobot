using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Knowledge;
using WechatRobot.Application.Models;
using WechatRobot.Application.Security;
using WechatRobot.Infrastructure.Knowledge;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.IntegrationTests.Knowledge;

public sealed class KnowledgeRetrievalVisibilityTests
{
    [Fact]
    public async Task Mysql_recheck_rejects_disabled_or_inactive_hits_even_when_vector_store_returns_them()
    {
        var options = new DbContextOptionsBuilder<WechatRobotDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var database = new WechatRobotDbContext(options);
        var enabledTag = new KnowledgeTagEntity { Name = "产品", NormalizedName = "产品" };
        var disabledTag = new KnowledgeTagEntity { Name = "旧", NormalizedName = "旧", IsEnabled = false };
        var activeDocument = new KnowledgeDocumentEntity { Status = "active", ActiveCollectionName = "kb_cosine_3", ActiveEmbeddingDimension = 3, ActiveDistance = "cosine" };
        var activeVersion = Version(activeDocument.Id, "active", true);
        activeDocument.ActiveVersionId = activeVersion.Id;
        var disabledDocument = new KnowledgeDocumentEntity { Status = "disabled", IsDeleteRequested = true };
        var disabledVersion = Version(disabledDocument.Id, "disabled", false);
        var allowedChunk = Chunk(activeVersion.Id);
        var staleChunk = Chunk(disabledVersion.Id);
        database.AddRange(enabledTag, disabledTag, activeDocument, activeVersion, disabledDocument, disabledVersion, allowedChunk, staleChunk);
        database.KnowledgeChunkTags.AddRange(new KnowledgeChunkTagEntity { KnowledgeChunkId = allowedChunk.Id, KnowledgeTagId = enabledTag.Id },
            new KnowledgeChunkTagEntity { KnowledgeChunkId = staleChunk.Id, KnowledgeTagId = enabledTag.Id });
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        var vectors = new MaliciousVectorStore(allowedChunk, activeDocument, activeVersion, staleChunk, disabledDocument, disabledVersion);
        var service = new QdrantKnowledgeService(database, new ModelConfigurationService(new PassThroughProtector()),
            new KnowledgeIndexOptions(3, VectorDistance.Cosine), TimeProvider.System);

        var hits = await service.SearchVisibleAsync([1, 0, 0], [enabledTag.Id, disabledTag.Id], vectors, 10, TestContext.Current.CancellationToken);

        Assert.Equal(allowedChunk.Id, Assert.Single(hits).ChunkId);
        Assert.Equal([enabledTag.Id], vectors.Request!.AllowedTagIds);
        Assert.Equal([activeVersion.Id], vectors.Request.ActiveVersionIds);
    }

    private static KnowledgeDocumentVersionEntity Version(Guid documentId, string status, bool published) => new()
    {
        KnowledgeDocumentId = documentId, Version = 1, OriginalFileName = Guid.NewGuid() + ".txt", SafeFileName = "file.txt", ContentType = "text/plain",
        Sha256 = Guid.NewGuid().ToString("N").PadLeft(64, '0'), ObjectKey = Guid.NewGuid().ToString("N"), Status = status, IsPublished = published
    };
    private static KnowledgeChunkEntity Chunk(Guid versionId) => new() { KnowledgeDocumentVersionId = versionId, Text = "authoritative", Status = "approved" };

    private sealed class MaliciousVectorStore(KnowledgeChunkEntity allowedChunk, KnowledgeDocumentEntity allowedDocument, KnowledgeDocumentVersionEntity allowedVersion,
        KnowledgeChunkEntity staleChunk, KnowledgeDocumentEntity staleDocument, KnowledgeDocumentVersionEntity staleVersion) : IVectorStore
    {
        public VectorSearchRequest? Request { get; private set; }
        public Task<IReadOnlyList<VectorSearchHit>> SearchAsync(VectorSearchRequest request, CancellationToken token)
        {
            Request = request;
            return Task.FromResult<IReadOnlyList<VectorSearchHit>>([
                new(allowedChunk.Id, allowedDocument.Id, allowedVersion.Id, 1), new(staleChunk.Id, staleDocument.Id, staleVersion.Id, .9)]);
        }
        public Task EnsureCollectionAsync(VectorCollection collection, CancellationToken token) => Task.CompletedTask;
        public Task UpsertAsync(VectorCollection collection, IReadOnlyList<VectorPoint> points, CancellationToken token) => Task.CompletedTask;
        public Task SetVersionActiveAsync(VectorCollection collection, Guid versionId, bool active, CancellationToken token) => Task.CompletedTask;
        public Task DeleteVersionAsync(VectorCollection collection, Guid versionId, CancellationToken token) => Task.CompletedTask;
        public Task<long> CountVersionAsync(VectorCollection collection, Guid versionId, bool activeOnly, CancellationToken token) => Task.FromResult(0L);
    }
    private sealed class PassThroughProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }
}
