using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
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
    public async Task Upload_completion_does_not_require_bulk_update_support()
    {
        await using var provider = CreateProviderBoundaryServices().BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var store = new KnowledgeDocumentStore(database);
        var pending = await store.StageAsync(
            new DocumentStageRequest(
                null,
                "provider-upload.txt",
                new ValidatedDocument(
                    "provider upload"u8.ToArray(),
                    "d".PadLeft(64, '0'),
                    "provider-upload.txt",
                    "text/plain")),
            TestContext.Current.CancellationToken);
        Assert.NotNull(pending);

        var completed = await store.MarkUploadedAsync(
            pending,
            new StoredObject(
                pending.ObjectKey,
                new Uri($"https://public.example.test/{pending.ObjectKey}")),
            TestContext.Current.CancellationToken);

        Assert.True(completed);
        Assert.Equal(
            "uploaded",
            (await database.KnowledgeDocumentVersions.SingleAsync(
                item => item.Id == pending.VersionId,
                TestContext.Current.CancellationToken)).Status);
        Assert.Equal(
            "pending",
            (await database.DurableJobs.SingleAsync(
                job =>
                    job.JobType == "ParseKnowledgeDocument" &&
                    job.PayloadJson.Contains(pending.VersionId.ToString()),
                TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task Upload_failure_does_not_require_bulk_update_support()
    {
        await using var provider = CreateProviderBoundaryServices().BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var store = new KnowledgeDocumentStore(database);
        var pending = await store.StageAsync(
            new DocumentStageRequest(
                null,
                "provider-failure.txt",
                new ValidatedDocument(
                    "provider failure"u8.ToArray(),
                    "e".PadLeft(64, '0'),
                    "provider-failure.txt",
                    "text/plain")),
            TestContext.Current.CancellationToken);
        Assert.NotNull(pending);

        await store.MarkFailedAsync(
            pending,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            "failed",
            (await database.KnowledgeDocumentVersions.SingleAsync(
                item => item.Id == pending.VersionId,
                TestContext.Current.CancellationToken)).Status);
        var uploadJob = await database.DurableJobs.SingleAsync(
            job =>
                job.JobType == "UploadKnowledgeDocument" &&
                job.PayloadJson.Contains(pending.VersionId.ToString()),
            TestContext.Current.CancellationToken);
        Assert.Equal("retrying", uploadJob.Status);
        Assert.Equal(1, uploadJob.AttemptCount);
    }

    [Fact]
    public async Task Physical_delete_request_does_not_require_bulk_update_support()
    {
        var documentId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var services = new ServiceCollection();
        services.AddDbContext<WechatRobotDbContext>(builder =>
            builder.UseInMemoryDatabase(Guid.NewGuid().ToString())
                .ReplaceService<IDatabaseProvider, ProviderWithoutBulkUpdateSupport>()
                .ConfigureWarnings(warnings =>
                    warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        database.AddRange(
            new KnowledgeDocumentEntity
            {
                Id = documentId,
                Title = "provider boundary",
                Status = "uploaded",
                ActiveVersionId = versionId,
                StateVersion = 6
            },
            new KnowledgeDocumentVersionEntity
            {
                Id = versionId,
                KnowledgeDocumentId = documentId,
                Version = 1,
                OriginalFileName = "provider-boundary.txt",
                SafeFileName = "provider-boundary.txt",
                ContentType = "text/plain",
                Sha256 = "a".PadLeft(64, '0'),
                ObjectKey = "wechatrobot/knowledge/provider-boundary.txt",
                Status = "uploaded",
                IsPublished = true
            });
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);

        var accepted = await new KnowledgeDocumentStore(database)
            .RequestPhysicalDeleteAsync(
                documentId,
                6,
                "admin",
                TestContext.Current.CancellationToken);

        Assert.True(accepted);
        var document = await database.KnowledgeDocuments.SingleAsync(
            item => item.Id == documentId,
            TestContext.Current.CancellationToken);
        Assert.True(document.IsDeleteRequested);
        Assert.Equal("disabled", document.Status);
        Assert.Null(document.ActiveVersionId);
        Assert.Equal(7, document.StateVersion);
        Assert.True(await database.DurableJobs.AnyAsync(
            item => item.Id == KnowledgeDocumentCleanupJobIdentity.Create(documentId),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Physical_delete_retry_does_not_require_bulk_update_support()
    {
        var documentId = Guid.NewGuid();
        var cleanupJobId = KnowledgeDocumentCleanupJobIdentity.Create(documentId);
        var services = new ServiceCollection();
        services.AddDbContext<WechatRobotDbContext>(builder =>
            builder.UseInMemoryDatabase(Guid.NewGuid().ToString())
                .ReplaceService<IDatabaseProvider, ProviderWithoutBulkUpdateSupport>()
                .ConfigureWarnings(warnings =>
                    warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        database.AddRange(
            new KnowledgeDocumentEntity
            {
                Id = documentId,
                Title = "provider retry boundary",
                Status = "disabled",
                IsDeleteRequested = true,
                StateVersion = 3
            },
            new DurableJobEntity
            {
                Id = cleanupJobId,
                JobType = "CleanupKnowledgeDocument",
                Status = "deadLetter",
                AttemptCount = 4,
                PayloadJson = JsonSerializer.Serialize(new { documentId })
            },
            new DeadLetterEntity
            {
                DurableJobId = cleanupJobId,
                Reason = "sanitized cleanup failure",
                PayloadJson = "{}"
            });
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);

        var accepted = await new KnowledgeDocumentStore(database)
            .RequestPhysicalDeleteAsync(
                documentId,
                3,
                "admin",
                TestContext.Current.CancellationToken);

        Assert.True(accepted);
        var cleanup = await database.DurableJobs.SingleAsync(
            item => item.Id == cleanupJobId,
            TestContext.Current.CancellationToken);
        Assert.Equal("pending", cleanup.Status);
        Assert.Equal(0, cleanup.AttemptCount);
        Assert.Null(cleanup.CompletedAtUtc);
        Assert.False(await database.DeadLetters.AnyAsync(
            item => item.DurableJobId == cleanupJobId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Completed_legacy_cleanup_is_recovered_without_bulk_update_support()
    {
        var documentId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var versionId = Guid.NewGuid();
        var cleanupJobId = Guid.Parse("03373499-eb61-6c73-6bd0-aedea0036746");
        var storage = new FakeStorage();
        var services = new ServiceCollection();
        var databaseName = Guid.NewGuid().ToString();
        services.AddDbContext<WechatRobotDbContext>(builder =>
            builder.UseInMemoryDatabase(databaseName)
                .ReplaceService<IDatabaseProvider, ProviderWithoutBulkUpdateSupport>()
                .ConfigureWarnings(warnings =>
                    warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        services.AddScoped<IDurableJobRepository, InMemoryCleanupJobs>();
        services.AddSingleton<IObjectStorage>(storage);
        services.AddSingleton<IVectorStore>(new FakeVectors());
        services.AddSingleton(new KnowledgeIndexOptions(3, VectorDistance.Cosine));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ISecretProtector, PassThroughProtector>();
        services.AddScoped<ModelConfigurationService>();
        services.AddScoped<QdrantKnowledgeService>();
        await using var provider = services.BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            database.AddRange(
                new KnowledgeDocumentEntity
                {
                    Id = documentId,
                    Status = "disabled",
                    IsDeleteRequested = true
                },
                new KnowledgeDocumentVersionEntity
                {
                    Id = versionId,
                    KnowledgeDocumentId = documentId,
                    Version = 1,
                    OriginalFileName = "legacy.txt",
                    SafeFileName = "legacy.txt",
                    ContentType = "text/plain",
                    Sha256 = "c".PadLeft(64, '0'),
                    ObjectKey = "wechatrobot/knowledge/legacy.txt",
                    Status = "disabled"
                },
                new DurableJobEntity
                {
                    Id = cleanupJobId,
                    JobType = "CleanupKnowledgeDocument",
                    Status = "completed",
                    PayloadJson = JsonSerializer.Serialize(new { documentId }),
                    CompletedAtUtc = DateTime.UtcNow.AddMinutes(-5)
                });
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var worker = new KnowledgeDocumentCleanupWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System);

        Assert.True(await worker.ProcessOnceAsync(
            TestContext.Current.CancellationToken));

        await using var verifyScope = provider.CreateAsyncScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        Assert.False(await verify.KnowledgeDocuments.AnyAsync(
            item => item.Id == documentId,
            TestContext.Current.CancellationToken));
        Assert.Equal(
            "completed",
            (await verify.DurableJobs.SingleAsync(
                item => item.Id == cleanupJobId,
                TestContext.Current.CancellationToken)).Status);
        Assert.Equal(["wechatrobot/knowledge/legacy.txt"], storage.Deleted);
    }

    private sealed class ProviderWithoutBulkUpdateSupport : IDatabaseProvider
    {
        public string Name => "ProviderWithoutBulkUpdateSupport";

        public bool IsConfigured(IDbContextOptions options) => true;
    }

    private static ServiceCollection CreateProviderBoundaryServices()
    {
        var services = new ServiceCollection();
        services.AddDbContext<WechatRobotDbContext>(builder =>
            builder.UseInMemoryDatabase(Guid.NewGuid().ToString())
                .ReplaceService<IDatabaseProvider, ProviderWithoutBulkUpdateSupport>()
                .ConfigureWarnings(warnings =>
                    warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        return services;
    }

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
            var candidate = new KnowledgeCandidateEntity
            {
                KnowledgeDocumentVersionId = versionId,
                Question = "question",
                Answer = "answer",
                EvidenceJson = "{}",
                Status = "published"
            };
            database.AddRange(document, version, candidate, new KnowledgeIndexJobEntity
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
        await using (var verifyScope = provider.CreateAsyncScope())
        {
            var database = verifyScope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            Assert.False(await database.KnowledgeDocuments.AnyAsync(item => item.Id == documentId, TestContext.Current.CancellationToken));
            Assert.False(await database.KnowledgeDocumentVersions.AnyAsync(item => item.Id == versionId, TestContext.Current.CancellationToken));
            Assert.Null((await database.KnowledgeCandidates.SingleAsync(TestContext.Current.CancellationToken)).KnowledgeDocumentVersionId);
        }
        Assert.False(await worker.ProcessOnceAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Failed_external_cleanup_keeps_database_records_and_fails_job()
    {
        var documentId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var job = new LeasedDurableJob(Guid.NewGuid(), "CleanupKnowledgeDocument", JsonSerializer.Serialize(new { documentId }), 0, "cleanup-owner");
        var jobs = new FakeJobs(job);
        var storage = new FakeStorage { FailDelete = true };
        var services = new ServiceCollection();
        var databaseName = Guid.NewGuid().ToString();
        services.AddDbContext<WechatRobotDbContext>(builder => builder.UseInMemoryDatabase(databaseName));
        services.AddSingleton<IDurableJobRepository>(jobs);
        services.AddSingleton<IObjectStorage>(storage);
        services.AddSingleton<IVectorStore>(new FakeVectors());
        services.AddSingleton(new KnowledgeIndexOptions(3, VectorDistance.Cosine));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ISecretProtector, PassThroughProtector>();
        services.AddScoped<ModelConfigurationService>();
        services.AddScoped<QdrantKnowledgeService>();
        await using var provider = services.BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            database.AddRange(
                new KnowledgeDocumentEntity { Id = documentId, Status = "disabled", IsDeleteRequested = true },
                new KnowledgeDocumentVersionEntity
                {
                    Id = versionId,
                    KnowledgeDocumentId = documentId,
                    Version = 1,
                    OriginalFileName = "retained.txt",
                    SafeFileName = "retained.txt",
                    ContentType = "text/plain",
                    Sha256 = "b".PadLeft(64, '0'),
                    ObjectKey = "wechatrobot/knowledge/retained.txt",
                    Status = "disabled",
                    IndexCollectionName = "kb_cosine_3_g1",
                    EmbeddingDimension = 3,
                    VectorDistance = "cosine",
                    IndexGeneration = 1
                },
                new KnowledgeIndexJobEntity
                {
                    KnowledgeDocumentId = documentId,
                    KnowledgeDocumentVersionId = versionId,
                    CollectionName = "kb_cosine_3_g2",
                    Dimension = 3,
                    Distance = "cosine",
                    Generation = 1,
                    Status = "failed"
                });
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var worker = new KnowledgeDocumentCleanupWorker(provider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
        Assert.True(await worker.ProcessOnceAsync(TestContext.Current.CancellationToken));

        Assert.Single(storage.Deleted);
        Assert.True(jobs.Failed);
        Assert.False(jobs.Completed);
        await using var verifyScope = provider.CreateAsyncScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        Assert.True(await verify.KnowledgeDocuments.AnyAsync(item => item.Id == documentId, TestContext.Current.CancellationToken));
        Assert.True(await verify.KnowledgeDocumentVersions.AnyAsync(item => item.Id == versionId, TestContext.Current.CancellationToken));
    }

    private sealed class FakeStorage : IObjectStorage
    {
        public List<string> Deleted { get; } = [];
        public bool FailDelete { get; init; }
        public Task DeleteAsync(string objectKey, CancellationToken token)
        {
            Deleted.Add(objectKey);
            return FailDelete
                ? Task.FromException(new IOException("simulated object-storage delete failure"))
                : Task.CompletedTask;
        }
        public Task<StoredObject> PutAsync(string objectKey, Stream content, string contentType, CancellationToken token) => throw new NotSupportedException();
    }
    private sealed class InMemoryCleanupJobs(
        WechatRobotDbContext database) : IDurableJobRepository
    {
        public async Task<LeasedDurableJob?> LeaseNextJobAsync(
            string type,
            string owner,
            DateTime now,
            TimeSpan duration,
            CancellationToken token)
        {
            var job = await database.DurableJobs.SingleOrDefaultAsync(
                item =>
                    item.JobType == type &&
                    (item.Status == "pending" ||
                     item.Status == "retrying") &&
                    item.NextAttemptAtUtc <= now,
                token);
            if (job is null)
                return null;
            job.Status = "leased";
            job.LeaseOwner = owner;
            job.LeaseExpiresAtUtc = now.Add(duration);
            job.Version++;
            await database.SaveChangesAsync(token);
            return new(
                job.Id,
                job.JobType,
                job.PayloadJson,
                job.AttemptCount,
                owner);
        }

        public async Task CompleteJobAsync(
            Guid id,
            string owner,
            DateTime at,
            CancellationToken token)
        {
            var job = await database.DurableJobs.SingleAsync(
                item =>
                    item.Id == id &&
                    item.Status == "leased" &&
                    item.LeaseOwner == owner,
                token);
            job.Status = "completed";
            job.CompletedAtUtc = at;
            job.LeaseOwner = null;
            job.LeaseExpiresAtUtc = null;
            job.Version++;
            await database.SaveChangesAsync(token);
        }

        public Task FailJobAsync(
            LeasedDurableJob job,
            string reason,
            DateTime at,
            CancellationToken token) =>
            throw new Xunit.Sdk.XunitException(reason);

        public Task<InboundMessageIngestResult> IngestInboundMessageAsync(
            InboundMessageIngestRequest request,
            CancellationToken token) =>
            throw new NotSupportedException();
        public Task<EnqueueSendCommandResult> EnqueueSendCommandAsync(
            EnqueueSendCommandRequest request,
            CancellationToken token) =>
            throw new NotSupportedException();
        public Task<LeasedSendCommand?> LeaseNextSendCommandAsync(
            string owner,
            DateTime now,
            TimeSpan duration,
            CancellationToken token) =>
            throw new NotSupportedException();
        public Task<bool> MarkSendDispatchingAsync(
            LeasedSendCommand command,
            DateTime dispatchedAtUtc,
            CancellationToken token) =>
            throw new NotSupportedException();
        public Task MarkSendDeliveryUnknownAsync(
            LeasedSendCommand command,
            string reason,
            DateTime failedAtUtc,
            CancellationToken token) =>
            throw new NotSupportedException();
        public Task MarkSendAcceptedAsync(
            LeasedSendCommand command,
            string workToolMessageId,
            DateTime at,
            CancellationToken token) =>
            throw new NotSupportedException();
        public Task MarkSendRejectedAsync(
            LeasedSendCommand command,
            string reason,
            DateTime at,
            CancellationToken token) =>
            throw new NotSupportedException();
        public Task FailSendCommandAsync(
            LeasedSendCommand command,
            string reason,
            DateTime at,
            TimeSpan? delay,
            CancellationToken token) =>
            throw new NotSupportedException();
        public Task<bool> RenewSendLeasesAsync(
            LeasedSendCommand command,
            DateTime now,
            TimeSpan duration,
            CancellationToken token) =>
            throw new NotSupportedException();
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
        public Task<IReadOnlyList<VectorPointMetadata>> InspectVersionAsync(VectorCollection collection, Guid versionId, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<VectorPointMetadata>>([]);
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
