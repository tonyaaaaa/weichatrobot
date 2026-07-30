using System.Data.Common;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using WechatRobot.Application.Jobs;
using WechatRobot.Application.Knowledge;
using WechatRobot.Application.Models;
using WechatRobot.Application.Security;
using WechatRobot.Application.Storage;
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
            await database.ModelConfigs.Where(item => item.ConfigurationType == "embedding" && item.IsDefault)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsDefault, false), TestContext.Current.CancellationToken);
            await database.KnowledgeIndexJobs.ExecuteUpdateAsync(setters => setters.SetProperty(job => job.Status, "completed"), TestContext.Current.CancellationToken);
            var tag = new KnowledgeTagEntity { Name = Guid.NewGuid().ToString("N"), NormalizedName = Guid.NewGuid().ToString("N") };
            var collection = $"kb_cosine_3_stale_{Guid.NewGuid():N}";
            var document = new KnowledgeDocumentEntity
            {
                Status = "active",
                ActiveCollectionName = collection,
                ActiveEmbeddingDimension = 3,
                ActiveDistance = "cosine",
                ActiveIndexGeneration = 1
            };
            var version = Version(document.Id, 1, "active", true);
            version.IndexCollectionName = collection;
            version.EmbeddingDimension = 3;
            version.VectorDistance = "cosine";
            version.IndexGeneration = 1;
            document.ActiveVersionId = version.Id;
            var chunk = new KnowledgeChunkEntity { KnowledgeDocumentVersionId = version.Id, Text = "slow", Status = "approved" };
            database.AddRange(tag, document, version, chunk, new ModelConfigEntity
            {
                Name = Guid.NewGuid().ToString("N"), NormalizedName = Guid.NewGuid().ToString("N"), Provider = "openai-compatible", ConfigurationType = "embedding", BaseUrl = "https://fake/",
                Model = "fake", EncryptedApiKey = "key", IsDefault = true, IsEnabled = true, EmbeddingDimension = 3
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

    [Fact]
    public async Task Physical_delete_cancels_leased_index_and_cleanup_drains_then_removes_racing_qdrant_write()
    {
        await using var qdrant = new ContainerBuilder("qdrant/qdrant:v1.18.2").WithPortBinding(6333, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(6333).ForPath("/readyz"))).Build();
        await qdrant.StartAsync(TestContext.Current.CancellationToken);
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{qdrant.GetMappedPublicPort(6333)}") };
        var realVectors = new QdrantVectorStore(http);
        var blockingVectors = new WriteThenBlockVectorStore(realVectors);
        var storage = new RecordingStorage();
        var services = new ServiceCollection();
        services.AddDbContext<WechatRobotDbContext>(builder => builder.UseMySQL(_fixture.ConnectionString));
        services.AddSingleton(new KnowledgeIndexOptions(3, VectorDistance.Cosine, 1, 2));
        services.AddSingleton(new KnowledgeIndexWorkerOptions(TimeSpan.FromMilliseconds(450), TimeSpan.FromMilliseconds(40)));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ISecretProtector, PassThroughProtector>();
        services.AddSingleton<IEmbeddingClient, ImmediateEmbeddingClient>();
        services.AddSingleton<IVectorStore>(blockingVectors);
        services.AddSingleton<IObjectStorage>(storage);
        services.AddScoped<IDurableJobRepository, DurableJobRepository>();
        services.AddScoped<IKnowledgeDocumentStore, KnowledgeDocumentStore>();
        services.AddScoped<ModelConfigurationService>();
        services.AddScoped<QdrantKnowledgeService>();
        services.AddScoped<IKnowledgeService>(provider => provider.GetRequiredService<QdrantKnowledgeService>());
        services.AddScoped<KnowledgeIndexService>();
        await using var provider = services.BuildServiceProvider();
        Guid documentId;
        Guid versionId;
        Guid jobId;
        string collectionName;
        await using (var setupScope = provider.CreateAsyncScope())
        {
            var database = setupScope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            await database.Database.MigrateAsync(TestContext.Current.CancellationToken);
            await database.ModelConfigs.Where(item => item.ConfigurationType == "embedding" && item.IsDefault)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsDefault, false), TestContext.Current.CancellationToken);
            await database.KnowledgeIndexJobs.ExecuteUpdateAsync(setters => setters.SetProperty(job => job.Status, "completed"), TestContext.Current.CancellationToken);
            await database.DurableJobs.ExecuteUpdateAsync(setters => setters.SetProperty(job => job.Status, "completed"), TestContext.Current.CancellationToken);
            var tag = new KnowledgeTagEntity { Name = Guid.NewGuid().ToString("N"), NormalizedName = Guid.NewGuid().ToString("N") };
            var document = new KnowledgeDocumentEntity { Status = "uploaded" };
            var version = Version(document.Id, 1, "approved", false);
            var chunk = new KnowledgeChunkEntity { KnowledgeDocumentVersionId = version.Id, Text = "racing", Status = "approved" };
            documentId = document.Id;
            versionId = version.Id;
            database.AddRange(tag, document, version, chunk,
                new ModelConfigEntity
                {
                    Name = Guid.NewGuid().ToString("N"), NormalizedName = Guid.NewGuid().ToString("N"), Provider = "openai-compatible", ConfigurationType = "embedding",
                    BaseUrl = "https://fake/", Model = "fake", EncryptedApiKey = "key", IsDefault = true, IsEnabled = true, EmbeddingDimension = 3
                });
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
            jobId = await setupScope.ServiceProvider.GetRequiredService<QdrantKnowledgeService>()
                .QueueIndexAsync(document.Id, version.Id, [tag.Id], false, TestContext.Current.CancellationToken);
            collectionName = await database.KnowledgeIndexJobs.AsNoTracking()
                .Where(job => job.Id == jobId)
                .Select(job => job.CollectionName)
                .SingleAsync(TestContext.Current.CancellationToken);
        }

        var indexWorker = new KnowledgeIndexWorker(provider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System,
            provider.GetRequiredService<KnowledgeIndexWorkerOptions>());
        var indexing = indexWorker.ProcessOnceAsync(TestContext.Current.CancellationToken);
        await blockingVectors.WriteCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await using (var deleteScope = provider.CreateAsyncScope())
            Assert.True(await deleteScope.ServiceProvider.GetRequiredService<IKnowledgeDocumentStore>()
                .RequestPhysicalDeleteAsync(documentId, TestContext.Current.CancellationToken));
        var cleanupWorker = new KnowledgeDocumentCleanupWorker(provider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
        var cleanup = cleanupWorker.ProcessOnceAsync(TestContext.Current.CancellationToken);

        Assert.True(await indexing.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.True(await cleanup.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        await using var verifyScope = provider.CreateAsyncScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        Assert.False(await verify.KnowledgeDocuments.AnyAsync(document => document.Id == documentId, TestContext.Current.CancellationToken));
        Assert.False(await verify.KnowledgeDocumentVersions.AnyAsync(version => version.Id == versionId, TestContext.Current.CancellationToken));
        Assert.False(await verify.KnowledgeIndexJobs.AnyAsync(job => job.Id == jobId, TestContext.Current.CancellationToken));
        Assert.Equal("completed", (await verify.DurableJobs.AsNoTracking().SingleAsync(job =>
            job.JobType == "CleanupKnowledgeDocument" && job.PayloadJson.Contains(documentId.ToString()), TestContext.Current.CancellationToken)).Status);
        Assert.Empty(await realVectors.InspectVersionAsync(new VectorCollection(collectionName, 3, VectorDistance.Cosine), versionId,
            TestContext.Current.CancellationToken));
        Assert.Single(storage.Deleted);
    }

    [Fact]
    public async Task Same_version_tag_reindex_keeps_active_tags_until_atomic_activation_in_mysql_and_qdrant()
    {
        await using var qdrant = new ContainerBuilder("qdrant/qdrant:v1.18.2").WithPortBinding(6333, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(6333).ForPath("/readyz"))).Build();
        await qdrant.StartAsync(TestContext.Current.CancellationToken);
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{qdrant.GetMappedPublicPort(6333)}") };
        var vectors = new QdrantVectorStore(http);
        var dbOptions = new DbContextOptionsBuilder<WechatRobotDbContext>().UseMySQL(_fixture.ConnectionString).Options;
        await using var database = new WechatRobotDbContext(dbOptions);
        await database.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await database.ModelConfigs.Where(item => item.ConfigurationType == "embedding" && item.IsDefault)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsDefault, false), TestContext.Current.CancellationToken);
        await database.KnowledgeIndexJobs.ExecuteUpdateAsync(setters => setters.SetProperty(job => job.Status, "completed"), TestContext.Current.CancellationToken);
        var tagA = new KnowledgeTagEntity { Name = Guid.NewGuid().ToString("N"), NormalizedName = Guid.NewGuid().ToString("N") };
        var tagB = new KnowledgeTagEntity { Name = Guid.NewGuid().ToString("N"), NormalizedName = Guid.NewGuid().ToString("N") };
        var document = new KnowledgeDocumentEntity
        {
            Status = "active", ActiveCollectionName = $"kb_cosine_3_tag_live_{Guid.NewGuid():N}", ActiveEmbeddingDimension = 3,
            ActiveDistance = "cosine", ActiveIndexGeneration = 1, ActiveCollectionExclusive = true
        };
        var version = Version(document.Id, 1, "active", true);
        version.IndexCollectionName = document.ActiveCollectionName;
        version.EmbeddingDimension = 3;
        version.VectorDistance = "cosine";
        version.IndexGeneration = 1;
        version.IndexCollectionExclusive = true;
        document.ActiveVersionId = version.Id;
        var chunk = new KnowledgeChunkEntity { KnowledgeDocumentVersionId = version.Id, Text = "tagged", Status = "approved" };
        var initialIndexJob = new KnowledgeIndexJobEntity
        {
            Id = StableIndexJobId(version.Id), KnowledgeDocumentId = document.Id, KnowledgeDocumentVersionId = version.Id,
            CollectionName = document.ActiveCollectionName, Dimension = 3, Distance = "cosine", Generation = 1,
            IsCollectionExclusive = true, Status = "completed"
        };
        database.AddRange(tagA, tagB, document, version, chunk,
            initialIndexJob,
            new KnowledgeChunkTagEntity { KnowledgeChunkId = chunk.Id, KnowledgeTagId = tagA.Id },
            new ModelConfigEntity
            {
                Name = Guid.NewGuid().ToString("N"), NormalizedName = Guid.NewGuid().ToString("N"), Provider = "openai-compatible", ConfigurationType = "embedding", BaseUrl = "https://fake/",
                Model = "fake", EncryptedApiKey = "key", IsDefault = true, IsEnabled = true, EmbeddingDimension = 3
            });
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        var live = new VectorCollection(document.ActiveCollectionName, 3, VectorDistance.Cosine);
        await vectors.EnsureCollectionAsync(live, TestContext.Current.CancellationToken);
        await vectors.UpsertAsync(live,
            [new VectorPoint(chunk.Id, document.Id, version.Id, [tagA.Id], [1, 0, 0], true, 1)], TestContext.Current.CancellationToken);
        var service = Service(database);

        var jobId = await service.QueueIndexAsync(document.Id, version.Id, [tagB.Id], true, TestContext.Current.CancellationToken);
        database.ChangeTracker.Clear();
        Assert.Equal([tagA.Id], await database.KnowledgeChunkTags.AsNoTracking().Where(binding => binding.KnowledgeChunkId == chunk.Id)
            .Select(binding => binding.KnowledgeTagId).ToArrayAsync(TestContext.Current.CancellationToken));
        var failedLease = Assert.IsType<LeasedKnowledgeIndexJob>(await service.LeaseNextAsync("stage-fail", DateTime.UtcNow, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken));
        var failedWork = await service.LoadIndexWorkAsync(jobId, TestContext.Current.CancellationToken);
        Assert.Equal([tagB.Id], Assert.Single(failedWork.Chunks).TagIds);
        var failedStaging = new VectorCollection(failedLease.CollectionName, 3, VectorDistance.Cosine);
        await vectors.EnsureCollectionAsync(failedStaging, TestContext.Current.CancellationToken);
        await vectors.UpsertAsync(failedStaging,
            [new VectorPoint(chunk.Id, document.Id, version.Id, [tagB.Id], [0, 1, 0], false, failedLease.Generation)], TestContext.Current.CancellationToken);
        Assert.Single(await service.SearchVisibleAsync([1, 0, 0], [tagA.Id], vectors, 5, TestContext.Current.CancellationToken));
        Assert.Empty(await service.SearchVisibleAsync([0, 1, 0], [tagB.Id], vectors, 5, TestContext.Current.CancellationToken));
        await service.MarkIndexFailedAsync(jobId, failedLease.LeaseOwner, "staged failure", false, TestContext.Current.CancellationToken);
        Assert.Equal([tagA.Id], await database.KnowledgeChunkTags.AsNoTracking().Where(binding => binding.KnowledgeChunkId == chunk.Id)
            .Select(binding => binding.KnowledgeTagId).ToArrayAsync(TestContext.Current.CancellationToken));

        await service.QueueIndexAsync(document.Id, version.Id, [tagB.Id], true, TestContext.Current.CancellationToken);
        database.ChangeTracker.Clear();
        Assert.NotNull(await service.LeaseNextAsync("stage-success", DateTime.UtcNow, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken));
        await new KnowledgeIndexService(new TagBEmbeddingClient(), vectors, service, new KnowledgeIndexOptions(3, VectorDistance.Cosine))
            .IndexAsync(jobId, TestContext.Current.CancellationToken);
        database.ChangeTracker.Clear();

        Assert.True((await database.KnowledgeIndexJobs.AsNoTracking().SingleAsync(job => job.Operation == "cleanup" &&
            job.CollectionName == live.Name, TestContext.Current.CancellationToken)).IsCollectionExclusive);

        Assert.Equal([tagB.Id], await database.KnowledgeChunkTags.AsNoTracking().Where(binding => binding.KnowledgeChunkId == chunk.Id)
            .Select(binding => binding.KnowledgeTagId).ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await service.SearchVisibleAsync([1, 0, 0], [tagA.Id], vectors, 5, TestContext.Current.CancellationToken));
        Assert.Single(await service.SearchVisibleAsync([0, 1, 0], [tagB.Id], vectors, 5, TestContext.Current.CancellationToken));

        var activeCollectionName = (await database.KnowledgeDocuments.AsNoTracking().SingleAsync(item => item.Id == document.Id,
            TestContext.Current.CancellationToken)).ActiveCollectionName!;
        var activeCollection = new VectorCollection(activeCollectionName, 3, VectorDistance.Cosine);
        var cleanupServices = new ServiceCollection();
        cleanupServices.AddDbContext<WechatRobotDbContext>(builder => builder.UseMySQL(_fixture.ConnectionString));
        cleanupServices.AddSingleton(new KnowledgeIndexOptions(3, VectorDistance.Cosine));
        cleanupServices.AddSingleton(KnowledgeIndexWorkerOptions.Default);
        cleanupServices.AddSingleton(TimeProvider.System);
        cleanupServices.AddSingleton<ISecretProtector, PassThroughProtector>();
        cleanupServices.AddSingleton<IVectorStore>(vectors);
        cleanupServices.AddSingleton<IObjectStorage, RecordingStorage>();
        cleanupServices.AddScoped<IDurableJobRepository, DurableJobRepository>();
        cleanupServices.AddScoped<ModelConfigurationService>();
        cleanupServices.AddScoped<QdrantKnowledgeService>();
        cleanupServices.AddScoped<KnowledgeDocumentStore>();
        await using var cleanupProvider = cleanupServices.BuildServiceProvider();
        var cleanupWorker = new KnowledgeIndexWorker(cleanupProvider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System,
            KnowledgeIndexWorkerOptions.Default);
        for (var attempt = 0; attempt < 4 && await database.KnowledgeIndexJobs.AsNoTracking().AnyAsync(job => job.Operation == "cleanup" &&
                 (job.Status == "pending" || job.Status == "retrying"), TestContext.Current.CancellationToken); attempt++)
            Assert.True(await cleanupWorker.ProcessOnceAsync(TestContext.Current.CancellationToken));

        Assert.Null(await vectors.InspectCollectionAsync(live.Name, TestContext.Current.CancellationToken));
        Assert.NotNull(await vectors.InspectCollectionAsync(activeCollection.Name, TestContext.Current.CancellationToken));
        Assert.Single(await service.SearchVisibleAsync([0, 1, 0], [tagB.Id], vectors, 5, TestContext.Current.CancellationToken));

        await using (var deleteScope = cleanupProvider.CreateAsyncScope())
            Assert.True(await deleteScope.ServiceProvider.GetRequiredService<KnowledgeDocumentStore>()
                .RequestPhysicalDeleteAsync(document.Id, TestContext.Current.CancellationToken));
        var physicalCleanup = new KnowledgeDocumentCleanupWorker(cleanupProvider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
        Assert.True(await physicalCleanup.ProcessOnceAsync(TestContext.Current.CancellationToken));
        Assert.Null(await vectors.InspectCollectionAsync(activeCollection.Name, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Requeue_preserves_failed_generation_cleanup_and_physical_delete_removes_every_overwritten_generation()
    {
        await using var qdrant = new ContainerBuilder("qdrant/qdrant:v1.18.2").WithPortBinding(6333, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(6333).ForPath("/readyz"))).Build();
        await qdrant.StartAsync(TestContext.Current.CancellationToken);
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{qdrant.GetMappedPublicPort(6333)}") };
        var vectors = new QdrantVectorStore(http);
        var storage = new RecordingStorage();
        var services = new ServiceCollection();
        services.AddDbContext<WechatRobotDbContext>(builder => builder.UseMySQL(_fixture.ConnectionString));
        services.AddSingleton(new KnowledgeIndexOptions(3, VectorDistance.Cosine, 1, 2));
        services.AddSingleton(KnowledgeIndexWorkerOptions.Default);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ISecretProtector, PassThroughProtector>();
        services.AddSingleton<IVectorStore>(vectors);
        services.AddSingleton<IObjectStorage>(storage);
        services.AddScoped<IDurableJobRepository, DurableJobRepository>();
        services.AddScoped<IKnowledgeDocumentStore, KnowledgeDocumentStore>();
        services.AddScoped<ModelConfigurationService>();
        services.AddScoped<QdrantKnowledgeService>();
        await using var provider = services.BuildServiceProvider();
        Guid documentId;
        Guid versionId;
        Guid chunkId;
        Guid primaryJobId;
        string generationOne;
        string generationTwo;
        string generationThree;
        await using (var scope = provider.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            await database.Database.MigrateAsync(TestContext.Current.CancellationToken);
            await database.KnowledgeIndexJobs.ExecuteUpdateAsync(setters => setters.SetProperty(job => job.Status, "completed"), TestContext.Current.CancellationToken);
            await database.DurableJobs.ExecuteUpdateAsync(setters => setters.SetProperty(job => job.Status, "completed"), TestContext.Current.CancellationToken);
            var tag = new KnowledgeTagEntity { Name = Guid.NewGuid().ToString("N"), NormalizedName = Guid.NewGuid().ToString("N") };
            var document = new KnowledgeDocumentEntity { Status = "uploaded" };
            var version = Version(document.Id, 1, "approved", false);
            var chunk = new KnowledgeChunkEntity { KnowledgeDocumentVersionId = version.Id, Text = "failed generation", Status = "approved" };
            documentId = document.Id;
            versionId = version.Id;
            chunkId = chunk.Id;
            database.AddRange(tag, document, version, chunk);
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
            var service = scope.ServiceProvider.GetRequiredService<QdrantKnowledgeService>();
            primaryJobId = await service.QueueIndexAsync(document.Id, version.Id, [tag.Id], true, TestContext.Current.CancellationToken);
            database.ChangeTracker.Clear();
            var firstLease = Assert.IsType<LeasedKnowledgeIndexJob>(await service.LeaseNextAsync("failed-g1", DateTime.UtcNow,
                TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken));
            generationOne = firstLease.CollectionName;
            var firstCollection = new VectorCollection(generationOne, 3, VectorDistance.Cosine);
            await vectors.EnsureCollectionAsync(firstCollection, TestContext.Current.CancellationToken);
            await vectors.UpsertAsync(firstCollection,
                [new VectorPoint(chunk.Id, document.Id, version.Id, [tag.Id], [1, 0, 0], false, firstLease.Generation)], TestContext.Current.CancellationToken);
            await service.MarkIndexFailedAsync(primaryJobId, firstLease.LeaseOwner, "failed g1", false, TestContext.Current.CancellationToken);
            await service.QueueIndexAsync(document.Id, version.Id, [tag.Id], true, TestContext.Current.CancellationToken);
            database.ChangeTracker.Clear();
            var primary = await database.KnowledgeIndexJobs.SingleAsync(job => job.Id == primaryJobId, TestContext.Current.CancellationToken);
            generationTwo = primary.CollectionName;
            var cleanup = await database.KnowledgeIndexJobs.AsNoTracking().SingleAsync(job => job.Operation == "cleanup" &&
                job.KnowledgeDocumentId == document.Id && job.CollectionName == generationOne, TestContext.Current.CancellationToken);
            Assert.Equal(firstLease.Generation, cleanup.Generation);
            Assert.Equal(primaryJobId, cleanup.SourceIndexJobId);
            Assert.Equal(1, await database.KnowledgeIndexJobs.CountAsync(job => job.Operation == "cleanup" && job.CollectionName == generationOne,
                TestContext.Current.CancellationToken));
            database.ChangeTracker.Clear();
            await service.QueueIndexAsync(document.Id, version.Id, [tag.Id], true, TestContext.Current.CancellationToken);
            database.ChangeTracker.Clear();
            primary = await database.KnowledgeIndexJobs.SingleAsync(job => job.Id == primaryJobId, TestContext.Current.CancellationToken);
            generationThree = primary.CollectionName;
            Assert.Equal(1, await database.KnowledgeIndexJobs.CountAsync(job => job.Operation == "cleanup" && job.CollectionName == generationOne,
                TestContext.Current.CancellationToken));
            Assert.Equal(1, await database.KnowledgeIndexJobs.CountAsync(job => job.Operation == "cleanup" && job.CollectionName == generationTwo,
                TestContext.Current.CancellationToken));
            primary.Status = "failed";
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var cleanupWorker = new KnowledgeIndexWorker(provider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System,
            provider.GetRequiredService<KnowledgeIndexWorkerOptions>());
        Assert.True(await cleanupWorker.ProcessOnceAsync(TestContext.Current.CancellationToken));
        Assert.True(await cleanupWorker.ProcessOnceAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await vectors.InspectVersionAsync(new VectorCollection(generationOne, 3, VectorDistance.Cosine), versionId,
            TestContext.Current.CancellationToken));
        var thirdCollection = new VectorCollection(generationThree, 3, VectorDistance.Cosine);
        await vectors.EnsureCollectionAsync(thirdCollection, TestContext.Current.CancellationToken);
        await vectors.UpsertAsync(thirdCollection,
            [new VectorPoint(chunkId, documentId, versionId, [], [0, 1, 0], false, 3)], TestContext.Current.CancellationToken);
        await using (var deleteScope = provider.CreateAsyncScope())
            Assert.True(await deleteScope.ServiceProvider.GetRequiredService<IKnowledgeDocumentStore>()
                .RequestPhysicalDeleteAsync(documentId, TestContext.Current.CancellationToken));
        var physicalCleanup = new KnowledgeDocumentCleanupWorker(provider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
        Assert.True(await physicalCleanup.ProcessOnceAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await vectors.InspectVersionAsync(new VectorCollection(generationOne, 3, VectorDistance.Cosine), versionId,
            TestContext.Current.CancellationToken));
        Assert.Empty(await vectors.InspectVersionAsync(new VectorCollection(generationTwo, 3, VectorDistance.Cosine), versionId,
            TestContext.Current.CancellationToken));
        Assert.Empty(await vectors.InspectVersionAsync(thirdCollection, versionId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Disable_cleanup_insert_failure_rolls_back_document_version_and_index_job()
    {
        var plainOptions = new DbContextOptionsBuilder<WechatRobotDbContext>().UseMySQL(_fixture.ConnectionString).Options;
        Guid documentId;
        Guid versionId;
        Guid jobId;
        Guid tagId;
        await using (var setup = new WechatRobotDbContext(plainOptions))
        {
            await setup.Database.MigrateAsync(TestContext.Current.CancellationToken);
            await setup.KnowledgeIndexJobs.ExecuteUpdateAsync(setters => setters.SetProperty(job => job.Status, "completed"), TestContext.Current.CancellationToken);
            var document = new KnowledgeDocumentEntity
            {
                Status = "active", IsDeleteRequested = false, ActiveCollectionName = $"kb_cosine_3_disable_{Guid.NewGuid():N}",
                ActiveEmbeddingDimension = 3, ActiveDistance = "cosine", ActiveIndexGeneration = 1
            };
            var version = Version(document.Id, 1, "active", true);
            var tag = new KnowledgeTagEntity { Name = Guid.NewGuid().ToString("N"), NormalizedName = Guid.NewGuid().ToString("N") };
            var embedding = new ModelConfigEntity
            {
                Name = Guid.NewGuid().ToString("N"),
                NormalizedName = Guid.NewGuid().ToString("N"),
                Provider = "openai-compatible",
                ConfigurationType = "embedding",
                BaseUrl = "https://fake/",
                Model = "embedding-test",
                EmbeddingDimension = 3,
                IsEnabled = true,
                IsDefault = true
            };
            var chunk = new KnowledgeChunkEntity { KnowledgeDocumentVersionId = version.Id, Text = "disable", Status = "approved" };
            document.ActiveVersionId = version.Id;
            version.IndexCollectionName = document.ActiveCollectionName;
            version.EmbeddingDimension = 3;
            version.VectorDistance = "cosine";
            version.IndexGeneration = 1;
            var job = new KnowledgeIndexJobEntity
            {
                KnowledgeDocumentId = document.Id, KnowledgeDocumentVersionId = version.Id, Operation = "reindex", Status = "pending",
                CollectionName = $"kb_cosine_3_pending_{Guid.NewGuid():N}", Dimension = 3, Distance = "cosine"
            };
            documentId = document.Id;
            versionId = version.Id;
            jobId = job.Id;
            tagId = tag.Id;
            setup.AddRange(document, version, tag, embedding, chunk, job,
                new KnowledgeChunkTagEntity { KnowledgeChunkId = chunk.Id, KnowledgeTagId = tag.Id });
            await setup.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var failingOptions = new DbContextOptionsBuilder<WechatRobotDbContext>().UseMySQL(_fixture.ConnectionString)
            .AddInterceptors(new FailCleanupInsertInterceptor()).Options;
        await using (var failing = new WechatRobotDbContext(failingOptions))
            await Assert.ThrowsAnyAsync<Exception>(() => Service(failing).DisableAsync(documentId, TestContext.Current.CancellationToken));

        await using var verify = new WechatRobotDbContext(plainOptions);
        var storedDocument = await verify.KnowledgeDocuments.AsNoTracking().SingleAsync(document => document.Id == documentId, TestContext.Current.CancellationToken);
        var storedVersion = await verify.KnowledgeDocumentVersions.AsNoTracking().SingleAsync(version => version.Id == versionId, TestContext.Current.CancellationToken);
        var storedJob = await verify.KnowledgeIndexJobs.AsNoTracking().SingleAsync(job => job.Id == jobId, TestContext.Current.CancellationToken);
        Assert.Equal("active", storedDocument.Status);
        Assert.False(storedDocument.IsDeleteRequested);
        Assert.Equal(versionId, storedDocument.ActiveVersionId);
        Assert.Equal("active", storedVersion.Status);
        Assert.True(storedVersion.IsPublished);
        Assert.Equal("pending", storedJob.Status);
        Assert.DoesNotContain(await verify.KnowledgeIndexJobs.AsNoTracking().Where(job => job.KnowledgeDocumentId == documentId).ToArrayAsync(TestContext.Current.CancellationToken),
            job => job.Operation == "cleanup");
        await verify.DisposeAsync();

        await using (var success = new WechatRobotDbContext(plainOptions))
            await Service(success).DisableAsync(documentId, TestContext.Current.CancellationToken);
        await using (var disabled = new WechatRobotDbContext(plainOptions))
        {
            var disabledDocument = await disabled.KnowledgeDocuments.AsNoTracking().SingleAsync(document => document.Id == documentId, TestContext.Current.CancellationToken);
            Assert.Equal("disabled", disabledDocument.Status);
            Assert.False(disabledDocument.IsDeleteRequested);
            Assert.Null(disabledDocument.ActiveVersionId);
            Assert.Equal("disabled", (await disabled.KnowledgeDocumentVersions.AsNoTracking().SingleAsync(version => version.Id == versionId,
                TestContext.Current.CancellationToken)).Status);
            Assert.Equal("cancelled", (await disabled.KnowledgeIndexJobs.AsNoTracking().SingleAsync(job => job.Id == jobId,
                TestContext.Current.CancellationToken)).Status);
            Assert.Contains(await disabled.KnowledgeIndexJobs.AsNoTracking().Where(job => job.KnowledgeDocumentId == documentId).ToArrayAsync(TestContext.Current.CancellationToken),
                job => job.Operation == "cleanup" && job.CollectionName == disabledDocument.ActiveCollectionName);
            Assert.Contains(await disabled.KnowledgeIndexJobs.AsNoTracking().Where(job => job.KnowledgeDocumentId == documentId).ToArrayAsync(TestContext.Current.CancellationToken),
                job => job.Operation == "cleanup" && job.CollectionName.Contains("pending", StringComparison.Ordinal));
            var service = Service(disabled);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.QueueIndexAsync(documentId, versionId, [tagId], false,
                TestContext.Current.CancellationToken));
            await service.QueueIndexAsync(documentId, versionId, [tagId], true, TestContext.Current.CancellationToken);
        }
        await using var reenabled = new WechatRobotDbContext(plainOptions);
        Assert.Equal("indexing", (await reenabled.KnowledgeDocuments.AsNoTracking().SingleAsync(document => document.Id == documentId,
            TestContext.Current.CancellationToken)).Status);
        Assert.False((await reenabled.KnowledgeDocuments.AsNoTracking().SingleAsync(document => document.Id == documentId,
            TestContext.Current.CancellationToken)).IsDeleteRequested);
    }

    [Fact]
    public async Task Stale_explicit_reindex_read_before_physical_delete_cannot_commit_after_cleanup()
    {
        var plainOptions = new DbContextOptionsBuilder<WechatRobotDbContext>().UseMySQL(_fixture.ConnectionString).Options;
        Guid documentId;
        Guid versionId;
        Guid tagId;
        await using (var setup = new WechatRobotDbContext(plainOptions))
        {
            await setup.Database.MigrateAsync(TestContext.Current.CancellationToken);
            await setup.KnowledgeIndexJobs.ExecuteUpdateAsync(setters => setters.SetProperty(job => job.Status, "completed"), TestContext.Current.CancellationToken);
            await setup.DurableJobs.ExecuteUpdateAsync(setters => setters.SetProperty(job => job.Status, "completed"), TestContext.Current.CancellationToken);
            var tag = new KnowledgeTagEntity { Name = Guid.NewGuid().ToString("N"), NormalizedName = Guid.NewGuid().ToString("N") };
            var document = new KnowledgeDocumentEntity { Status = "uploaded" };
            var version = Version(document.Id, 1, "approved", false);
            var chunk = new KnowledgeChunkEntity { KnowledgeDocumentVersionId = version.Id, Text = "stale queue", Status = "approved" };
            documentId = document.Id;
            versionId = version.Id;
            tagId = tag.Id;
            setup.AddRange(tag, document, version, chunk);
            await setup.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var pause = new PauseAfterDocumentReadInterceptor();
        var staleOptions = new DbContextOptionsBuilder<WechatRobotDbContext>().UseMySQL(_fixture.ConnectionString).AddInterceptors(pause).Options;
        async Task<Guid> QueueStaleAsync()
        {
            await using var stale = new WechatRobotDbContext(staleOptions);
            return await Service(stale).QueueIndexAsync(documentId, versionId, [tagId], true, TestContext.Current.CancellationToken);
        }
        var staleQueue = QueueStaleAsync();
        await pause.ReadCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await using (var delete = new WechatRobotDbContext(plainOptions))
            Assert.True(await new KnowledgeDocumentStore(delete).RequestPhysicalDeleteAsync(documentId, TestContext.Current.CancellationToken));
        var cleanupServices = new ServiceCollection();
        cleanupServices.AddDbContext<WechatRobotDbContext>(builder => builder.UseMySQL(_fixture.ConnectionString));
        cleanupServices.AddSingleton(TimeProvider.System);
        cleanupServices.AddSingleton(new KnowledgeIndexOptions(3, VectorDistance.Cosine));
        cleanupServices.AddSingleton<ISecretProtector, PassThroughProtector>();
        cleanupServices.AddSingleton<IObjectStorage, RecordingStorage>();
        cleanupServices.AddSingleton<IVectorStore, MemoryVectorStore>();
        cleanupServices.AddScoped<IDurableJobRepository, DurableJobRepository>();
        cleanupServices.AddScoped<ModelConfigurationService>();
        cleanupServices.AddScoped<QdrantKnowledgeService>();
        await using var cleanupProvider = cleanupServices.BuildServiceProvider();
        var cleanup = new KnowledgeDocumentCleanupWorker(cleanupProvider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
        Assert.True(await cleanup.ProcessOnceAsync(TestContext.Current.CancellationToken));
        pause.Release.TrySetResult();

        Assert.NotNull(await Record.ExceptionAsync(async () => await staleQueue));
        await using var verify = new WechatRobotDbContext(plainOptions);
        Assert.False(await verify.KnowledgeDocuments.AnyAsync(item => item.Id == documentId, TestContext.Current.CancellationToken));
        Assert.False(await verify.KnowledgeDocumentVersions.AnyAsync(item => item.Id == versionId, TestContext.Current.CancellationToken));
        Assert.False(await verify.KnowledgeIndexJobs.AnyAsync(job => job.KnowledgeDocumentId == documentId, TestContext.Current.CancellationToken));
        Assert.Equal("completed", (await verify.DurableJobs.AsNoTracking().SingleAsync(job => job.JobType == "CleanupKnowledgeDocument" &&
            job.PayloadJson.Contains(documentId.ToString()), TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task Disable_cleanup_tombstones_generation_collection_before_crashed_late_upsert_is_released()
    {
        await using var qdrant = new ContainerBuilder("qdrant/qdrant:v1.18.2").WithPortBinding(6333, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(6333).ForPath("/readyz"))).Build();
        await qdrant.StartAsync(TestContext.Current.CancellationToken);
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{qdrant.GetMappedPublicPort(6333)}") };
        var realVectors = new QdrantVectorStore(http);
        var lateVectors = new ReleaseThenWriteVectorStore(realVectors);
        var services = new ServiceCollection();
        services.AddDbContext<WechatRobotDbContext>(builder => builder.UseMySQL(_fixture.ConnectionString));
        services.AddSingleton(new KnowledgeIndexOptions(3, VectorDistance.Cosine, 1, 2));
        services.AddSingleton(new KnowledgeIndexWorkerOptions(TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(40)));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ISecretProtector, PassThroughProtector>();
        services.AddSingleton<IEmbeddingClient, ImmediateEmbeddingClient>();
        services.AddSingleton<IVectorStore>(lateVectors);
        services.AddScoped<ModelConfigurationService>();
        services.AddScoped<QdrantKnowledgeService>();
        services.AddScoped<IKnowledgeService>(provider => provider.GetRequiredService<QdrantKnowledgeService>());
        services.AddScoped<KnowledgeIndexService>();
        await using var provider = services.BuildServiceProvider();
        Guid documentId;
        Guid versionId;
        string collection;
        await using (var setupScope = provider.CreateAsyncScope())
        {
            var database = setupScope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            await database.Database.MigrateAsync(TestContext.Current.CancellationToken);
            await database.ModelConfigs.Where(item => item.ConfigurationType == "embedding" && item.IsDefault)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsDefault, false), TestContext.Current.CancellationToken);
            await database.KnowledgeIndexJobs.ExecuteUpdateAsync(setters => setters.SetProperty(job => job.Status, "completed"), TestContext.Current.CancellationToken);
            var tag = new KnowledgeTagEntity { Name = Guid.NewGuid().ToString("N"), NormalizedName = Guid.NewGuid().ToString("N") };
            var document = new KnowledgeDocumentEntity { Status = "uploaded" };
            var version = Version(document.Id, 1, "approved", false);
            var chunk = new KnowledgeChunkEntity { KnowledgeDocumentVersionId = version.Id, Text = "late upsert", Status = "approved" };
            documentId = document.Id;
            versionId = version.Id;
            database.AddRange(tag, document, version, chunk,
                new ModelConfigEntity
                {
                    Name = Guid.NewGuid().ToString("N"), NormalizedName = Guid.NewGuid().ToString("N"), Provider = "openai-compatible", ConfigurationType = "embedding", BaseUrl = "https://fake/",
                    Model = "fake", EncryptedApiKey = "key", IsDefault = true, IsEnabled = true, EmbeddingDimension = 3
                });
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
            var service = setupScope.ServiceProvider.GetRequiredService<QdrantKnowledgeService>();
            await service.QueueIndexAsync(document.Id, version.Id, [tag.Id], false, TestContext.Current.CancellationToken);
            collection = (await database.KnowledgeIndexJobs.AsNoTracking().SingleAsync(job => job.KnowledgeDocumentId == document.Id && job.Operation == "index",
                TestContext.Current.CancellationToken)).CollectionName;
        }
        var indexWorker = new KnowledgeIndexWorker(provider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System,
            provider.GetRequiredService<KnowledgeIndexWorkerOptions>());
        var indexing = indexWorker.ProcessOnceAsync(TestContext.Current.CancellationToken);
        await lateVectors.UpsertEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await using (var disableScope = provider.CreateAsyncScope())
            await disableScope.ServiceProvider.GetRequiredService<QdrantKnowledgeService>().DisableAsync(documentId, TestContext.Current.CancellationToken);
        var cleanupWorker = new KnowledgeIndexWorker(provider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System,
            provider.GetRequiredService<KnowledgeIndexWorkerOptions>());
        var cleaning = cleanupWorker.ProcessOnceAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.True(await cleaning.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            using var deleted = await http.GetAsync($"/collections/{collection}", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);
        }
        finally { lateVectors.Release.TrySetResult(); }
        Assert.True(await indexing.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        using var stillDeleted = await http.GetAsync($"/collections/{collection}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, stillDeleted.StatusCode);
        Assert.Empty(await realVectors.InspectVersionAsync(new VectorCollection(collection, 3, VectorDistance.Cosine), versionId,
            TestContext.Current.CancellationToken));
        await using var verifyScope = provider.CreateAsyncScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        Assert.Equal("completed", (await verify.KnowledgeIndexJobs.AsNoTracking().SingleAsync(job => job.Operation == "cleanup" &&
            job.KnowledgeDocumentId == documentId && job.CollectionName == collection, TestContext.Current.CancellationToken)).Status);
        Assert.False(await cleanupWorker.ProcessOnceAsync(TestContext.Current.CancellationToken));
    }

    private static QdrantKnowledgeService Service(WechatRobotDbContext database) => new(database,
        new ModelConfigurationService(new PassThroughProtector()), new KnowledgeIndexOptions(3, VectorDistance.Cosine), TimeProvider.System);

    private static Guid StableIndexJobId(Guid versionId) => new(SHA256.HashData(Encoding.UTF8.GetBytes($"index:{versionId:N}")).AsSpan(0, 16));

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
    private sealed class ImmediateEmbeddingClient : IEmbeddingClient
    {
        public Task<EmbeddingBatchResponse> CreateEmbeddingsAsync(ModelProviderConfiguration configuration, EmbeddingBatchRequest request,
            CancellationToken token = default) => Task.FromResult(new EmbeddingBatchResponse(
                request.Inputs.Select(_ => (IReadOnlyList<float>)new float[] { 1, 0, 0 }).ToArray()));
    }
    private sealed class TagBEmbeddingClient : IEmbeddingClient
    {
        public Task<EmbeddingBatchResponse> CreateEmbeddingsAsync(ModelProviderConfiguration configuration, EmbeddingBatchRequest request,
            CancellationToken token = default) => Task.FromResult(new EmbeddingBatchResponse(
                request.Inputs.Select(_ => (IReadOnlyList<float>)new float[] { 0, 1, 0 }).ToArray()));
    }
    private sealed class WriteThenBlockVectorStore(IVectorStore inner) : IVectorStore
    {
        public TaskCompletionSource WriteCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task EnsureCollectionAsync(VectorCollection collection, CancellationToken token) => inner.EnsureCollectionAsync(collection, token);
        public async Task UpsertAsync(VectorCollection collection, IReadOnlyList<VectorPoint> points, CancellationToken token)
        {
            await inner.UpsertAsync(collection, points, token);
            WriteCompleted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
        public Task SetVersionActiveAsync(VectorCollection collection, Guid versionId, bool active, CancellationToken token) => inner.SetVersionActiveAsync(collection, versionId, active, token);
        public Task DeleteCollectionAsync(VectorCollection collection, CancellationToken token) => inner.DeleteCollectionAsync(collection, token);
        public Task<VectorCollection?> InspectCollectionAsync(string collectionName, CancellationToken token) => inner.InspectCollectionAsync(collectionName, token);
        public Task DeleteVersionAsync(VectorCollection collection, Guid versionId, CancellationToken token) => inner.DeleteVersionAsync(collection, versionId, token);
        public Task<IReadOnlyList<VectorPointMetadata>> InspectVersionAsync(VectorCollection collection, Guid versionId, CancellationToken token) => inner.InspectVersionAsync(collection, versionId, token);
        public Task<IReadOnlyList<VectorSearchHit>> SearchAsync(VectorSearchRequest request, CancellationToken token) => inner.SearchAsync(request, token);
    }
    private sealed class RecordingStorage : IObjectStorage
    {
        public List<string> Deleted { get; } = [];
        public Task DeleteAsync(string objectKey, CancellationToken token) { Deleted.Add(objectKey); return Task.CompletedTask; }
        public Task<StoredObject> PutAsync(string objectKey, Stream content, string contentType, CancellationToken token) => throw new NotSupportedException();
    }
    private sealed class FailCleanupInsertInterceptor : DbCommandInterceptor
    {
        private static bool ShouldFail(DbCommand command) => command.CommandText.Contains("INSERT INTO `knowledge_index_job`", StringComparison.OrdinalIgnoreCase);
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken token = default) => ShouldFail(command)
            ? ValueTask.FromException<InterceptionResult<int>>(new InvalidOperationException("injected cleanup insert failure"))
            : ValueTask.FromResult(result);
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken token = default) => ShouldFail(command)
            ? ValueTask.FromException<InterceptionResult<DbDataReader>>(new InvalidOperationException("injected cleanup insert failure"))
            : ValueTask.FromResult(result);
    }
    private sealed class PauseAfterDocumentReadInterceptor : DbCommandInterceptor
    {
        private int _paused;
        public TaskCompletionSource ReadCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken token = default)
        {
            if (command.CommandText.Contains("knowledge_document", StringComparison.OrdinalIgnoreCase) && Interlocked.CompareExchange(ref _paused, 1, 0) == 0)
            {
                ReadCompleted.TrySetResult();
                await Release.Task.WaitAsync(token);
            }
            return result;
        }
    }
    private sealed class ReleaseThenWriteVectorStore(IVectorStore inner) : IVectorStore
    {
        public TaskCompletionSource UpsertEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task EnsureCollectionAsync(VectorCollection collection, CancellationToken token) => inner.EnsureCollectionAsync(collection, token);
        public async Task UpsertAsync(VectorCollection collection, IReadOnlyList<VectorPoint> points, CancellationToken token)
        {
            UpsertEntered.TrySetResult();
            await Release.Task;
            await inner.UpsertAsync(collection, points, CancellationToken.None);
        }
        public Task SetVersionActiveAsync(VectorCollection collection, Guid versionId, bool active, CancellationToken token) => inner.SetVersionActiveAsync(collection, versionId, active, token);
        public Task DeleteCollectionAsync(VectorCollection collection, CancellationToken token) => inner.DeleteCollectionAsync(collection, token);
        public Task<VectorCollection?> InspectCollectionAsync(string collectionName, CancellationToken token) => inner.InspectCollectionAsync(collectionName, token);
        public Task DeleteVersionAsync(VectorCollection collection, Guid versionId, CancellationToken token) => inner.DeleteVersionAsync(collection, versionId, token);
        public Task<IReadOnlyList<VectorPointMetadata>> InspectVersionAsync(VectorCollection collection, Guid versionId, CancellationToken token) => inner.InspectVersionAsync(collection, versionId, token);
        public Task<IReadOnlyList<VectorSearchHit>> SearchAsync(VectorSearchRequest request, CancellationToken token) => inner.SearchAsync(request, token);
    }
    private sealed class MemoryVectorStore : IVectorStore
    {
        public Task EnsureCollectionAsync(VectorCollection collection, CancellationToken token) => Task.CompletedTask;
        public Task UpsertAsync(VectorCollection collection, IReadOnlyList<VectorPoint> points, CancellationToken token) => Task.CompletedTask;
        public Task SetVersionActiveAsync(VectorCollection collection, Guid versionId, bool active, CancellationToken token) => Task.CompletedTask;
        public Task DeleteCollectionAsync(VectorCollection collection, CancellationToken token) => Task.CompletedTask;
        public Task<VectorCollection?> InspectCollectionAsync(string collectionName, CancellationToken token) => Task.FromResult<VectorCollection?>(null);
        public Task DeleteVersionAsync(VectorCollection collection, Guid versionId, CancellationToken token) => Task.CompletedTask;
        public Task<IReadOnlyList<VectorPointMetadata>> InspectVersionAsync(VectorCollection collection, Guid versionId, CancellationToken token) => Task.FromResult<IReadOnlyList<VectorPointMetadata>>([]);
        public Task<IReadOnlyList<VectorSearchHit>> SearchAsync(VectorSearchRequest request, CancellationToken token) => Task.FromResult<IReadOnlyList<VectorSearchHit>>([]);
    }
}
