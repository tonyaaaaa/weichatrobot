using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Knowledge;
using WechatRobot.Application.Models;
using WechatRobot.Application.Security;
using WechatRobot.Infrastructure.Knowledge;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.UnitTests.Knowledge;

public sealed class SharedKnowledgeIndexQueueTests
{
    [Fact]
    public async Task Two_documents_with_one_embedding_contract_queue_the_same_nonexclusive_collection()
    {
        await using var database = new WechatRobotDbContext(
            new DbContextOptionsBuilder<WechatRobotDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options);
        var tag = new KnowledgeTagEntity { Name = "签证知识", NormalizedName = "签证知识" };
        var model = new ModelConfigEntity
        {
            Name = "GLM Embedding",
            NormalizedName = "GLM EMBEDDING",
            Provider = "glm",
            ConfigurationType = "embedding",
            BaseUrl = "https://embedding.example.test/v1",
            Model = "embedding-3",
            IsEnabled = true,
            IsDefault = true,
            EmbeddingDimension = 3
        };
        var first = AddApprovedDocument(database);
        var second = AddApprovedDocument(database);
        database.AddRange(tag, model);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        var service = new QdrantKnowledgeService(
            database,
            new ModelConfigurationService(new PassThroughProtector()),
            new KnowledgeIndexOptions(3, VectorDistance.Cosine),
            TimeProvider.System);

        var firstJobId = await service.QueueIndexAsync(
            first.Document.Id,
            first.Version.Id,
            [tag.Id],
            false,
            TestContext.Current.CancellationToken);
        var secondJobId = await service.QueueIndexAsync(
            second.Document.Id,
            second.Version.Id,
            [tag.Id],
            false,
            TestContext.Current.CancellationToken);
        var jobs = await database.KnowledgeIndexJobs.AsNoTracking()
            .Where(x => x.Id == firstJobId || x.Id == secondJobId)
            .OrderBy(x => x.Id)
            .ToArrayAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, jobs.Length);
        Assert.Equal(jobs[0].CollectionName, jobs[1].CollectionName);
        Assert.True(EmbeddingSpaceContract.IsSharedCollectionName(jobs[0].CollectionName));
        Assert.All(jobs, job => Assert.False(job.IsCollectionExclusive));
        Assert.All(jobs, job => Assert.False(string.IsNullOrWhiteSpace(job.EmbeddingContractKey)));
        var queryContract = await service.LoadEmbeddingSpaceContractAsync(
            null,
            null,
            TestContext.Current.CancellationToken);
        Assert.Equal(queryContract.Key, jobs[0].EmbeddingContractKey);
        Assert.Equal(queryContract.CollectionName, jobs[0].CollectionName);

        var failed = await database.KnowledgeIndexJobs.SingleAsync(
            x => x.Id == firstJobId,
            TestContext.Current.CancellationToken);
        failed.Status = "failed";
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        await service.QueueIndexAsync(
            first.Document.Id,
            first.Version.Id,
            [tag.Id],
            true,
            TestContext.Current.CancellationToken);

        Assert.False(await database.KnowledgeIndexJobs.AnyAsync(
            x => x.Operation == "cleanup"
                 && x.KnowledgeDocumentVersionId == first.Version.Id
                 && x.CollectionName == failed.CollectionName,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Physical_collection_delete_rejects_shared_or_active_collections()
    {
        await using var database = new WechatRobotDbContext(
            new DbContextOptionsBuilder<WechatRobotDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options);
        database.KnowledgeDocuments.Add(new KnowledgeDocumentEntity
        {
            Status = "active",
            ActiveVersionId = Guid.NewGuid(),
            ActiveCollectionName = "kb_cosine_3_active"
        });
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        var service = new QdrantKnowledgeService(
            database,
            new ModelConfigurationService(new PassThroughProtector()),
            new KnowledgeIndexOptions(3, VectorDistance.Cosine),
            TimeProvider.System);

        Assert.False(await service.CanPhysicallyDeleteCollectionAsync(
            "kb_shared_0123456789abcdef_cosine_3", true, TestContext.Current.CancellationToken));
        Assert.False(await service.CanPhysicallyDeleteCollectionAsync(
            "kb_cosine_3_active", true, TestContext.Current.CancellationToken));
        Assert.False(await service.CanPhysicallyDeleteCollectionAsync(
            "kb_cosine_3_unreferenced", false, TestContext.Current.CancellationToken));
        Assert.True(await service.CanPhysicallyDeleteCollectionAsync(
            "kb_cosine_3_unreferenced", true, TestContext.Current.CancellationToken));
    }

    private static (KnowledgeDocumentEntity Document, KnowledgeDocumentVersionEntity Version) AddApprovedDocument(
        WechatRobotDbContext database)
    {
        var document = new KnowledgeDocumentEntity { Status = "uploaded" };
        var version = new KnowledgeDocumentVersionEntity
        {
            KnowledgeDocumentId = document.Id,
            Version = 1,
            OriginalFileName = $"{Guid.NewGuid():N}.md",
            SafeFileName = "knowledge.md",
            ContentType = "text/markdown",
            Sha256 = Convert.ToHexString(Guid.NewGuid().ToByteArray()).PadRight(64, '0'),
            ObjectKey = $"knowledge/{Guid.NewGuid():N}",
            Status = "approved"
        };
        database.AddRange(
            document,
            version,
            new KnowledgeChunkEntity
            {
                KnowledgeDocumentVersionId = version.Id,
                Text = "签证知识",
                Status = "approved"
            });
        return (document, version);
    }

    private sealed class PassThroughProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }
}
