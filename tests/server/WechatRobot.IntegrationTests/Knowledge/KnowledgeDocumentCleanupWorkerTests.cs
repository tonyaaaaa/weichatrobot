using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WechatRobot.Application.Jobs;
using WechatRobot.Application.Knowledge;
using WechatRobot.Application.Models;
using WechatRobot.Application.Security;
using WechatRobot.Application.Storage;
using WechatRobot.Infrastructure.Knowledge;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.Worker.Jobs;

namespace WechatRobot.IntegrationTests.Knowledge;

public sealed class KnowledgeDocumentCleanupWorkerTests
{
    [Fact]
    public async Task Physical_delete_job_removes_every_oss_object_and_vector_generation_then_completes()
    {
        var documentId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var job = new LeasedDurableJob(Guid.NewGuid(), "CleanupKnowledgeDocument", JsonSerializer.Serialize(new { documentId }), 0, "cleanup-owner");
        var jobs = new FakeJobs(job);
        var storage = new FakeStorage();
        var vectors = new FakeVectors();
        var services = new ServiceCollection();
        var databaseName = Guid.NewGuid().ToString();
        services.AddDbContext<WechatRobotDbContext>(builder => builder.UseInMemoryDatabase(databaseName));
        services.AddSingleton<IDurableJobRepository>(jobs);
        services.AddSingleton<IObjectStorage>(storage);
        services.AddSingleton<IVectorStore>(vectors);
        services.AddSingleton(new KnowledgeIndexOptions(3, VectorDistance.Cosine));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ISecretProtector, PassThroughProtector>();
        services.AddScoped<ModelConfigurationService>();
        services.AddScoped<QdrantKnowledgeService>();
        await using var provider = services.BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            var document = new KnowledgeDocumentEntity { Id = documentId, Status = "disabled", IsDeleteRequested = true };
            var version = new KnowledgeDocumentVersionEntity
            {
                Id = versionId, KnowledgeDocumentId = documentId, Version = 1, OriginalFileName = "a.txt", SafeFileName = "a.txt", ContentType = "text/plain",
                Sha256 = "a".PadLeft(64, '0'), ObjectKey = "wechatrobot/knowledge/a.txt", Status = "disabled", IndexCollectionName = "kb_cosine_3_g1",
                EmbeddingDimension = 3, VectorDistance = "cosine", IndexGeneration = 1
            };
            database.AddRange(document, version, new KnowledgeIndexJobEntity
            {
                KnowledgeDocumentId = documentId, KnowledgeDocumentVersionId = versionId, CollectionName = "kb_cosine_3_g2",
                Dimension = 3, Distance = "cosine", Generation = 2, Status = "failed"
            });
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        await using (var verifyScope = provider.CreateAsyncScope())
            Assert.Equal("wechatrobot/knowledge/a.txt", (await verifyScope.ServiceProvider.GetRequiredService<WechatRobotDbContext>()
                .KnowledgeDocumentVersions.SingleAsync(TestContext.Current.CancellationToken)).ObjectKey);

        var worker = new KnowledgeDocumentCleanupWorker(provider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
        Assert.True(await worker.ProcessOnceAsync(TestContext.Current.CancellationToken));

        Assert.False(jobs.Failed, jobs.FailureReason);
        Assert.Equal(["wechatrobot/knowledge/a.txt"], storage.Deleted);
        Assert.Equal(["kb_cosine_3_g1", "kb_cosine_3_g2"], vectors.Deleted.Select(item => item.Collection.Name).Distinct().Order().ToArray());
        Assert.Equal(4, vectors.Deleted.Count);
        Assert.All(vectors.Deleted, item => Assert.Equal(versionId, item.VersionId));
        Assert.True(jobs.Completed);
        Assert.False(jobs.Failed);
        Assert.False(await worker.ProcessOnceAsync(TestContext.Current.CancellationToken));
    }

    private sealed class FakeStorage : IObjectStorage
    {
        public List<string> Deleted { get; } = [];
        public Task DeleteAsync(string objectKey, CancellationToken token) { Deleted.Add(objectKey); return Task.CompletedTask; }
        public Task<StoredObject> PutAsync(string objectKey, Stream content, string contentType, CancellationToken token) => throw new NotSupportedException();
    }
    private sealed class FakeVectors : IVectorStore
    {
        public List<(VectorCollection Collection, Guid VersionId)> Deleted { get; } = [];
        public Task DeleteVersionAsync(VectorCollection collection, Guid versionId, CancellationToken token) { Deleted.Add((collection, versionId)); return Task.CompletedTask; }
        public Task DeleteCollectionAsync(VectorCollection collection, CancellationToken token) => Task.CompletedTask;
        public Task<VectorCollection?> InspectCollectionAsync(string collectionName, CancellationToken token) => Task.FromResult<VectorCollection?>(null);
        public Task EnsureCollectionAsync(VectorCollection collection, CancellationToken token) => Task.CompletedTask;
        public Task UpsertAsync(VectorCollection collection, IReadOnlyList<VectorPoint> points, CancellationToken token) => Task.CompletedTask;
        public Task SetVersionActiveAsync(VectorCollection collection, Guid versionId, bool active, CancellationToken token) => Task.CompletedTask;
        public Task<IReadOnlyList<VectorPointMetadata>> InspectVersionAsync(VectorCollection collection, Guid versionId, CancellationToken token) => Task.FromResult<IReadOnlyList<VectorPointMetadata>>([]);
        public Task<IReadOnlyList<VectorSearchHit>> SearchAsync(VectorSearchRequest request, CancellationToken token) => Task.FromResult<IReadOnlyList<VectorSearchHit>>([]);
    }
    private sealed class FakeJobs(LeasedDurableJob job) : IDurableJobRepository
    {
        private bool _leased;
        public bool Completed { get; private set; }
        public bool Failed { get; private set; }
        public string FailureReason { get; private set; } = string.Empty;
        public Task<LeasedDurableJob?> LeaseNextJobAsync(string type, string owner, DateTime now, TimeSpan duration, CancellationToken token)
        { if (_leased) return Task.FromResult<LeasedDurableJob?>(null); _leased = true; return Task.FromResult<LeasedDurableJob?>(job); }
        public Task CompleteJobAsync(Guid id, string owner, DateTime at, CancellationToken token) { Completed = true; return Task.CompletedTask; }
        public Task FailJobAsync(LeasedDurableJob value, string reason, DateTime at, CancellationToken token) { Failed = true; FailureReason = reason; return Task.CompletedTask; }
        public Task<InboundMessageIngestResult> IngestInboundMessageAsync(InboundMessageIngestRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task<EnqueueSendCommandResult> EnqueueSendCommandAsync(EnqueueSendCommandRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task<LeasedSendCommand?> LeaseNextSendCommandAsync(string owner, DateTime now, TimeSpan duration, CancellationToken token) => throw new NotSupportedException();
        public Task<bool> MarkSendDispatchingAsync(LeasedSendCommand command, DateTime dispatchedAtUtc, CancellationToken token) => throw new NotSupportedException();
        public Task MarkSendDeliveryUnknownAsync(LeasedSendCommand command, string reason, DateTime failedAtUtc, CancellationToken token) => throw new NotSupportedException();
        public Task MarkSendAcceptedAsync(LeasedSendCommand command, string workToolMessageId, DateTime at, CancellationToken token) => throw new NotSupportedException();
        public Task MarkSendRejectedAsync(LeasedSendCommand command, string reason, DateTime at, CancellationToken token) => throw new NotSupportedException();
        public Task FailSendCommandAsync(LeasedSendCommand command, string reason, DateTime at, TimeSpan? delay, CancellationToken token) => throw new NotSupportedException();
        public Task<bool> RenewSendLeasesAsync(LeasedSendCommand command, DateTime now, TimeSpan duration, CancellationToken token) => throw new NotSupportedException();
    }
    private sealed class PassThroughProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }
}
