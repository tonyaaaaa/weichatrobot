using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Knowledge;
using WechatRobot.Application.Models;
using WechatRobot.Application.Security;
using WechatRobot.Infrastructure.Knowledge;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.UnitTests.Knowledge;

public sealed class KnowledgeSearchFanoutTests
{
    [Fact]
    public async Task Eligible_collections_each_receive_exactly_one_call_within_limit()
    {
        await using var database = Database();
        var tag = Tag("产品");
        database.Add(tag);
        for (var index = 0; index < 4; index++) AddActiveDocument(database, tag.Id, $"kb_cosine_3_{index}");
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        var vectors = new EmptyRecordingVectorStore();
        var service = Service(database, new KnowledgeIndexOptions(3, VectorDistance.Cosine, MaximumCollectionsPerSearch: 4));

        var hits = await service.SearchVisibleAsync([1, 0, 0], [tag.Id], vectors, 10, TestContext.Current.CancellationToken);

        Assert.Empty(hits);
        Assert.Equal(4, vectors.CallCount);
        Assert.Equal(["kb_cosine_3_0", "kb_cosine_3_1", "kb_cosine_3_2", "kb_cosine_3_3"],
            vectors.Requests.Select(request => request.Collection.Name).Order().ToArray());
    }

    [Fact]
    public async Task Unavailable_collection_does_not_discard_hits_from_successful_collection()
    {
        await using var database = Database();
        var tag = Tag("签证");
        database.Add(tag);
        var expected = AddActiveDocument(database, tag.Id, "kb_cosine_3_available");
        AddActiveDocument(database, tag.Id, "kb_cosine_3_unavailable");
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        var vectors = new SelectiveVectorStore(
            "kb_cosine_3_unavailable",
            new Dictionary<string, IReadOnlyList<VectorSearchHit>>
            {
                ["kb_cosine_3_available"] = [expected]
            });
        var service = Service(
            database,
            new KnowledgeIndexOptions(
                3,
                VectorDistance.Cosine,
                MaximumCollectionsPerSearch: 2));

        var hits = await service.SearchVisibleAsync(
            [1, 0, 0],
            [tag.Id],
            vectors,
            10,
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, Assert.Single(hits));
        Assert.Equal(2, vectors.CallCount);
    }

    [Fact]
    public async Task All_unavailable_collections_still_fail_the_search()
    {
        await using var database = Database();
        var tag = Tag("签证");
        database.Add(tag);
        AddActiveDocument(database, tag.Id, "kb_cosine_3_a");
        AddActiveDocument(database, tag.Id, "kb_cosine_3_b");
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        var vectors = new SelectiveVectorStore(
            "kb_cosine_3_a",
            new Dictionary<string, IReadOnlyList<VectorSearchHit>>(),
            failAll: true);
        var service = Service(
            database,
            new KnowledgeIndexOptions(
                3,
                VectorDistance.Cosine,
                MaximumCollectionsPerSearch: 2));

        await Assert.ThrowsAsync<VectorStoreUnavailableException>(() =>
            service.SearchVisibleAsync(
                [1, 0, 0],
                [tag.Id],
                vectors,
                10,
                TestContext.Current.CancellationToken));

        Assert.Equal(2, vectors.CallCount);
    }

    [Fact]
    public async Task Capacity_overflow_fails_explicitly_before_any_vector_call()
    {
        await using var database = Database();
        var tag = Tag("售后");
        database.Add(tag);
        for (var index = 0; index < 3; index++) AddActiveDocument(database, tag.Id, $"kb_cosine_3_{index}");
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        var vectors = new EmptyRecordingVectorStore();
        var service = Service(database, new KnowledgeIndexOptions(3, VectorDistance.Cosine, MaximumCollectionsPerSearch: 2));

        var exception = await Assert.ThrowsAsync<KnowledgeSearchCapacityException>(() =>
            service.SearchVisibleAsync([1, 0, 0], [tag.Id], vectors, 10, TestContext.Current.CancellationToken));

        Assert.Equal(3, exception.EligibleCollectionCount);
        Assert.Equal(2, exception.MaximumCollections);
        Assert.Equal(0, vectors.CallCount);
    }

    [Fact]
    public async Task Three_hundred_twenty_two_documents_in_one_contract_use_one_vector_search()
    {
        await using var database = Database();
        var tag = Tag("签证知识");
        var contract = EmbeddingSpaceContract.Create(
            "glm",
            "https://embedding.example.test/v1",
            "embedding-3",
            3,
            VectorDistance.Cosine);
        database.Add(tag);
        for (var index = 0; index < 322; index++)
            AddActiveDocument(database, tag.Id, contract.CollectionName, contract.Key);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        var vectors = new EmptyRecordingVectorStore();
        var service = Service(database, new KnowledgeIndexOptions(3, VectorDistance.Cosine));

        await service.SearchVisibleAsync(
            [1, 0, 0],
            [tag.Id],
            contract,
            vectors,
            8,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, vectors.CallCount);
        Assert.Equal(322, Assert.Single(vectors.Requests).ActiveVersionIds.Count);
    }

    [Fact]
    public async Task Incompatible_embedding_contract_fails_before_vector_search()
    {
        await using var database = Database();
        var tag = Tag("签证知识");
        var indexedContract = EmbeddingSpaceContract.Create(
            "glm", "https://embedding.example.test/v1", "embedding-3", 3, VectorDistance.Cosine);
        var queryContract = EmbeddingSpaceContract.Create(
            "glm", "https://embedding.example.test/v1", "embedding-4", 3, VectorDistance.Cosine);
        database.Add(tag);
        AddActiveDocument(database, tag.Id, indexedContract.CollectionName, indexedContract.Key);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        var vectors = new EmptyRecordingVectorStore();
        var service = Service(database, new KnowledgeIndexOptions(3, VectorDistance.Cosine));

        await Assert.ThrowsAsync<VectorCollectionConfigurationException>(() => service.SearchVisibleAsync(
            [1, 0, 0],
            [tag.Id],
            queryContract,
            vectors,
            8,
            TestContext.Current.CancellationToken));

        Assert.Equal(0, vectors.CallCount);
    }

    [Fact]
    public async Task Unrelated_active_collections_are_filtered_before_fanout()
    {
        await using var database = Database();
        var allowed = Tag("产品");
        var unrelated = Tag("财务");
        database.AddRange(allowed, unrelated);
        AddActiveDocument(database, allowed.Id, "kb_cosine_3_allowed");
        AddActiveDocument(database, unrelated.Id, "kb_cosine_3_unrelated");
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        var vectors = new EmptyRecordingVectorStore();
        var service = Service(database, new KnowledgeIndexOptions(3, VectorDistance.Cosine));

        await service.SearchVisibleAsync([1, 0, 0], [allowed.Id], vectors, 10, TestContext.Current.CancellationToken);

        Assert.Equal(1, vectors.CallCount);
        Assert.Equal("kb_cosine_3_allowed", Assert.Single(vectors.Requests).Collection.Name);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(257)]
    public void Search_collection_limit_must_be_between_one_and_256(int value)
    {
        var options = new KnowledgeIndexOptions(3, VectorDistance.Cosine, MaximumCollectionsPerSearch: value);
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Search_collection_limit_defaults_to_64_and_accepts_256()
    {
        var defaults = new KnowledgeIndexOptions(3, VectorDistance.Cosine);
        Assert.Equal(64, defaults.MaximumCollectionsPerSearch);
        defaults.Validate();
        new KnowledgeIndexOptions(3, VectorDistance.Cosine, MaximumCollectionsPerSearch: 256).Validate();
    }

    private static WechatRobotDbContext Database() => new(new DbContextOptionsBuilder<WechatRobotDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static KnowledgeTagEntity Tag(string name) => new() { Name = name, NormalizedName = name };

    private static VectorSearchHit AddActiveDocument(
        WechatRobotDbContext database,
        Guid tagId,
        string collection,
        string? embeddingContractKey = null)
    {
        var document = new KnowledgeDocumentEntity
        {
            Status = "active", ActiveCollectionName = collection, ActiveEmbeddingContractKey = embeddingContractKey,
            ActiveEmbeddingDimension = 3, ActiveDistance = "cosine", ActiveIndexGeneration = 1
        };
        var version = new KnowledgeDocumentVersionEntity
        {
            KnowledgeDocumentId = document.Id, Version = 1, OriginalFileName = Guid.NewGuid() + ".txt", SafeFileName = "file.txt",
            ContentType = "text/plain", Sha256 = Guid.NewGuid().ToString("N").PadLeft(64, '0'), ObjectKey = Guid.NewGuid().ToString("N"),
            Status = "active", IsPublished = true, IndexEmbeddingContractKey = embeddingContractKey
        };
        document.ActiveVersionId = version.Id;
        var chunk = new KnowledgeChunkEntity { KnowledgeDocumentVersionId = version.Id, Text = "text", Status = "approved" };
        database.AddRange(document, version, chunk, new KnowledgeChunkTagEntity { KnowledgeChunkId = chunk.Id, KnowledgeTagId = tagId });
        return new VectorSearchHit(chunk.Id, document.Id, version.Id, 0.9);
    }

    private static QdrantKnowledgeService Service(WechatRobotDbContext database, KnowledgeIndexOptions options) =>
        new(database, new ModelConfigurationService(new PassThroughProtector()), options, TimeProvider.System);

    private sealed class EmptyRecordingVectorStore : IVectorStore
    {
        private int _callCount;
        public int CallCount => _callCount;
        public ConcurrentBag<VectorSearchRequest> Requests { get; } = [];
        public Task<IReadOnlyList<VectorSearchHit>> SearchAsync(VectorSearchRequest request, CancellationToken token)
        {
            Interlocked.Increment(ref _callCount);
            Requests.Add(request);
            return Task.FromResult<IReadOnlyList<VectorSearchHit>>([]);
        }
        public Task EnsureCollectionAsync(VectorCollection collection, CancellationToken token) => Task.CompletedTask;
        public Task UpsertAsync(VectorCollection collection, IReadOnlyList<VectorPoint> points, CancellationToken token) => Task.CompletedTask;
        public Task SetVersionActiveAsync(VectorCollection collection, Guid versionId, bool active, CancellationToken token) => Task.CompletedTask;
        public Task DeleteCollectionAsync(VectorCollection collection, CancellationToken token) => Task.CompletedTask;
        public Task<VectorCollection?> InspectCollectionAsync(string collectionName, CancellationToken token) => Task.FromResult<VectorCollection?>(null);
        public Task DeleteVersionAsync(VectorCollection collection, Guid versionId, CancellationToken token) => Task.CompletedTask;
        public Task<IReadOnlyList<VectorPointMetadata>> InspectVersionAsync(VectorCollection collection, Guid versionId, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<VectorPointMetadata>>([]);
    }

    private sealed class SelectiveVectorStore(
        string unavailableCollection,
        IReadOnlyDictionary<string, IReadOnlyList<VectorSearchHit>> hits,
        bool failAll = false) : IVectorStore
    {
        private int _callCount;
        public int CallCount => _callCount;

        public Task<IReadOnlyList<VectorSearchHit>> SearchAsync(
            VectorSearchRequest request,
            CancellationToken token)
        {
            Interlocked.Increment(ref _callCount);
            if (failAll ||
                string.Equals(
                    request.Collection.Name,
                    unavailableCollection,
                    StringComparison.Ordinal))
            {
                throw new VectorStoreUnavailableException(
                    "Simulated unavailable collection.");
            }

            return Task.FromResult(
                hits.TryGetValue(request.Collection.Name, out var result)
                    ? result
                    : (IReadOnlyList<VectorSearchHit>)[]);
        }

        public Task EnsureCollectionAsync(
            VectorCollection collection,
            CancellationToken token) => Task.CompletedTask;
        public Task UpsertAsync(
            VectorCollection collection,
            IReadOnlyList<VectorPoint> points,
            CancellationToken token) => Task.CompletedTask;
        public Task SetVersionActiveAsync(
            VectorCollection collection,
            Guid versionId,
            bool active,
            CancellationToken token) => Task.CompletedTask;
        public Task DeleteCollectionAsync(
            VectorCollection collection,
            CancellationToken token) => Task.CompletedTask;
        public Task<VectorCollection?> InspectCollectionAsync(
            string collectionName,
            CancellationToken token) =>
            Task.FromResult<VectorCollection?>(null);
        public Task DeleteVersionAsync(
            VectorCollection collection,
            Guid versionId,
            CancellationToken token) => Task.CompletedTask;
        public Task<IReadOnlyList<VectorPointMetadata>> InspectVersionAsync(
            VectorCollection collection,
            Guid versionId,
            CancellationToken token) =>
            Task.FromResult<IReadOnlyList<VectorPointMetadata>>([]);
    }

    private sealed class PassThroughProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }
}
