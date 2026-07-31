using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Agents;
using WechatRobot.Application.Conversations;
using WechatRobot.Application.FixedReplies;
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

        var router = new RecordingTemplateRouter(
            new ContinueKnowledgeAnswer());
        var rewrite = new CountingPassThroughRewriteAgent();
        var processor = new PrivateChatProcessor(
            database,
            new UnusedAnswerAgent(),
            new ModelConfigurationService(new PassThroughProtector()),
            new DurableJobRepository(database),
            new PrivateKnowledgeIngestStore(database),
            new ConversationContextService(),
            TimeProvider.System,
            MultiTurn(rewrite),
            templateRouter: router,
            fixedReplies: new FixedReplyTemplateService(
                new FixedReplyTemplateStore(database),
                TimeProvider.System));

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
        Assert.Equal(0, router.PrivateCallCount);
        Assert.Equal(0, rewrite.CallCount);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task Ordinary_private_message_returns_fixed_template_before_answer_agent(
        int roomType)
    {
        await using var database = Database();
        var robotId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var template = new FixedReplyTemplateEntity
        {
            Name = "签证进度",
            NormalizedName = "签证进度",
            IntentDescription = "询问签证进度",
            ReplyText = "固定回复正文",
            ScopeType = "SelectedGroups",
            Priority = 100,
            IsEnabled = true,
            Version = 3
        };
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
        database.FixedReplyTemplates.Add(template);
        database.ConversationMessages.Add(new ConversationMessageEntity
        {
            Id = messageId,
            RobotConfigId = robotId,
            WorkToolMessageId = $"private-fixed-{roomType}",
            FallbackHash = $"private-fixed-{roomType}",
            FallbackWindowStartUtc = DateTime.UnixEpoch,
            ProcessingState = "pending",
            ChannelType = "Private",
            RoomType = roomType,
            PeerDisplayName = roomType == 2 ? "外部联系人" : "内部同事",
            ScopeHash = $"fixed-scope-{roomType}",
            SenderDisplayName = roomType == 2 ? "外部联系人" : "内部同事",
            Text = "签证还有多久？",
            ReceivedAtUtc = DateTime.UtcNow
        });
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        var router = new RecordingTemplateRouter(
            new MatchFixedTemplate(template.Id, template.Version));
        var rewrite = new CountingPassThroughRewriteAgent();
        var processor = new PrivateChatProcessor(
            database,
            new UnusedAnswerAgent(),
            new ModelConfigurationService(new PassThroughProtector()),
            new DurableJobRepository(database),
            new PrivateKnowledgeIngestStore(database),
            new ConversationContextService(),
            TimeProvider.System,
            MultiTurn(rewrite),
            templateRouter: router,
            fixedReplies: new FixedReplyTemplateService(
                new FixedReplyTemplateStore(database),
                TimeProvider.System));

        await processor.ProcessAsync(
            new LeasedDurableJob(
                Guid.NewGuid(),
                "ProcessPrivateMessage",
                $$"""{"messageId":"{{messageId:D}}"}""",
                0,
                "test"),
            TestContext.Current.CancellationToken);

        var outbound = await database.ConversationMessages.AsNoTracking()
            .SingleAsync(
                item => item.InReplyToMessageId == messageId,
                TestContext.Current.CancellationToken);
        var audit = await database.RetrievalAudits.AsNoTracking()
            .SingleAsync(
                item => item.ConversationMessageId == messageId,
                TestContext.Current.CancellationToken);
        Assert.Equal("固定回复正文", outbound.Text);
        Assert.Equal("fixed_template", audit.AnswerSource);
        Assert.Equal(template.Id, audit.FixedReplyTemplateId);
        Assert.Equal(template.Version, audit.FixedReplyTemplateVersion);
        Assert.Equal(1, router.PrivateCallCount);
        Assert.Equal(0, rewrite.CallCount);
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
        var router = new RecordingTemplateRouter(
            new ContinueKnowledgeAnswer());
        var processor = new PrivateChatProcessor(
            database,
            answerAgent,
            new ModelConfigurationService(new PassThroughProtector()),
            new DurableJobRepository(database),
            new PrivateKnowledgeIngestStore(database),
            new ConversationContextService(),
            TimeProvider.System,
            MultiTurn(new CountingPassThroughRewriteAgent()),
            templateRouter: router,
            fixedReplies: new FixedReplyTemplateService(
                new FixedReplyTemplateStore(database),
                TimeProvider.System));
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
        Assert.Equal(2, router.PrivateCallCount);
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

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task Contextual_private_follow_up_passes_rewritten_query_to_answer_agent(
        int roomType)
    {
        await using var database = Database();
        var robotId = Guid.NewGuid();
        var currentId = Guid.NewGuid();
        var scopeHash = $"private-follow-up-{roomType}";
        var previousAt = DateTime.UtcNow.AddMinutes(-1);
        var ingestId = Guid.NewGuid();
        var ingestNotice = PrivateMessage(
            Guid.NewGuid(),
            robotId,
            roomType,
            scopeHash,
            "outbound",
            "assistant",
            "已收到，正在整理并对比现有知识。",
            previousAt.AddSeconds(-1));
        ingestNotice.InReplyToMessageId = ingestId;
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
        database.ConversationMessages.AddRange(
            PrivateMessage(
                ingestId,
                robotId,
                roomType,
                scopeHash,
                "inbound",
                "user",
                "#知识入库 测试知识",
                previousAt.AddSeconds(-2)),
            ingestNotice,
            PrivateMessage(
                Guid.NewGuid(),
                robotId,
                roomType,
                scopeHash,
                "inbound",
                "user",
                "日本三年签证你们能办吗？",
                previousAt),
            PrivateMessage(
                Guid.NewGuid(),
                robotId,
                roomType,
                scopeHash,
                "outbound",
                "assistant",
                "可以办理。",
                previousAt.AddSeconds(1)),
            PrivateMessage(
                currentId,
                robotId,
                roomType,
                scopeHash,
                "inbound",
                "user",
                "需要什么材料？",
                DateTime.UtcNow),
            PrivateMessage(
                Guid.NewGuid(),
                robotId,
                roomType,
                "different-private-scope",
                "inbound",
                "user",
                "不属于当前私聊",
                previousAt));
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        var answer = new CapturingAnswerAgent();
        var rewrite = new FixedRewriteAgent(new QueryRewriteResult(
            QueryRewriteDecision.Search,
            "办理日本三年签证需要准备什么材料？",
            null,
            QueryRewriteReasonCode.ContextualFollowUp,
            11));
        var processor = new PrivateChatProcessor(
            database,
            answer,
            new ModelConfigurationService(new PassThroughProtector()),
            new DurableJobRepository(database),
            new PrivateKnowledgeIngestStore(database),
            new ConversationContextService(),
            TimeProvider.System,
            MultiTurn(rewrite),
            runtimeOptions: new AgentRuntimeOptions
            {
                TemplateRoutingRuntimeMode = TemplateRoutingRuntimeMode.Disabled
            });

        await processor.ProcessAsync(
            new LeasedDurableJob(
                Guid.NewGuid(),
                "ProcessPrivateMessage",
                $$"""{"messageId":"{{currentId:D}}"}""",
                0,
                "test"),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, rewrite.CallCount);
        Assert.Equal(
            "办理日本三年签证需要准备什么材料？",
            answer.LastRequest!.RetrievalQuery!.Query);
        Assert.Equal(
            ConversationChannelType.Private,
            answer.LastRequest.QueryRewriteAudit!.ChannelType);
        Assert.DoesNotContain(
            rewrite.LastRequest!.Context.Messages,
            historyMessage => historyMessage.Content.StartsWith(
                "#知识入库",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            rewrite.LastRequest.Context.Messages,
            historyMessage => historyMessage.Content.Contains(
                "正在整理并对比现有知识",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            answer.LastRequest.Context.Messages,
            message => message.Content == "不属于当前私聊");
    }

    [Fact]
    public async Task Ambiguous_private_follow_up_replies_without_answer_or_retrieval()
    {
        await using var database = Database();
        var robotId = Guid.NewGuid();
        var currentId = Guid.NewGuid();
        const int roomType = 2;
        const string scopeHash = "private-ambiguous";
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
        database.ConversationMessages.AddRange(
            PrivateMessage(
                Guid.NewGuid(),
                robotId,
                roomType,
                scopeHash,
                "inbound",
                "user",
                "日本三年签证和五年签证都能办吗？",
                DateTime.UtcNow.AddMinutes(-1)),
            PrivateMessage(
                currentId,
                robotId,
                roomType,
                scopeHash,
                "inbound",
                "user",
                "那个需要什么材料？",
                DateTime.UtcNow));
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        var answer = new CapturingAnswerAgent();
        var processor = new PrivateChatProcessor(
            database,
            answer,
            new ModelConfigurationService(new PassThroughProtector()),
            new DurableJobRepository(database),
            new PrivateKnowledgeIngestStore(database),
            new ConversationContextService(),
            TimeProvider.System,
            MultiTurn(new FixedRewriteAgent(new QueryRewriteResult(
                QueryRewriteDecision.Clarification,
                null,
                "请确认您咨询的是日本三年签证还是五年签证？",
                QueryRewriteReasonCode.AmbiguousReference))),
            runtimeOptions: new AgentRuntimeOptions
            {
                TemplateRoutingRuntimeMode = TemplateRoutingRuntimeMode.Disabled
            });

        await processor.ProcessAsync(
            new LeasedDurableJob(
                Guid.NewGuid(),
                "ProcessPrivateMessage",
                $$"""{"messageId":"{{currentId:D}}"}""",
                0,
                "test"),
            TestContext.Current.CancellationToken);

        Assert.Null(answer.LastRequest);
        var outbound = await database.ConversationMessages.AsNoTracking()
            .SingleAsync(
                message => message.InReplyToMessageId == currentId,
                TestContext.Current.CancellationToken);
        var audit = await database.RetrievalAudits.AsNoTracking()
            .SingleAsync(
                item => item.ConversationMessageId == currentId,
                TestContext.Current.CancellationToken);
        Assert.Equal(
            "请确认您咨询的是日本三年签证还是五年签证？",
            outbound.Text);
        Assert.Contains(
            "\"RagExecuted\":false",
            audit.InputSummaryJson,
            StringComparison.Ordinal);
    }

    private static WechatRobotDbContext Database() =>
        new(new DbContextOptionsBuilder<WechatRobotDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static MultiTurnRetrievalService MultiTurn(
        IQueryRewriteAgent rewriteAgent) =>
        new(
            rewriteAgent,
            new RetrievalQueryOptions(),
            new AnswerOutputFirewall(),
            new GroundedAnswerOptions());

    private static ConversationMessageEntity PrivateMessage(
        Guid id,
        Guid robotId,
        int roomType,
        string scopeHash,
        string direction,
        string role,
        string text,
        DateTime atUtc) =>
        new()
        {
            Id = id,
            RobotConfigId = robotId,
            WorkToolMessageId = direction == "inbound"
                ? $"private-{id:N}"
                : null,
            FallbackHash = $"private-{id:N}",
            FallbackWindowStartUtc = DateTime.UnixEpoch,
            ProcessingState = "pending",
            ChannelType = "Private",
            RoomType = roomType,
            PeerDisplayName = "测试用户",
            ScopeHash = scopeHash,
            SenderDisplayName = direction == "outbound"
                ? "机器人"
                : "测试用户",
            Direction = direction,
            Role = role,
            Text = text,
            ReceivedAtUtc = atUtc,
            CreatedAtUtc = atUtc
        };

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

    private sealed class CapturingAnswerAgent : IAnswerAgent
    {
        public GroundedAnswerRequest? LastRequest { get; private set; }

        public Task<GroundedAnswerResult> AnswerAsync(
            GroundedAnswerRequest request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new GroundedAnswerResult(
                new AnswerDecision(AnswerDecisionKind.Answer, "测试回答"),
                new RetrievalAuditDraft(
                    [],
                    .7,
                    .9,
                    "private",
                    "Answer",
                    InputSummaryJson: "{}")));
        }
    }

    private sealed class FixedRewriteAgent(QueryRewriteResult result)
        : IQueryRewriteAgent
    {
        public int CallCount { get; private set; }
        public QueryRewriteRequest? LastRequest { get; private set; }

        public Task<QueryRewriteResult> RewriteAsync(
            QueryRewriteRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            return Task.FromResult(result);
        }
    }

    private sealed class CountingPassThroughRewriteAgent
        : IQueryRewriteAgent
    {
        public int CallCount { get; private set; }

        public Task<QueryRewriteResult> RewriteAsync(
            QueryRewriteRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new QueryRewriteResult(
                QueryRewriteDecision.Search,
                request.CurrentQuestion,
                null,
                QueryRewriteReasonCode.StandaloneQuestion));
        }
    }

    private sealed class UnusedAnswerAgent : IAnswerAgent
    {
        public Task<GroundedAnswerResult> AnswerAsync(
            GroundedAnswerRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Direct ingest must not call the answer agent.");
    }

    private sealed class RecordingTemplateRouter(TemplateRouteDecision decision)
        : ITemplateRoutingAgent
    {
        public int PrivateCallCount { get; private set; }

        public Task<TemplateRouteDecision> RoutePrivateAsync(
            string message,
            CancellationToken cancellationToken)
        {
            PrivateCallCount++;
            return Task.FromResult(decision);
        }

        public Task<TemplateRouteDecision> RouteAsync(
            Guid groupProfileId,
            string message,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Private chat must not use group routing.");
    }

    private sealed class PassThroughProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }
}
