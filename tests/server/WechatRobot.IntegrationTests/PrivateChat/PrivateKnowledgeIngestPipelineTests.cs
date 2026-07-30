using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using WechatRobot.Application.Jobs;
using WechatRobot.Application.Knowledge;
using WechatRobot.Application.Models;
using WechatRobot.Application.PrivateChat;
using WechatRobot.Application.Security;
using WechatRobot.Infrastructure.Agents;
using WechatRobot.Infrastructure.Knowledge;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.IntegrationTests.PrivateChat;

public sealed class PrivateKnowledgeIngestPipelineTests
{
    [Fact]
    public async Task Processor_stages_validated_items_with_global_tag_and_batch_index_job()
    {
        await using var database = Database();
        var robotId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        database.RobotConfigs.Add(new RobotConfigEntity
        {
            Id = robotId,
            Name = "机器人",
            EncryptedWorkToolRobotId = "encrypted"
        });
        database.ModelConfigs.Add(new ModelConfigEntity
        {
            Name = "embedding",
            NormalizedName = "EMBEDDING",
            Provider = "OpenAI",
            ConfigurationType = "embedding",
            BaseUrl = "https://example.test",
            Model = "embedding",
            EmbeddingDimension = 3,
            IsEnabled = true,
            IsDefault = true
        });
        database.ConversationMessages.Add(new ConversationMessageEntity
        {
            Id = messageId,
            RobotConfigId = robotId,
            WorkToolMessageId = "private-ingest-1",
            FallbackHash = "private-ingest-1",
            FallbackWindowStartUtc = DateTime.UnixEpoch,
            ChannelType = "Private",
            RoomType = 4,
            PeerDisplayName = "内部同事",
            ScopeHash = "scope",
            SenderDisplayName = "内部同事",
            Text = "#知识入库\n加拿大签证由签证机关审核。",
            ReceivedAtUtc = DateTime.UtcNow
        });
        database.PrivateKnowledgeIngestBatches.Add(new PrivateKnowledgeIngestBatchEntity
        {
            Id = batchId,
            RobotConfigId = robotId,
            SourceConversationMessageId = messageId,
            RoomType = 4,
            SourceActorDisplayName = "内部同事",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);

        var knowledge = new QdrantKnowledgeService(
            database,
            new ModelConfigurationService(new PassThroughProtector()),
            new KnowledgeIndexOptions(3, VectorDistance.Cosine),
            TimeProvider.System);
        var processor = new PrivateKnowledgeIngestProcessor(
            database,
            new StubProposalAgent(),
            new PrivateKnowledgeIngestStore(database),
            knowledge,
            new DurableJobRepository(database),
            TimeProvider.System);

        await processor.ProcessAsync(
            new LeasedDurableJob(
                batchId,
                "ProcessPrivateKnowledgeIngest",
                $$"""{"batchId":"{{batchId:D}}"}""",
                0,
                "test"),
            TestContext.Current.CancellationToken);

        var batch = await database.PrivateKnowledgeIngestBatches
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        var item = await database.PrivateKnowledgeIngestItems
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        var version = await database.KnowledgeDocumentVersions
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        var indexJob = await database.KnowledgeIndexJobs
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        var global = await database.KnowledgeTags
            .AsNoTracking()
            .SingleAsync(x => x.SystemKind == "GlobalKnowledge", TestContext.Current.CancellationToken);

        Assert.Equal("Indexing", batch.Status);
        Assert.Equal("New", item.ChangeKind);
        Assert.Equal("PrivateChatDirect", version.SourceKind);
        Assert.Equal(batchId, version.SourceBatchId);
        Assert.Equal(batchId, indexJob.PrivateKnowledgeIngestBatchId);
        Assert.Contains(global.Id.ToString(), indexJob.PendingTagIdsJson, StringComparison.OrdinalIgnoreCase);

        var trackedIndexJob = await database.KnowledgeIndexJobs.SingleAsync(
            x => x.Id == indexJob.Id,
            TestContext.Current.CancellationToken);
        trackedIndexJob.Status = "leased";
        trackedIndexJob.LeaseOwner = "index-test";
        trackedIndexJob.LeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(2);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        await new KnowledgeIndexService(
                new ImmediateEmbeddingClient(),
                new MemoryVectorStore(),
                knowledge,
                new KnowledgeIndexOptions(3, VectorDistance.Cosine))
            .IndexAsync(indexJob.Id, TestContext.Current.CancellationToken);

        database.ChangeTracker.Clear();
        batch = await database.PrivateKnowledgeIngestBatches
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        var document = await database.KnowledgeDocuments
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        version = await database.KnowledgeDocumentVersions
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Activated", batch.Status);
        Assert.Equal(version.Id, document.ActiveVersionId);
        Assert.True(version.IsPublished);
        Assert.Contains(
            await database.SendCommands.AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken),
            x => x.IdempotencyKey == $"private-ingest-final:{batchId:D}");
    }

    [Fact]
    public async Task Batch_does_not_activate_first_version_when_another_index_job_fails()
    {
        await using var database = Database();
        var robotId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var tagId = Guid.NewGuid();
        database.RobotConfigs.Add(new RobotConfigEntity
        {
            Id = robotId,
            Name = "机器人",
            EncryptedWorkToolRobotId = "encrypted"
        });
        database.ModelConfigs.Add(new ModelConfigEntity
        {
            Name = "embedding",
            NormalizedName = "EMBEDDING",
            Provider = "OpenAI",
            ConfigurationType = "embedding",
            BaseUrl = "https://example.test",
            Model = "embedding",
            EmbeddingDimension = 3,
            IsEnabled = true,
            IsDefault = true
        });
        database.KnowledgeTags.Add(new KnowledgeTagEntity
        {
            Id = tagId,
            Name = "签证",
            NormalizedName = "签证",
            IsEnabled = true
        });
        database.ConversationMessages.Add(new ConversationMessageEntity
        {
            Id = messageId,
            RobotConfigId = robotId,
            WorkToolMessageId = "private-ingest-atomic",
            FallbackHash = "private-ingest-atomic",
            FallbackWindowStartUtc = DateTime.UnixEpoch,
            ChannelType = "Private",
            RoomType = 4,
            PeerDisplayName = "内部同事",
            ScopeHash = "scope",
            SenderDisplayName = "内部同事",
            Text = "#知识入库\n两条知识",
            ReceivedAtUtc = DateTime.UtcNow
        });
        database.PrivateKnowledgeIngestBatches.Add(new PrivateKnowledgeIngestBatchEntity
        {
            Id = batchId,
            RobotConfigId = robotId,
            SourceConversationMessageId = messageId,
            RoomType = 4,
            SourceActorDisplayName = "内部同事",
            Status = "Indexing",
            TotalCount = 2,
            NewCount = 2,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });
        var documents = Enumerable.Range(1, 2).Select(index =>
        {
            var document = new KnowledgeDocumentEntity
            {
                Title = $"知识 {index}",
                Status = "indexing"
            };
            var version = new KnowledgeDocumentVersionEntity
            {
                KnowledgeDocumentId = document.Id,
                Version = 1,
                OriginalFileName = $"{index}.txt",
                SafeFileName = $"{Guid.NewGuid():N}.txt",
                ContentType = "text/plain",
                Sha256 = new string((char)('A' + index), 64),
                ObjectKey = $"private/{index}",
                Status = "approved",
                SourceKind = "PrivateChatDirect",
                SourceConversationMessageId = messageId,
                SourceBatchId = batchId,
                ChangeKind = "New"
            };
            database.KnowledgeDocuments.Add(document);
            database.KnowledgeDocumentVersions.Add(version);
            database.KnowledgeChunks.Add(new KnowledgeChunkEntity
            {
                KnowledgeDocumentVersionId = version.Id,
                Sequence = 1,
                Text = $"知识 {index}",
                Status = "approved"
            });
            database.PrivateKnowledgeIngestItems.Add(new PrivateKnowledgeIngestItemEntity
            {
                BatchId = batchId,
                Sequence = index,
                Question = $"问题 {index}",
                Answer = $"答案 {index}",
                ChangeKind = "New",
                StagedDocumentId = document.Id,
                StagedVersionId = version.Id,
                QuestionFingerprint = new string('Q', 63) + index,
                AnswerFingerprint = new string('A', 63) + index,
                ResolvedTagIdsJson = JsonSerializer.Serialize(new[] { tagId })
            });
            return (document, version);
        }).ToArray();
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        var knowledge = new QdrantKnowledgeService(
            database,
            new ModelConfigurationService(new PassThroughProtector()),
            new KnowledgeIndexOptions(3, VectorDistance.Cosine),
            TimeProvider.System);
        foreach (var (document, version) in documents)
        {
            await knowledge.QueuePrivateBatchIndexAsync(
                batchId,
                document.Id,
                version.Id,
                [tagId],
                TestContext.Current.CancellationToken);
        }

        var jobs = await database.KnowledgeIndexJobs
            .OrderBy(x => x.Id)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        jobs[0].Status = "leased";
        jobs[0].LeaseOwner = "first";
        jobs[0].LeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(1);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        var firstWork = await knowledge.LoadIndexWorkAsync(
            jobs[0].Id,
            TestContext.Current.CancellationToken);
        Assert.True(await knowledge.CompleteIndexAsync(
            firstWork,
            TestContext.Current.CancellationToken));

        database.ChangeTracker.Clear();
        Assert.All(
            await database.KnowledgeDocuments.AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken),
            document => Assert.Null(document.ActiveVersionId));

        var second = await database.KnowledgeIndexJobs.SingleAsync(
            x => x.Id == jobs[1].Id,
            TestContext.Current.CancellationToken);
        second.Status = "leased";
        second.LeaseOwner = "second";
        second.LeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(1);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        await knowledge.MarkIndexFailedAsync(
            second.Id,
            "second",
            "injected failure",
            false,
            TestContext.Current.CancellationToken);

        database.ChangeTracker.Clear();
        var failedBatch = await database.PrivateKnowledgeIngestBatches
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Failed", failedBatch.Status);
        Assert.All(
            await database.KnowledgeDocuments.AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken),
            document => Assert.Null(document.ActiveVersionId));
        Assert.All(
            await database.KnowledgeDocumentVersions.AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken),
            version => Assert.False(version.IsPublished));
    }

    private static WechatRobotDbContext Database() =>
        new(new DbContextOptionsBuilder<WechatRobotDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private sealed class StubProposalAgent : IPrivateKnowledgeProposalAgent
    {
        public Task<IReadOnlyList<ProposedKnowledgeItem>> ProposeAsync(
            string sourceText,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProposedKnowledgeItem>>([
                new(
                    "加拿大签证由谁审核？",
                    "加拿大签证由签证机关审核。",
                    [],
                    null,
                    null,
                    KnowledgeChangeKind.New)
            ]);
    }

    private sealed class PassThroughProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }

    private sealed class ImmediateEmbeddingClient : IEmbeddingClient
    {
        public Task<EmbeddingBatchResponse> CreateEmbeddingsAsync(
            ModelProviderConfiguration configuration,
            EmbeddingBatchRequest request,
            CancellationToken token = default) =>
            Task.FromResult(new EmbeddingBatchResponse(
                request.Inputs.Select(_ =>
                    (IReadOnlyList<float>)new float[] { 1, 0, 0 }).ToArray()));
    }

    private sealed class MemoryVectorStore : IVectorStore
    {
        public Task EnsureCollectionAsync(VectorCollection collection, CancellationToken token) => Task.CompletedTask;
        public Task UpsertAsync(VectorCollection collection, IReadOnlyList<VectorPoint> points, CancellationToken token) => Task.CompletedTask;
        public Task SetVersionActiveAsync(VectorCollection collection, Guid versionId, bool active, CancellationToken token) => Task.CompletedTask;
        public Task DeleteCollectionAsync(VectorCollection collection, CancellationToken token) => Task.CompletedTask;
        public Task<VectorCollection?> InspectCollectionAsync(string collectionName, CancellationToken token) => Task.FromResult<VectorCollection?>(null);
        public Task DeleteVersionAsync(VectorCollection collection, Guid versionId, CancellationToken token) => Task.CompletedTask;
        public Task<IReadOnlyList<VectorPointMetadata>> InspectVersionAsync(VectorCollection collection, Guid versionId, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<VectorPointMetadata>>([]);
        public Task<IReadOnlyList<VectorSearchHit>> SearchAsync(VectorSearchRequest request, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<VectorSearchHit>>([]);
    }
}
