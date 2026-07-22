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

    [Fact]
    public async Task Runtime_contract_change_keeps_old_active_collection_visible_and_requires_explicit_reindex()
    {
        var dbOptions = new DbContextOptionsBuilder<WechatRobotDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var database = new WechatRobotDbContext(dbOptions);
        var tag = new KnowledgeTagEntity { Name = "公开", NormalizedName = "公开", IsGlobalPublic = true };
        var document = new KnowledgeDocumentEntity { Status = "active", ActiveCollectionName = "kb_cosine_3_g1_old", ActiveEmbeddingDimension = 3, ActiveDistance = "cosine", ActiveIndexGeneration = 1 };
        var version = Version(document.Id, "active", true);
        version.IndexCollectionName = document.ActiveCollectionName; version.EmbeddingDimension = 3; version.VectorDistance = "cosine"; version.IndexGeneration = 1;
        document.ActiveVersionId = version.Id;
        var chunk = Chunk(version.Id);
        database.AddRange(tag, document, version, chunk, new KnowledgeChunkTagEntity { KnowledgeChunkId = chunk.Id, KnowledgeTagId = tag.Id });
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        var vectors = new RecordingVectorStore(new VectorSearchHit(chunk.Id, document.Id, version.Id, 1));
        var changedRuntime = new QdrantKnowledgeService(database, new ModelConfigurationService(new PassThroughProtector()),
            new KnowledgeIndexOptions(4, VectorDistance.Dot), TimeProvider.System);

        var stillVisible = await changedRuntime.SearchVisibleAsync([1, 0, 0], [], vectors, 5, TestContext.Current.CancellationToken);
        Assert.Equal(chunk.Id, Assert.Single(stillVisible).ChunkId);
        Assert.Equal("kb_cosine_3_g1_old", vectors.Request!.Collection.Name);
        await Assert.ThrowsAsync<InvalidOperationException>(() => changedRuntime.QueueIndexAsync(document.Id, version.Id, [tag.Id], false, TestContext.Current.CancellationToken));

        var jobId = await changedRuntime.QueueIndexAsync(document.Id, version.Id, [tag.Id], true, TestContext.Current.CancellationToken);
        var job = await database.KnowledgeIndexJobs.SingleAsync(item => item.Id == jobId, TestContext.Current.CancellationToken);
        Assert.StartsWith("kb_dot_4_g", job.CollectionName, StringComparison.Ordinal);
        Assert.Equal("kb_cosine_3_g1_old", document.ActiveCollectionName);
        job.Status = "failed";
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(chunk.Id, Assert.Single(await changedRuntime.SearchVisibleAsync([1, 0, 0], [], vectors, 5, TestContext.Current.CancellationToken)).ChunkId);
        Assert.Equal("kb_cosine_3_g1_old", vectors.Request!.Collection.Name);
    }

    [Fact]
    public async Task Consistency_check_reports_payload_drift_even_when_point_count_matches()
    {
        var dbOptions = new DbContextOptionsBuilder<WechatRobotDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var database = new WechatRobotDbContext(dbOptions);
        var expectedTag = new KnowledgeTagEntity { Name = "售后", NormalizedName = "售后" };
        var wrongTag = Guid.NewGuid();
        var document = new KnowledgeDocumentEntity { Status = "active", ActiveCollectionName = "kb_cosine_3_g2", ActiveEmbeddingDimension = 3, ActiveDistance = "cosine", ActiveIndexGeneration = 2 };
        var version = Version(document.Id, "active", true);
        document.ActiveVersionId = version.Id;
        var chunk = Chunk(version.Id);
        database.AddRange(expectedTag, document, version, chunk, new KnowledgeChunkTagEntity { KnowledgeChunkId = chunk.Id, KnowledgeTagId = expectedTag.Id });
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        var vectors = new RecordingVectorStore(new VectorSearchHit(chunk.Id, document.Id, version.Id, 1),
            [new VectorPointMetadata(chunk.Id, document.Id, version.Id, [wrongTag], true, 2)]);
        var service = new QdrantKnowledgeService(database, new ModelConfigurationService(new PassThroughProtector()),
            new KnowledgeIndexOptions(3, VectorDistance.Cosine), TimeProvider.System);

        var status = await service.GetStatusAsync(document.Id, vectors, true, TestContext.Current.CancellationToken);

        Assert.Equal(1, status.ApprovedChunkCount);
        Assert.Equal(1, status.ActivePointCount);
        Assert.Equal("drift", status.Consistency);
        Assert.Contains(status.DriftDetails, detail => detail == $"payload:{chunk.Id:D}");
    }

    [Fact]
    public async Task Consistency_check_reports_drift_when_active_generation_is_missing()
    {
        var dbOptions = new DbContextOptionsBuilder<WechatRobotDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var database = new WechatRobotDbContext(dbOptions);
        var tag = new KnowledgeTagEntity { Name = "产品", NormalizedName = "产品" };
        var document = new KnowledgeDocumentEntity
        {
            Status = "active", ActiveCollectionName = "kb_cosine_3_g2", ActiveEmbeddingDimension = 3,
            ActiveDistance = "cosine", ActiveIndexGeneration = null
        };
        var version = Version(document.Id, "active", true);
        document.ActiveVersionId = version.Id;
        var chunk = Chunk(version.Id);
        database.AddRange(tag, document, version, chunk,
            new KnowledgeChunkTagEntity { KnowledgeChunkId = chunk.Id, KnowledgeTagId = tag.Id });
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        var vectors = new RecordingVectorStore(new VectorSearchHit(chunk.Id, document.Id, version.Id, 1),
            [new VectorPointMetadata(chunk.Id, document.Id, version.Id, [tag.Id], true, 2)]);
        var service = new QdrantKnowledgeService(database, new ModelConfigurationService(new PassThroughProtector()),
            new KnowledgeIndexOptions(3, VectorDistance.Cosine), TimeProvider.System);

        var status = await service.GetStatusAsync(document.Id, vectors, true, TestContext.Current.CancellationToken);

        Assert.Equal("drift", status.Consistency);
        Assert.Contains(status.DriftDetails, detail => detail == "missing-active-generation");
    }

    [Fact]
    public async Task Retry_cannot_restart_failed_index_after_physical_delete()
    {
        var dbOptions = new DbContextOptionsBuilder<WechatRobotDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var database = new WechatRobotDbContext(dbOptions);
        var document = new KnowledgeDocumentEntity { Status = "disabled", IsDeleteRequested = true };
        var version = Version(document.Id, "disabled", false);
        var job = new KnowledgeIndexJobEntity
        {
            KnowledgeDocumentId = document.Id, KnowledgeDocumentVersionId = version.Id, Operation = "index", Status = "failed",
            CollectionName = "kb_cosine_3_failed", Dimension = 3, Distance = "cosine"
        };
        database.AddRange(document, version, job);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        var service = new QdrantKnowledgeService(database, new ModelConfigurationService(new PassThroughProtector()),
            new KnowledgeIndexOptions(3, VectorDistance.Cosine), TimeProvider.System);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RetryAsync(job.Id, TestContext.Current.CancellationToken));

        Assert.Equal("failed", job.Status);
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
        public Task<IReadOnlyList<VectorPointMetadata>> InspectVersionAsync(VectorCollection collection, Guid versionId, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<VectorPointMetadata>>([]);
    }
    private sealed class RecordingVectorStore(VectorSearchHit hit, IReadOnlyList<VectorPointMetadata>? metadata = null) : IVectorStore
    {
        public VectorSearchRequest? Request { get; private set; }
        public Task<IReadOnlyList<VectorSearchHit>> SearchAsync(VectorSearchRequest request, CancellationToken token) { Request = request; return Task.FromResult<IReadOnlyList<VectorSearchHit>>([hit]); }
        public Task EnsureCollectionAsync(VectorCollection collection, CancellationToken token) => Task.CompletedTask;
        public Task UpsertAsync(VectorCollection collection, IReadOnlyList<VectorPoint> points, CancellationToken token) => Task.CompletedTask;
        public Task SetVersionActiveAsync(VectorCollection collection, Guid versionId, bool active, CancellationToken token) => Task.CompletedTask;
        public Task DeleteVersionAsync(VectorCollection collection, Guid versionId, CancellationToken token) => Task.CompletedTask;
        public Task<IReadOnlyList<VectorPointMetadata>> InspectVersionAsync(VectorCollection collection, Guid versionId, CancellationToken token) =>
            Task.FromResult(metadata ?? (IReadOnlyList<VectorPointMetadata>)[]);
    }
    private sealed class PassThroughProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }
}
