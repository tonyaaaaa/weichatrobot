using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Agents;
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
            new UnusedAnswerAgent(),
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

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task Ordinary_private_answer_uses_all_enabled_tags_and_unconfigured_fallbacks(
        int roomType)
    {
        await using var database = Database();
        var robotId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var enabledTagIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var disabledTagId = Guid.NewGuid();
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
            IsDefault = true,
            WebSearchMode = "ZaiChatCompletions"
        });
        database.KnowledgeTags.AddRange(
            new KnowledgeTagEntity
            {
                Id = enabledTagIds[0],
                Name = "产品",
                NormalizedName = "产品",
                IsEnabled = true
            },
            new KnowledgeTagEntity
            {
                Id = enabledTagIds[1],
                Name = "流程",
                NormalizedName = "流程",
                IsEnabled = true
            },
            new KnowledgeTagEntity
            {
                Id = disabledTagId,
                Name = "停用",
                NormalizedName = "停用",
                IsEnabled = false
            });
        database.ConversationMessages.Add(new ConversationMessageEntity
        {
            Id = messageId,
            RobotConfigId = robotId,
            WorkToolMessageId = $"private-answer-{roomType}",
            FallbackHash = $"private-answer-{roomType}",
            FallbackWindowStartUtc = DateTime.UnixEpoch,
            ProcessingState = "pending",
            ChannelType = "Private",
            RoomType = roomType,
            PeerDisplayName = roomType == 2 ? "外部联系人" : "内部同事",
            ScopeHash = $"stable-scope-{roomType}",
            SenderDisplayName = roomType == 2 ? "外部联系人" : "内部同事",
            Text = "签证要多久？",
            ReceivedAtUtc = DateTime.UtcNow
        });
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);

        var retrieval = new EmptyRetrieval();
        var chat = new FallbackChatClient();
        var answerAgent = new RecordingAnswerAgent(new GroundedAnswerService(
            retrieval,
            chat,
            new GroundedAnswerOptions(),
            new AnswerOutputFirewall()));
        var processor = new PrivateChatProcessor(
            database,
            answerAgent,
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
        Assert.Equal(2, chat.Requests.Count);
        Assert.NotNull(chat.Requests[0].WebSearch);
        Assert.Null(chat.Requests[1].WebSearch);
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
        Assert.Equal("大模型回答", outbound.Text);
        Assert.Null(audit.GroupProfileId);
        Assert.Equal("Private", audit.ChannelType);
        Assert.Equal("model_knowledge", audit.AnswerSource);
        Assert.Equal("web_search_no_sources", audit.WebSearchFailureCode);
        Assert.Equal(2, answerAgent.CallCount);
        Assert.Equal(4, chat.Requests.Count);
        Assert.NotNull(chat.Requests[2].WebSearch);
        Assert.Null(chat.Requests[3].WebSearch);
        Assert.Equal(
            enabledTagIds.OrderBy(id => id).ToArray(),
            retrieval.RequestedTagIds.OrderBy(id => id).ToArray());
        Assert.DoesNotContain(disabledTagId, retrieval.RequestedTagIds);
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
        public IReadOnlyList<Guid> RequestedTagIds { get; private set; } = [];

        public Task<KnowledgeTagScope> ResolveScopeAsync(
            IReadOnlyList<Guid> requestedTagIds,
            CancellationToken token)
        {
            RequestedTagIds = requestedTagIds;
            return Task.FromResult(new KnowledgeTagScope(
                requestedTagIds,
                requestedTagIds,
                "all-active-tags"));
        }

        public Task<IReadOnlyList<RetrievalEvidence>> RetrieveAsync(
            string question,
            KnowledgeTagScope scope,
            int limit,
            CancellationToken token) =>
            Task.FromResult<IReadOnlyList<RetrievalEvidence>>([]);
    }

    private sealed class FallbackChatClient : IChatCompletionClient
    {
        public List<ChatCompletionRequest> Requests { get; } = [];

        public Task<ChatCompletionResponse> CompleteAsync(
            ModelProviderConfiguration configuration,
            ChatCompletionRequest request,
            CancellationToken token = default)
        {
            Requests.Add(request);
            return Task.FromResult(
                Requests.Count == 1
                    ? new ChatCompletionResponse("搜索回答", [])
                    : new ChatCompletionResponse("大模型回答"));
        }
    }

    private sealed class RecordingAnswerAgent(GroundedAnswerService inner) : IAnswerAgent
    {
        public int CallCount { get; private set; }

        public Task<GroundedAnswerResult> AnswerAsync(
            GroundedAnswerRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return inner.AnswerAsync(request, cancellationToken);
        }
    }

    private sealed class UnusedAnswerAgent : IAnswerAgent
    {
        public Task<GroundedAnswerResult> AnswerAsync(
            GroundedAnswerRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Direct ingest must not call the answer agent.");
    }

    private sealed class PassThroughProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }
}
