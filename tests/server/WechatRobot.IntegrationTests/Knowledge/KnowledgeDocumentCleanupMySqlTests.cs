using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WechatRobot.Application.Conversations;
using WechatRobot.Application.Handoffs;
using WechatRobot.Application.Jobs;
using WechatRobot.Application.Knowledge;
using WechatRobot.Application.Models;
using WechatRobot.Application.Security;
using WechatRobot.Application.Storage;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.Infrastructure.Knowledge;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.IntegrationTests.Infrastructure;
using WechatRobot.Worker.Jobs;

namespace WechatRobot.IntegrationTests.Knowledge;

public sealed class KnowledgeDocumentCleanupMySqlTests(MySqlFixture fixture) : IClassFixture<MySqlFixture>
{
    [Fact]
    public async Task Cleanup_detaches_candidate_cascades_document_data_and_releases_sha256()
    {
        var token = TestContext.Current.CancellationToken;
        var options = new DbContextOptionsBuilder<WechatRobotDbContext>()
            .UseMySQL(fixture.ConnectionString)
            .Options;
        var document = new KnowledgeDocumentEntity
        {
            Title = "physical-delete",
            Status = "disabled",
            IsDeleteRequested = true
        };
        var version = new KnowledgeDocumentVersionEntity
        {
            KnowledgeDocumentId = document.Id,
            Version = 1,
            OriginalFileName = "physical-delete.txt",
            SafeFileName = "physical-delete.txt",
            ContentType = "text/plain",
            Sha256 = "c".PadLeft(64, '0'),
            ObjectKey = "wechatrobot/knowledge/physical-delete.txt",
            Status = "disabled",
            IndexCollectionName = "kb_cosine_3_physical_delete",
            EmbeddingDimension = 3,
            VectorDistance = "cosine",
            IndexGeneration = 1
        };
        var chunk = new KnowledgeChunkEntity
        {
            KnowledgeDocumentVersionId = version.Id,
            Text = "retained candidate source",
            Status = "approved"
        };
        var tag = new KnowledgeTagEntity
        {
            Name = "physical-delete-" + Guid.NewGuid().ToString("N"),
            NormalizedName = "PHYSICAL-DELETE-" + Guid.NewGuid().ToString("N")
        };
        var reviewer = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "physical-delete-" + Guid.NewGuid().ToString("N"),
            NormalizedUserName = "PHYSICAL-DELETE-" + Guid.NewGuid().ToString("N"),
            SecurityStamp = Guid.NewGuid().ToString()
        };
        var robot = new RobotConfigEntity
        {
            Name = "physical-delete-" + Guid.NewGuid().ToString("N"),
            WorkToolRobotId = Guid.NewGuid().ToString("N"),
            CallbackSecretHash = "hash"
        };
        var group = new GroupProfileEntity
        {
            RobotConfigId = robot.Id,
            ExternalGroupId = Guid.NewGuid().ToString("N"),
            Name = "physical-delete-group"
        };
        var message = new ConversationMessageEntity
        {
            RobotConfigId = robot.Id,
            GroupProfileId = group.Id,
            GroupName = group.Name,
            SenderDisplayName = "customer",
            Text = "question",
            FallbackHash = Guid.NewGuid().ToString("N")
        };

        Guid candidateId;
        await using (var setup = new WechatRobotDbContext(options))
        {
            await setup.Database.MigrateAsync(token);
            setup.AddRange(
                document,
                version,
                chunk,
                tag,
                reviewer,
                robot,
                group,
                message,
                new KnowledgeChunkTagEntity { KnowledgeChunkId = chunk.Id, KnowledgeTagId = tag.Id },
                new KnowledgeChunkPreviewEntity
                {
                    KnowledgeDocumentVersionId = version.Id,
                    Text = "preview"
                },
                new KnowledgeOcrPageEntity
                {
                    KnowledgeDocumentVersionId = version.Id,
                    PageNumber = 1,
                    Status = "completed"
                },
                new KnowledgeIndexJobEntity
                {
                    KnowledgeDocumentId = document.Id,
                    KnowledgeDocumentVersionId = version.Id,
                    CollectionName = "kb_cosine_3_physical_delete_pending",
                    Dimension = 3,
                    Distance = "cosine",
                    Status = "failed"
                });
            await setup.SaveChangesAsync(token);

            var handoffs = new HandoffService(new EfHandoffStore(setup), TimeProvider.System);
            var started = await handoffs.StartAsync(
                new StartHandoffCommand(
                    message.Id,
                    robot.Id,
                    group.Id,
                    robot.WorkToolRobotId,
                    group.Name,
                    "explicit_transfer",
                    "[]",
                    HandoffPauseScope.Group,
                    null,
                    reviewer.Id,
                    reviewer.UserName!,
                    "physical-delete-mysql"),
                token);
            var resolved = await handoffs.ResolveAsync(
                started.Id,
                reviewer.Id,
                "retained answer",
                started.Version,
                token);
            candidateId = resolved.Id;
            var candidate = await setup.KnowledgeCandidates.SingleAsync(item => item.Id == candidateId, token);
            candidate.KnowledgeDocumentVersionId = version.Id;
            setup.KnowledgeReviews.Add(new KnowledgeReviewEntity
            {
                KnowledgeCandidateId = candidateId,
                ReviewerUserId = reviewer.Id,
                Decision = "approve",
                TagIdsJson = "[]",
                IdempotencyKey = "physical-delete-review-" + Guid.NewGuid().ToString("N")
            });
            await setup.SaveChangesAsync(token);
        }

        var leased = new LeasedDurableJob(
            Guid.NewGuid(),
            "CleanupKnowledgeDocument",
            JsonSerializer.Serialize(new { documentId = document.Id }),
            0,
            "cleanup-owner");
        var jobs = new FakeJobs(leased);
        var services = new ServiceCollection();
        services.AddDbContext<WechatRobotDbContext>(builder => builder.UseMySQL(fixture.ConnectionString));
        services.AddSingleton<IDurableJobRepository>(jobs);
        services.AddSingleton<IObjectStorage, FakeStorage>();
        services.AddSingleton<IVectorStore, FakeVectors>();
        services.AddSingleton(new KnowledgeIndexOptions(3, VectorDistance.Cosine));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ISecretProtector, PassThroughProtector>();
        services.AddScoped<ModelConfigurationService>();
        services.AddScoped<QdrantKnowledgeService>();
        await using var provider = services.BuildServiceProvider();
        var worker = new KnowledgeDocumentCleanupWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System);

        Assert.True(await worker.ProcessOnceAsync(token));
        Assert.True(jobs.Completed);
        Assert.False(jobs.Failed);

        await using var verify = new WechatRobotDbContext(options);
        Assert.False(await verify.KnowledgeDocuments.AnyAsync(item => item.Id == document.Id, token));
        Assert.False(await verify.KnowledgeDocumentVersions.AnyAsync(item => item.Id == version.Id, token));
        Assert.False(await verify.KnowledgeChunks.AnyAsync(item => item.KnowledgeDocumentVersionId == version.Id, token));
        Assert.False(await verify.KnowledgeChunkPreviews.AnyAsync(item => item.KnowledgeDocumentVersionId == version.Id, token));
        Assert.False(await verify.KnowledgeOcrPages.AnyAsync(item => item.KnowledgeDocumentVersionId == version.Id, token));
        Assert.False(await verify.KnowledgeChunkTags.AnyAsync(item => item.KnowledgeChunkId == chunk.Id, token));
        Assert.False(await verify.KnowledgeIndexJobs.AnyAsync(item => item.KnowledgeDocumentId == document.Id, token));
        Assert.Null((await verify.KnowledgeCandidates.SingleAsync(item => item.Id == candidateId, token)).KnowledgeDocumentVersionId);
        Assert.True(await verify.KnowledgeReviews.AnyAsync(item => item.KnowledgeCandidateId == candidateId, token));

        var replacement = new KnowledgeDocumentEntity { Title = "replacement", Status = "uploading" };
        verify.AddRange(
            replacement,
            new KnowledgeDocumentVersionEntity
            {
                KnowledgeDocumentId = replacement.Id,
                Version = 1,
                OriginalFileName = "replacement.txt",
                SafeFileName = "replacement.txt",
                ContentType = "text/plain",
                Sha256 = version.Sha256,
                ObjectKey = "wechatrobot/knowledge/replacement.txt"
            });
        await verify.SaveChangesAsync(token);
    }

    private sealed class FakeStorage : IObjectStorage
    {
        public Task DeleteAsync(string objectKey, CancellationToken token) => Task.CompletedTask;
        public Task<StoredObject> PutAsync(string objectKey, Stream content, string contentType, CancellationToken token) =>
            throw new NotSupportedException();
    }

    private sealed class FakeVectors : IVectorStore
    {
        public Task DeleteVersionAsync(VectorCollection collection, Guid versionId, CancellationToken token) => Task.CompletedTask;
        public Task DeleteCollectionAsync(VectorCollection collection, CancellationToken token) => Task.CompletedTask;
        public Task<VectorCollection?> InspectCollectionAsync(string collectionName, CancellationToken token) =>
            Task.FromResult<VectorCollection?>(null);
        public Task EnsureCollectionAsync(VectorCollection collection, CancellationToken token) => Task.CompletedTask;
        public Task UpsertAsync(VectorCollection collection, IReadOnlyList<VectorPoint> points, CancellationToken token) => Task.CompletedTask;
        public Task SetVersionActiveAsync(VectorCollection collection, Guid versionId, bool active, CancellationToken token) => Task.CompletedTask;
        public Task<IReadOnlyList<VectorPointMetadata>> InspectVersionAsync(VectorCollection collection, Guid versionId, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<VectorPointMetadata>>([]);
        public Task<IReadOnlyList<VectorSearchHit>> SearchAsync(VectorSearchRequest request, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<VectorSearchHit>>([]);
    }

    private sealed class FakeJobs(LeasedDurableJob job) : IDurableJobRepository
    {
        private bool _leased;
        public bool Completed { get; private set; }
        public bool Failed { get; private set; }

        public Task<LeasedDurableJob?> LeaseNextJobAsync(
            string type,
            string owner,
            DateTime now,
            TimeSpan duration,
            CancellationToken token)
        {
            if (_leased) return Task.FromResult<LeasedDurableJob?>(null);
            _leased = true;
            return Task.FromResult<LeasedDurableJob?>(job);
        }

        public Task CompleteJobAsync(Guid id, string owner, DateTime at, CancellationToken token)
        {
            Completed = true;
            return Task.CompletedTask;
        }

        public Task FailJobAsync(LeasedDurableJob value, string reason, DateTime at, CancellationToken token)
        {
            Failed = true;
            return Task.CompletedTask;
        }

        public Task<InboundMessageIngestResult> IngestInboundMessageAsync(InboundMessageIngestRequest request, CancellationToken token) =>
            throw new NotSupportedException();
        public Task<EnqueueSendCommandResult> EnqueueSendCommandAsync(EnqueueSendCommandRequest request, CancellationToken token) =>
            throw new NotSupportedException();
        public Task<LeasedSendCommand?> LeaseNextSendCommandAsync(string owner, DateTime now, TimeSpan duration, CancellationToken token) =>
            throw new NotSupportedException();
        public Task<bool> MarkSendDispatchingAsync(LeasedSendCommand command, DateTime dispatchedAtUtc, CancellationToken token) =>
            throw new NotSupportedException();
        public Task MarkSendDeliveryUnknownAsync(LeasedSendCommand command, string reason, DateTime failedAtUtc, CancellationToken token) =>
            throw new NotSupportedException();
        public Task MarkSendAcceptedAsync(LeasedSendCommand command, string workToolMessageId, DateTime at, CancellationToken token) =>
            throw new NotSupportedException();
        public Task MarkSendRejectedAsync(LeasedSendCommand command, string reason, DateTime at, CancellationToken token) =>
            throw new NotSupportedException();
        public Task FailSendCommandAsync(LeasedSendCommand command, string reason, DateTime at, TimeSpan? delay, CancellationToken token) =>
            throw new NotSupportedException();
        public Task<bool> RenewSendLeasesAsync(LeasedSendCommand command, DateTime now, TimeSpan duration, CancellationToken token) =>
            throw new NotSupportedException();
    }

    private sealed class PassThroughProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }
}
