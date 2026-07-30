using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Conversations;
using WechatRobot.Application.Jobs;
using WechatRobot.Application.Knowledge;
using WechatRobot.Application.Models;
using WechatRobot.Application.PrivateChat;
using WechatRobot.Application.Security;
using WechatRobot.Infrastructure.Agents;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.IntegrationTests.PrivateChat;

public sealed class PrivateChatProcessorTests
{
    [Fact]
    public async Task Direct_ingest_batch_job_does_not_reuse_source_message_unique_relation()
    {
        await using var database = Database();
        var robotId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var parentJobId = Guid.NewGuid();
        database.RobotConfigs.Add(new RobotConfigEntity
        {
            Id = robotId,
            Name = "机器人",
            EncryptedWorkToolRobotId = "encrypted"
        });
        database.ConversationMessages.Add(new ConversationMessageEntity
        {
            Id = messageId,
            RobotConfigId = robotId,
            WorkToolMessageId = "private-ingest-job-relation",
            FallbackHash = "private-ingest-job-relation",
            FallbackWindowStartUtc = DateTime.UnixEpoch,
            ProcessingState = "leased",
            ChannelType = "Private",
            RoomType = 4,
            PeerDisplayName = "内部同事",
            ScopeHash = "stable-scope",
            SenderDisplayName = "内部同事",
            Text = "#知识入库 测试知识",
            ReceivedAtUtc = DateTime.UtcNow
        });
        database.DurableJobs.Add(new DurableJobEntity
        {
            Id = parentJobId,
            JobType = "ProcessPrivateMessage",
            RelatedConversationMessageId = messageId,
            Status = "leased",
            PayloadJson = $$"""{"messageId":"{{messageId:D}}"}"""
        });
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);

        var processor = new PrivateChatProcessor(
            database,
            new GroundedAnswerService(
                new EmptyRetrieval(),
                new UnusedChatClient(),
                new GroundedAnswerOptions(),
                new AnswerOutputFirewall()),
            new ModelConfigurationService(new PassThroughProtector()),
            new DurableJobRepository(database),
            new PrivateKnowledgeIngestStore(database),
            new ConversationContextService(),
            TimeProvider.System);

        await processor.ProcessAsync(
            new LeasedDurableJob(
                parentJobId,
                "ProcessPrivateMessage",
                $$"""{"messageId":"{{messageId:D}}"}""",
                0,
                "test"),
            TestContext.Current.CancellationToken);

        var batch = await database.PrivateKnowledgeIngestBatches.AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        var batchJob = await database.DurableJobs.AsNoTracking()
            .SingleAsync(x => x.Id == batch.Id, TestContext.Current.CancellationToken);
        Assert.Null(batchJob.RelatedConversationMessageId);
    }

    [Fact]
    public async Task Ordinary_private_answer_is_session_bound_audited_and_enqueued_once()
    {
        await using var database = Database();
        var robotId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        database.RobotConfigs.Add(new RobotConfigEntity
        {
            Id = robotId,
            Name = "机器人",
            EncryptedWorkToolRobotId = "encrypted"
        });
        database.ModelConfigs.Add(new ModelConfigEntity
        {
            Name = "chat",
            NormalizedName = "CHAT",
            Provider = "OpenAI",
            ConfigurationType = "chat",
            BaseUrl = "https://example.test",
            Model = "chat",
            IsEnabled = true,
            IsDefault = true
        });
        database.KnowledgeTags.Add(new KnowledgeTagEntity
        {
            Name = "全局",
            NormalizedName = "全局",
            IsEnabled = true
        });
        database.ConversationMessages.Add(new ConversationMessageEntity
        {
            Id = messageId,
            RobotConfigId = robotId,
            WorkToolMessageId = "private-answer-1",
            FallbackHash = "private-answer-1",
            FallbackWindowStartUtc = DateTime.UnixEpoch,
            ProcessingState = "pending",
            ChannelType = "Private",
            RoomType = 4,
            PeerDisplayName = "内部同事",
            ScopeHash = "stable-scope",
            SenderDisplayName = "内部同事",
            Text = "签证要多久？",
            ReceivedAtUtc = DateTime.UtcNow
        });
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);

        var processor = new PrivateChatProcessor(
            database,
            new GroundedAnswerService(
                new EmptyRetrieval(),
                new UnusedChatClient(),
                new GroundedAnswerOptions(),
                new AnswerOutputFirewall()),
            new ModelConfigurationService(new PassThroughProtector()),
            new DurableJobRepository(database),
            new PrivateKnowledgeIngestStore(database),
            new ConversationContextService(),
            TimeProvider.System);
        var job = new LeasedDurableJob(
            Guid.NewGuid(),
            "ProcessPrivateMessage",
            $$"""{"messageId":"{{messageId:D}}"}""",
            0,
            "test");

        await processor.ProcessAsync(job, TestContext.Current.CancellationToken);
        await processor.ProcessAsync(job, TestContext.Current.CancellationToken);

        database.ChangeTracker.Clear();
        var inbound = await database.ConversationMessages.AsNoTracking()
            .SingleAsync(x => x.Id == messageId, TestContext.Current.CancellationToken);
        var outbound = await database.ConversationMessages.AsNoTracking()
            .SingleAsync(x => x.InReplyToMessageId == messageId, TestContext.Current.CancellationToken);
        var audit = await database.RetrievalAudits.AsNoTracking()
            .SingleAsync(x => x.ConversationMessageId == messageId, TestContext.Current.CancellationToken);

        Assert.NotNull(inbound.ConversationSessionId);
        Assert.Equal(inbound.ConversationSessionId, outbound.ConversationSessionId);
        Assert.Null(audit.GroupProfileId);
        Assert.Equal("Private", audit.ChannelType);
        Assert.Single(await database.SendCommands.AsNoTracking()
            .Where(x => x.IdempotencyKey == $"private-reply:{messageId:D}")
            .ToArrayAsync(TestContext.Current.CancellationToken));
    }

    private static WechatRobotDbContext Database() =>
        new(new DbContextOptionsBuilder<WechatRobotDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private sealed class EmptyRetrieval : IRetrievalEvidenceProvider
    {
        public Task<KnowledgeTagScope> ResolveScopeAsync(
            IReadOnlyList<Guid> requestedTagIds,
            CancellationToken token) =>
            Task.FromResult(new KnowledgeTagScope(requestedTagIds, [], "all-active-tags"));

        public Task<IReadOnlyList<RetrievalEvidence>> RetrieveAsync(
            string question,
            KnowledgeTagScope scope,
            int limit,
            CancellationToken token) =>
            Task.FromResult<IReadOnlyList<RetrievalEvidence>>([]);
    }

    private sealed class UnusedChatClient : IChatCompletionClient
    {
        public Task<ChatCompletionResponse> CompleteAsync(
            ModelProviderConfiguration configuration,
            ChatCompletionRequest request,
            CancellationToken token = default) =>
            throw new InvalidOperationException("No-evidence private answer must not call the chat model.");
    }

    private sealed class PassThroughProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }
}
