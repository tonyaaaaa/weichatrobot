using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WechatRobot.Application.Knowledge;
using WechatRobot.Application.Models;
using WechatRobot.Application.Security;
using WechatRobot.Infrastructure.Knowledge;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.IntegrationTests.Infrastructure;
using WechatRobot.Worker.Jobs;

namespace WechatRobot.IntegrationTests.Knowledge;

public sealed class KnowledgeIndexMySqlConcurrencyTests : IClassFixture<MySqlFixture>
{
    private readonly MySqlFixture _fixture;
    public KnowledgeIndexMySqlConcurrencyTests(MySqlFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Concurrent_version_activation_has_one_winner_and_enqueues_old_cleanup_atomically()
    {
        var options = new DbContextOptionsBuilder<WechatRobotDbContext>().UseMySQL(_fixture.ConnectionString).Options;
        Guid documentId;
        await using (var setup = new WechatRobotDbContext(options))
        {
            await setup.Database.MigrateAsync(TestContext.Current.CancellationToken);
            var document = new KnowledgeDocumentEntity { Status = "active", ActiveCollectionName = "kb_cosine_3_g1_old", ActiveEmbeddingDimension = 3, ActiveDistance = "cosine", ActiveIndexGeneration = 1 };
            documentId = document.Id;
            var oldVersion = Version(document.Id, 1, "active", true);
            oldVersion.IndexCollectionName = "kb_cosine_3"; oldVersion.EmbeddingDimension = 3; oldVersion.VectorDistance = "cosine";
            var second = Version(document.Id, 2, "approved", false);
            var third = Version(document.Id, 3, "approved", false);
            document.ActiveVersionId = oldVersion.Id;
            setup.AddRange(document, oldVersion, second, third);
            setup.KnowledgeIndexJobs.AddRange(Job(document.Id, second.Id, oldVersion.Id), Job(document.Id, third.Id, oldVersion.Id));
            await setup.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        KnowledgeIndexWork Work(KnowledgeIndexJobEntity job) => new(job.Id, job.KnowledgeDocumentId, job.KnowledgeDocumentVersionId,
            job.PreviousActiveVersionId, job.CollectionName, 3, VectorDistance.Cosine, [], job.LeaseOwner, job.Generation,
            job.PreviousActiveCollectionName, 3, VectorDistance.Cosine);
        KnowledgeIndexJobEntity[] jobs;
        await using (var read = new WechatRobotDbContext(options)) jobs = await read.KnowledgeIndexJobs.AsNoTracking().Where(job => job.Operation == "index").ToArrayAsync(TestContext.Current.CancellationToken);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> ActivateAsync(KnowledgeIndexJobEntity job)
        {
            await using var database = new WechatRobotDbContext(options);
            var service = Service(database);
            await gate.Task;
            return await service.ActivateVersionAsync(Work(job), TestContext.Current.CancellationToken);
        }
        var attempts = jobs.Select(ActivateAsync).ToArray();
        gate.SetResult();
        var results = await Task.WhenAll(attempts);

        Assert.Single(results, result => result);
        await using var verify = new WechatRobotDbContext(options);
        var storedDocument = await verify.KnowledgeDocuments.AsNoTracking().SingleAsync(document => document.Id == documentId, TestContext.Current.CancellationToken);
        Assert.True(storedDocument.ActiveVersionId.HasValue);
        Assert.Contains(storedDocument.ActiveVersionId.Value, jobs.Select(job => job.KnowledgeDocumentVersionId));
        var published = await verify.KnowledgeDocumentVersions.AsNoTracking().Where(version => version.KnowledgeDocumentId == documentId && version.IsPublished).ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(storedDocument.ActiveVersionId, Assert.Single(published).Id);
        var cleanup = await verify.KnowledgeIndexJobs.AsNoTracking().Where(job => job.KnowledgeDocumentId == documentId && job.Operation == "cleanup").ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Single(cleanup);
        Assert.Equal(1, await verify.KnowledgeIndexJobs.CountAsync(job => job.KnowledgeDocumentId == storedDocument.Id && job.Status == "completed", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Renewed_lease_cannot_be_reclaimed_but_expired_owner_can_be_recovered()
    {
        var dbOptions = new DbContextOptionsBuilder<WechatRobotDbContext>().UseMySQL(_fixture.ConnectionString).Options;
        var documentId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        await using (var setup = new WechatRobotDbContext(dbOptions))
        {
            await setup.Database.MigrateAsync(TestContext.Current.CancellationToken);
            await setup.KnowledgeIndexJobs.ExecuteUpdateAsync(setters => setters.SetProperty(job => job.Status, "completed"), TestContext.Current.CancellationToken);
            setup.Add(new KnowledgeDocumentEntity { Id = documentId, Status = "indexing" });
            var storedVersion = Version(documentId, 1, "approved", false);
            storedVersion.Id = versionId;
            setup.Add(storedVersion);
            setup.KnowledgeIndexJobs.Add(new KnowledgeIndexJobEntity
            {
                KnowledgeDocumentId = documentId, KnowledgeDocumentVersionId = versionId, CollectionName = "kb_cosine_3_g1_lease",
                Dimension = 3, Distance = "cosine", Status = "pending", NextAttemptAtUtc = new DateTime(2026, 1, 1)
            });
            await setup.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var now = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        Guid jobId;
        await using (var firstDb = new WechatRobotDbContext(dbOptions))
        {
            var first = Service(firstDb);
            var lease = Assert.IsType<LeasedKnowledgeIndexJob>(await first.LeaseNextAsync("owner-a", now, TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken));
            jobId = lease.Id;
            Assert.True(await first.RenewLeaseAsync(jobId, "owner-a", now.AddMilliseconds(80), TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken));
        }
        await using (var competingDb = new WechatRobotDbContext(dbOptions))
            Assert.Null(await Service(competingDb).LeaseNextAsync("owner-b", now.AddMilliseconds(150), TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken));
        await using (var recoveryDb = new WechatRobotDbContext(dbOptions))
        {
            var recovered = Assert.IsType<LeasedKnowledgeIndexJob>(await Service(recoveryDb).LeaseNextAsync("owner-b", now.AddMilliseconds(300), TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken));
            Assert.Equal(jobId, recovered.Id);
            Assert.Equal("owner-b", recovered.LeaseOwner);
        }
    }

    [Fact]
    public async Task Worker_renews_short_lease_during_slow_embedding_and_prevents_duplicate_provider_cost()
    {
        var services = new ServiceCollection();
        services.AddDbContext<WechatRobotDbContext>(builder => builder.UseMySQL(_fixture.ConnectionString));
        services.AddSingleton(new KnowledgeIndexOptions(3, VectorDistance.Cosine, 1, 2));
        services.AddSingleton(new KnowledgeIndexWorkerOptions(TimeSpan.FromMilliseconds(180), TimeSpan.FromMilliseconds(40)));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ISecretProtector, PassThroughProtector>();
        services.AddScoped<ModelConfigurationService>();
        services.AddScoped<QdrantKnowledgeService>();
        services.AddScoped<IKnowledgeService>(provider => provider.GetRequiredService<QdrantKnowledgeService>());
        services.AddScoped<KnowledgeIndexService>();
        var embedding = new SlowEmbeddingClient();
        services.AddSingleton<IEmbeddingClient>(embedding);
        services.AddSingleton<IVectorStore, MemoryVectorStore>();
        await using var provider = services.BuildServiceProvider();
        Guid jobId;
        await using (var setupScope = provider.CreateAsyncScope())
        {
            var database = setupScope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            await database.Database.MigrateAsync(TestContext.Current.CancellationToken);
            await database.KnowledgeIndexJobs.ExecuteUpdateAsync(setters => setters.SetProperty(job => job.Status, "completed"), TestContext.Current.CancellationToken);
            var tag = new KnowledgeTagEntity { Name = Guid.NewGuid().ToString("N"), NormalizedName = Guid.NewGuid().ToString("N") };
            var document = new KnowledgeDocumentEntity { Status = "uploaded" };
            var version = Version(document.Id, 1, "approved", false);
            var chunk = new KnowledgeChunkEntity { KnowledgeDocumentVersionId = version.Id, Text = "slow", Status = "approved" };
            database.AddRange(tag, document, version, chunk, new ModelConfigEntity
            {
                Name = Guid.NewGuid().ToString("N"), Provider = "openai-compatible", ConfigurationType = "embedding", BaseUrl = "https://fake/",
                Model = "fake", EncryptedApiKey = "key", IsDefault = true, IsEnabled = true
            });
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
            jobId = await setupScope.ServiceProvider.GetRequiredService<QdrantKnowledgeService>()
                .QueueIndexAsync(document.Id, version.Id, [tag.Id], false, TestContext.Current.CancellationToken);
        }

        var worker = new KnowledgeIndexWorker(provider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System,
            provider.GetRequiredService<KnowledgeIndexWorkerOptions>());
        var running = worker.ProcessOnceAsync(TestContext.Current.CancellationToken);
        await embedding.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        await using (var competitorScope = provider.CreateAsyncScope())
        {
            var competitor = competitorScope.ServiceProvider.GetRequiredService<QdrantKnowledgeService>();
            Assert.Null(await competitor.LeaseNextAsync("other-worker", DateTime.UtcNow, TimeSpan.FromMilliseconds(180), TestContext.Current.CancellationToken));
        }
        embedding.Release.SetResult();
        Assert.True(await running);
        Assert.Equal(1, embedding.Calls);
        await using var verifyScope = provider.CreateAsyncScope();
        Assert.Equal("completed", (await verifyScope.ServiceProvider.GetRequiredService<WechatRobotDbContext>().KnowledgeIndexJobs
            .SingleAsync(job => job.Id == jobId, TestContext.Current.CancellationToken)).Status);
    }

    private static QdrantKnowledgeService Service(WechatRobotDbContext database) => new(database,
        new ModelConfigurationService(new PassThroughProtector()), new KnowledgeIndexOptions(3, VectorDistance.Cosine), TimeProvider.System);

    private static KnowledgeIndexJobEntity Job(Guid documentId, Guid versionId, Guid oldVersionId) => new()
    {
        KnowledgeDocumentId = documentId, KnowledgeDocumentVersionId = versionId, PreviousActiveVersionId = oldVersionId,
        CollectionName = $"kb_cosine_3_g1_{versionId:N}", Dimension = 3, Distance = "cosine", Status = "leased", LeaseOwner = "test",
        PreviousActiveCollectionName = "kb_cosine_3_g1_old", PreviousActiveEmbeddingDimension = 3, PreviousActiveDistance = "cosine"
    };

    private static KnowledgeDocumentVersionEntity Version(Guid documentId, int number, string status, bool published) => new()
    {
        KnowledgeDocumentId = documentId, Version = number, OriginalFileName = $"v{number}.txt", SafeFileName = $"v{number}.txt",
        ContentType = "text/plain", Sha256 = Guid.NewGuid().ToString("N").PadLeft(64, '0'), ObjectKey = $"v{number}-{Guid.NewGuid():N}", Status = status, IsPublished = published
    };

    private sealed class PassThroughProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }
    private sealed class SlowEmbeddingClient : IEmbeddingClient
    {
        public int Calls { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<EmbeddingBatchResponse> CreateEmbeddingsAsync(ModelProviderConfiguration configuration, EmbeddingBatchRequest request, CancellationToken token = default)
        {
            Calls++; Started.TrySetResult(); await Release.Task.WaitAsync(token);
            return new EmbeddingBatchResponse(request.Inputs.Select(_ => (IReadOnlyList<float>)new float[] { 1, 0, 0 }).ToArray());
        }
    }
    private sealed class MemoryVectorStore : IVectorStore
    {
        public Task EnsureCollectionAsync(VectorCollection collection, CancellationToken token) => Task.CompletedTask;
        public Task UpsertAsync(VectorCollection collection, IReadOnlyList<VectorPoint> points, CancellationToken token) => Task.CompletedTask;
        public Task SetVersionActiveAsync(VectorCollection collection, Guid versionId, bool active, CancellationToken token) => Task.CompletedTask;
        public Task DeleteVersionAsync(VectorCollection collection, Guid versionId, CancellationToken token) => Task.CompletedTask;
        public Task<IReadOnlyList<VectorPointMetadata>> InspectVersionAsync(VectorCollection collection, Guid versionId, CancellationToken token) => Task.FromResult<IReadOnlyList<VectorPointMetadata>>([]);
        public Task<IReadOnlyList<VectorSearchHit>> SearchAsync(VectorSearchRequest request, CancellationToken token) => Task.FromResult<IReadOnlyList<VectorSearchHit>>([]);
    }
}
