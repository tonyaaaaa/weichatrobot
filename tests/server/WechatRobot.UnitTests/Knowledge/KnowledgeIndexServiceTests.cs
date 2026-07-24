using WechatRobot.Application.Knowledge;
using WechatRobot.Application.Models;

namespace WechatRobot.UnitTests.Knowledge;

public sealed class KnowledgeIndexServiceTests
{
    private static readonly Guid DocumentId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid VersionId = Guid.Parse("20000000-0000-0000-0000-000000000001");

    [Fact]
    public async Task Batches_points_and_activates_only_after_every_batch_succeeds()
    {
        var knowledge = new FakeKnowledgeService(Work(5));
        var vectors = new FakeVectorStore();
        var embeddings = new FakeEmbeddingClient(3);
        var service = new KnowledgeIndexService(embeddings, vectors, knowledge,
            new KnowledgeIndexOptions(3, VectorDistance.Cosine, 2, 2));

        await service.IndexAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.Equal([2, 2, 1], vectors.BatchSizes);
        Assert.Equal([2, 2, 1], embeddings.BatchSizes);
        Assert.Equal(["ensure", "upsert", "upsert", "upsert", "activate-vector", "activate-mysql", "cleanup"],
            vectors.Events.Concat(knowledge.Events).OrderBy(item => item.Sequence).Select(item => item.Name));
    }

    [Fact]
    public async Task Retries_retryable_vector_failure_without_activating_partial_points()
    {
        var knowledge = new FakeKnowledgeService(Work(2));
        var vectors = new FakeVectorStore { RetryableFailuresRemaining = 1 };
        var embeddings = new FakeEmbeddingClient(3);
        var service = new KnowledgeIndexService(embeddings, vectors, knowledge,
            new KnowledgeIndexOptions(3, VectorDistance.Cosine, 2, 2));

        await service.IndexAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.Equal(2, vectors.UpsertAttempts);
        Assert.Equal([2], embeddings.BatchSizes);
        Assert.True(knowledge.Activated);
    }

    [Fact]
    public async Task Dimension_mismatch_is_a_hard_configuration_error_and_never_activates()
    {
        var knowledge = new FakeKnowledgeService(Work(1));
        var vectors = new FakeVectorStore();
        var service = new KnowledgeIndexService(new FakeEmbeddingClient(2), vectors, knowledge,
            new KnowledgeIndexOptions(3, VectorDistance.Cosine, 2, 2));

        await Assert.ThrowsAsync<EmbeddingDimensionMismatchException>(() =>
            service.IndexAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));

        Assert.False(knowledge.Activated);
        Assert.Empty(vectors.BatchSizes);
        Assert.True(knowledge.Failed);
    }

    [Fact]
    public async Task Final_batch_failure_leaves_version_inactive_and_marks_job_retryable()
    {
        var knowledge = new FakeKnowledgeService(Work(3));
        var vectors = new FakeVectorStore { FailBatchNumber = 2 };
        var service = new KnowledgeIndexService(new FakeEmbeddingClient(3), vectors, knowledge,
            new KnowledgeIndexOptions(3, VectorDistance.Cosine, 2, 1));

        await Assert.ThrowsAsync<VectorStoreUnavailableException>(() =>
            service.IndexAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));

        Assert.False(knowledge.Activated);
        Assert.True(knowledge.RetryableFailure);
        Assert.DoesNotContain(vectors.Events, item => item.Name == "activate-vector");
    }

    private static KnowledgeIndexWork Work(int count) => new(Guid.NewGuid(), DocumentId, VersionId, null, "kb_cosine_3", 3,
        VectorDistance.Cosine, Enumerable.Range(0, count).Select(index => new KnowledgeIndexChunk(
            Guid.Parse($"30000000-0000-0000-0000-{index + 1:000000000000}"), DocumentId, VersionId, $"text-{index}",
            [Guid.Parse("40000000-0000-0000-0000-000000000001")])).ToArray(), "test");

    private sealed class FakeEmbeddingClient(int dimension) : IEmbeddingClient
    {
        public List<int> BatchSizes { get; } = [];

        public Task<EmbeddingBatchResponse> CreateEmbeddingsAsync(ModelProviderConfiguration configuration, EmbeddingBatchRequest request, CancellationToken cancellationToken = default)
        {
            BatchSizes.Add(request.Inputs.Count);
            return Task.FromResult(new EmbeddingBatchResponse(request.Inputs
                .Select(_ => (IReadOnlyList<float>)Enumerable.Repeat(1f, dimension).ToArray()).ToArray()));
        }
    }

    private sealed class FakeVectorStore : IVectorStore
    {
        private int _batch;
        public int RetryableFailuresRemaining { get; set; }
        public int? FailBatchNumber { get; set; }
        public int UpsertAttempts { get; private set; }
        public List<int> BatchSizes { get; } = [];
        public List<(long Sequence, string Name)> Events { get; } = [];
        public Task EnsureCollectionAsync(VectorCollection collection, CancellationToken token) { Events.Add((EventClock.Next(), "ensure")); return Task.CompletedTask; }
        public Task UpsertAsync(VectorCollection collection, IReadOnlyList<VectorPoint> points, CancellationToken token)
        {
            UpsertAttempts++;
            if (RetryableFailuresRemaining-- > 0) throw new VectorStoreUnavailableException("temporary");
            _batch++;
            if (FailBatchNumber == _batch) throw new VectorStoreUnavailableException("down");
            BatchSizes.Add(points.Count); Events.Add((EventClock.Next(), "upsert")); return Task.CompletedTask;
        }
        public Task SetVersionActiveAsync(VectorCollection collection, Guid versionId, bool active, CancellationToken token) { Events.Add((EventClock.Next(), "activate-vector")); return Task.CompletedTask; }
        public Task DeleteCollectionAsync(VectorCollection collection, CancellationToken token) => Task.CompletedTask;
        public Task<VectorCollection?> InspectCollectionAsync(string collectionName, CancellationToken token) => Task.FromResult<VectorCollection?>(null);
        public Task DeleteVersionAsync(VectorCollection collection, Guid versionId, CancellationToken token) => Task.CompletedTask;
        public Task<IReadOnlyList<VectorPointMetadata>> InspectVersionAsync(VectorCollection collection, Guid versionId, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<VectorPointMetadata>>([]);
        public Task<IReadOnlyList<VectorSearchHit>> SearchAsync(VectorSearchRequest request, CancellationToken token) => Task.FromResult<IReadOnlyList<VectorSearchHit>>([]);
    }

    private sealed class FakeKnowledgeService(KnowledgeIndexWork work) : IKnowledgeService
    {
        public bool Activated { get; private set; }
        public bool Failed { get; private set; }
        public bool RetryableFailure { get; private set; }
        public List<(long Sequence, string Name)> Events { get; } = [];
        public Task<KnowledgeIndexWork> LoadIndexWorkAsync(Guid jobId, CancellationToken token) => Task.FromResult(work);
        public Task<ModelProviderConfiguration> LoadEmbeddingConfigurationAsync(CancellationToken token) => Task.FromResult(new ModelProviderConfiguration("https://fake/", "fake", "cipher", TimeSpan.FromSeconds(1), 0));
        public Task<bool> IsIndexLeaseOwnedAsync(Guid jobId, string owner, CancellationToken token) => Task.FromResult(true);
        public Task<bool> ActivateVersionAsync(KnowledgeIndexWork value, CancellationToken token) { Activated = true; Events.Add((EventClock.Next(), "activate-mysql")); return Task.FromResult(true); }
        public Task EnqueueCleanupAsync(KnowledgeIndexWork value, CancellationToken token) { Events.Add((EventClock.Next(), "cleanup")); return Task.CompletedTask; }
        public Task MarkIndexFailedAsync(Guid jobId, string? leaseOwner, string reason, bool retryable, CancellationToken token) { Failed = true; RetryableFailure = retryable; return Task.CompletedTask; }
    }

    private static class EventClock { private static long _value; public static long Next() => Interlocked.Increment(ref _value); }
}
