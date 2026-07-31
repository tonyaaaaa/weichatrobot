using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WechatRobot.Application.Agents;
using WechatRobot.Application.Conversations;
using WechatRobot.Application.Jobs;
using WechatRobot.Application.Knowledge;
using WechatRobot.Application.Messaging;
using WechatRobot.Application.Models;
using WechatRobot.Application.Security;
using WechatRobot.Domain.Groups;
using WechatRobot.Infrastructure.Conversations;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.IntegrationTests.Infrastructure;
using WechatRobot.Worker.Jobs;

namespace WechatRobot.IntegrationTests.Conversations;

public sealed class InboundGroupRulePipelineTests : IClassFixture<MySqlFixture>
{
    private readonly MySqlFixture fixture;

    public InboundGroupRulePipelineTests(MySqlFixture fixture) => this.fixture = fixture;

    [Fact]
    public async Task Disabled_group_message_reaches_a_terminal_no_reply_without_entering_a_session()
    {
        await using var database = new WechatRobotDbContext(
            new DbContextOptionsBuilder<WechatRobotDbContext>()
                .UseMySQL(fixture.ConnectionString)
                .Options);
        await database.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var robot = new RobotConfigEntity
        {
            Name = $"disabled-group-{Guid.NewGuid():N}",
            WorkToolRobotId = $"disabled-group-{Guid.NewGuid():N}",
            CallbackSecretHash = "test"
        };
        var group = new GroupProfileEntity
        {
            RobotConfigId = robot.Id,
            Name = "停用测试群",
            IsEnabled = false
        };
        var message = new ConversationMessageEntity
        {
            RobotConfigId = robot.Id,
            GroupProfileId = group.Id,
            Direction = "inbound",
            Role = "user",
            GroupName = group.Name,
            SenderDisplayName = "Alice",
            Text = "问题",
            FallbackHash = Guid.NewGuid().ToString("N"),
            ProcessingState = "pending"
        };
        database.AddRange(robot, group, message);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new GroundedConversationRepository(
            database,
            new ModelConfigurationService(new PassThroughProtector()),
            TimeProvider.System);

        var decision = await repository.EvaluateInboundPolicyAsync(
            message.Id,
            group.Name,
            null,
            wasMentioned: true,
            TestContext.Current.CancellationToken);
        await repository.PersistNoReplyTerminalAsync(
            decision,
            TestContext.Current.CancellationToken);

        database.ChangeTracker.Clear();
        var stored = await database.ConversationMessages.SingleAsync(
            item => item.Id == message.Id,
            TestContext.Current.CancellationToken);
        Assert.Equal("group_disabled", stored.TerminalReason);
        Assert.Equal("completed", stored.ProcessingState);
        Assert.Empty(await database.ConversationSessions.Where(
            session => session.GroupProfileId == group.Id).ToArrayAsync(
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Intent_no_reply_detaches_preleased_message_from_formal_context()
    {
        await using var database = new WechatRobotDbContext(
            new DbContextOptionsBuilder<WechatRobotDbContext>()
                .UseMySQL(fixture.ConnectionString)
                .Options);
        await database.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var now = DateTime.UtcNow;
        var robot = new RobotConfigEntity
        {
            Name = $"intent-no-reply-{Guid.NewGuid():N}",
            WorkToolRobotId = $"intent-no-reply-{Guid.NewGuid():N}",
            CallbackSecretHash = "test"
        };
        var group = new GroupProfileEntity
        {
            RobotConfigId = robot.Id,
            Name = $"意图过滤群-{Guid.NewGuid():N}"
        };
        var session = new ConversationSessionEntity
        {
            GroupProfileId = group.Id,
            SenderScopeKey = "group-shared",
            LastActivityAtUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        var message = new ConversationMessageEntity
        {
            RobotConfigId = robot.Id,
            GroupProfileId = group.Id,
            ConversationSessionId = session.Id,
            SessionSequence = 1,
            Direction = "inbound",
            Role = "user",
            GroupName = group.Name,
            SenderDisplayName = "Alice",
            Text = "你们两个人继续聊",
            FallbackHash = Guid.NewGuid().ToString("N"),
            ProcessingState = "leased"
        };
        database.AddRange(robot, group, session, message);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new GroundedConversationRepository(
            database,
            new ModelConfigurationService(new PassThroughProtector()),
            TimeProvider.System);

        await repository.PersistNoReplyTerminalAsync(
            new(
                message.Id,
                InboundPolicyDecisionKind.NoReply,
                group.Id,
                "human_to_human_exchange",
                "{}"),
            TestContext.Current.CancellationToken);

        database.ChangeTracker.Clear();
        var stored = await database.ConversationMessages.AsNoTracking()
            .SingleAsync(
                item => item.Id == message.Id,
                TestContext.Current.CancellationToken);
        Assert.Null(stored.ConversationSessionId);
        Assert.Null(stored.SessionSequence);
        Assert.Equal("no_reply", stored.TerminalDecision);
    }

    public static TheoryData<string, string, GroupRulePatternKind, string?, GroupRulePatternKind, bool, bool, string?> Cases => new()
    {
        { "技术部", "技术部", GroupRulePatternKind.Exact, null, GroupRulePatternKind.Exact, true, true, null },
        { "华东技术支持群", "技术", GroupRulePatternKind.Contains, null, GroupRulePatternKind.Exact, true, true, null },
        { "售后-北京-01", "^售后-.*-\\d+$", GroupRulePatternKind.Regex, null, GroupRulePatternKind.Exact, true, true, null },
        { "技术测试群", "技术", GroupRulePatternKind.Contains, "测试", GroupRulePatternKind.Contains, true, false, "group_rule_excluded" },
        { "行政群", "技术", GroupRulePatternKind.Contains, null, GroupRulePatternKind.Exact, true, false, "group_rule_unmatched" },
        { "技术部", "技术部", GroupRulePatternKind.Exact, null, GroupRulePatternKind.Exact, false, true, null },
        { "技术部", "(", GroupRulePatternKind.Regex, null, GroupRulePatternKind.Exact, true, false, "group_rule_invalid" }
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Persisted_rules_gate_the_real_durable_pipeline_before_retrieval_model_and_send(
        string inboundGroupName,
        string includePattern,
        GroupRulePatternKind includeKind,
        string? excludePattern,
        GroupRulePatternKind excludeKind,
        bool wasMentioned,
        bool expectedReply,
        string? expectedReason)
    {
        using var services = Services();
        Guid messageId;
        Guid groupId;
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
            await db.ModelConfigs.Where(item => item.ConfigurationType == "chat" && item.IsDefault)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsDefault, false), TestContext.Current.CancellationToken);
            var suffix = Guid.NewGuid().ToString("N");
            var robot = new RobotConfigEntity { Name = $"rules-{suffix}", WorkToolRobotId = $"rules-{suffix}", CallbackSecretHash = "test" };
            var group = new GroupProfileEntity
            {
                RobotConfigId = robot.Id,
                ExternalGroupId = $"opaque-external-{suffix}",
                Name = includeKind == GroupRulePatternKind.Exact && inboundGroupName != "行政群"
                    ? inboundGroupName
                    : $"configured-rule-target-{suffix}"
            };
            var rule = new GroupRuleEntity
            {
                GroupProfileId = group.Id,
                RuleKind = 0,
                IncludePattern = includePattern,
                IncludePatternKind = (int)includeKind,
                IsEnabled = true
            };
            var persistedRules = new List<GroupRuleEntity> { rule };
            if (excludePattern is not null)
            {
                persistedRules.Add(new GroupRuleEntity
                {
                    GroupProfileId = group.Id,
                    RuleKind = 1,
                    IncludePattern = excludePattern,
                    IncludePatternKind = (int)excludeKind,
                    IsEnabled = true
                });
            }
            db.AddRange(robot, group, new ModelConfigEntity
            {
                Name = $"chat-{suffix}", NormalizedName = $"CHAT-{suffix.ToUpperInvariant()}", Provider = "fake", ConfigurationType = "chat", BaseUrl = "https://fake.invalid",
                Model = "fake", EncryptedApiKey = "fake", IsDefault = true
            });
            db.GroupRules.AddRange(persistedRules);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            var externalMessageId = $"rules-message-{suffix}";
            await scope.ServiceProvider.GetRequiredService<IDurableJobRepository>().IngestInboundMessageAsync(new(
                robot.Id, externalMessageId, $"rules-fallback-{suffix}", DateTime.UtcNow, inboundGroupName, null, "Alice", "question",
                DateTime.UtcNow, "stable-alice", wasMentioned), TestContext.Current.CancellationToken);
            messageId = await db.ConversationMessages.Where(item => item.WorkToolMessageId == externalMessageId)
                .Select(item => item.Id).SingleAsync(TestContext.Current.CancellationToken);
            groupId = group.Id;
        }

        var retrievalBefore = services.GetRequiredService<CountingEvidence>().CallCount;
        var modelBefore = services.GetRequiredService<CountingChat>().CallCount;
        var worker = new DurableJobWorker(services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
        Assert.True(await worker.ProcessOnceAsync(TestContext.Current.CancellationToken));

        await using var verify = services.CreateAsyncScope();
        var database = verify.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var message = await database.ConversationMessages.AsNoTracking().SingleAsync(item => item.Id == messageId, TestContext.Current.CancellationToken);
        Assert.Equal("completed", message.ProcessingState);
        Assert.Equal(expectedReply ? groupId : message.GroupProfileId, message.GroupProfileId);
        Assert.Equal(expectedReply ? null : "no_reply", message.TerminalDecision);
        Assert.Equal(expectedReason, message.TerminalReason);
        if (expectedReply)
        {
            Assert.Equal(retrievalBefore + 1, services.GetRequiredService<CountingEvidence>().CallCount);
            Assert.Equal(modelBefore + 1, services.GetRequiredService<CountingChat>().CallCount);
            var send = await database.SendCommands.SingleAsync(
                item => item.GroupProfileId == groupId,
                TestContext.Current.CancellationToken);
            using var payload = JsonDocument.Parse(send.PayloadJson);
            Assert.Equal(
                inboundGroupName,
                payload.RootElement.GetProperty("GroupName").GetString());
        }
        else
        {
            Assert.Equal(retrievalBefore, services.GetRequiredService<CountingEvidence>().CallCount);
            Assert.Equal(modelBefore, services.GetRequiredService<CountingChat>().CallCount);
            Assert.Equal(0, await database.SendCommands.CountAsync(item => item.IdempotencyKey.Contains(messageId.ToString()), TestContext.Current.CancellationToken));
            Assert.DoesNotContain(inboundGroupName, message.TerminalEvidenceJson ?? string.Empty, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Visible_group_name_cannot_be_hijacked_by_another_profiles_external_id()
    {
        using var services = Services();
        Guid messageId;
        Guid groupId;
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
            await db.ModelConfigs.Where(item => item.ConfigurationType == "chat" && item.IsDefault)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsDefault, false), TestContext.Current.CancellationToken);
            var suffix = Guid.NewGuid().ToString("N");
            var visibleName = $"visible-{suffix}";
            var robot = new RobotConfigEntity { Name = suffix, WorkToolRobotId = suffix, CallbackSecretHash = "test" };
            var intended = new GroupProfileEntity { RobotConfigId = robot.Id, ExternalGroupId = $"actual-{suffix}", Name = visibleName };
            var hijacker = new GroupProfileEntity { RobotConfigId = robot.Id, ExternalGroupId = visibleName, Name = $"unrelated-{suffix}" };
            db.AddRange(robot, intended, hijacker,
                new GroupRuleEntity { GroupProfileId = intended.Id, RuleKind = 0, IncludePattern = visibleName, IncludePatternKind = (int)GroupRulePatternKind.Exact },
                new GroupRuleEntity { GroupProfileId = hijacker.Id, RuleKind = 0, IncludePattern = hijacker.Name, IncludePatternKind = (int)GroupRulePatternKind.Exact },
                new ModelConfigEntity { Name = $"chat-{suffix}", NormalizedName = $"CHAT-{suffix.ToUpperInvariant()}", Provider = "fake", ConfigurationType = "chat", BaseUrl = "https://fake.invalid", Model = "fake", EncryptedApiKey = "fake", IsDefault = true });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            await scope.ServiceProvider.GetRequiredService<IDurableJobRepository>().IngestInboundMessageAsync(new(
                robot.Id, $"message-{suffix}", $"fallback-{suffix}", DateTime.UtcNow, visibleName, null, "Alice", "question", DateTime.UtcNow,
                "stable", true), TestContext.Current.CancellationToken);
            messageId = await db.ConversationMessages.OrderByDescending(item => item.CreatedAtUtc).Select(item => item.Id).FirstAsync(TestContext.Current.CancellationToken);
            groupId = intended.Id;
        }

        Assert.True(await new DurableJobWorker(services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System)
            .ProcessOnceAsync(TestContext.Current.CancellationToken));

        await using var verify = services.CreateAsyncScope();
        var message = await verify.ServiceProvider.GetRequiredService<WechatRobotDbContext>().ConversationMessages.AsNoTracking()
            .SingleAsync(item => item.Id == messageId, TestContext.Current.CancellationToken);
        Assert.Equal(groupId, message.GroupProfileId);
        Assert.Null(message.TerminalDecision);
    }

    [Fact]
    public async Task Duplicate_visible_names_are_resolved_only_by_the_exact_configured_remark()
    {
        using var services = Services();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var suffix = Guid.NewGuid().ToString("N");
        var robot = new RobotConfigEntity { Name = suffix, WorkToolRobotId = suffix, CallbackSecretHash = "test" };
        var east = new GroupProfileEntity
        {
            RobotConfigId = robot.Id,
            ExternalGroupId = $"legacy-east-{suffix}",
            Name = $"duplicate-{suffix}",
            WorkToolGroupRemark = "support-east"
        };
        var west = new GroupProfileEntity
        {
            RobotConfigId = robot.Id,
            ExternalGroupId = $"legacy-west-{suffix}",
            Name = east.Name,
            WorkToolGroupRemark = "support-west"
        };
        db.AddRange(robot, east, west);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = scope.ServiceProvider.GetRequiredService<IDurableJobRepository>();
        var externalMessageId = $"remark-{suffix}";
        await repository.IngestInboundMessageAsync(new InboundMessageIngestRequest(
            robot.Id,
            externalMessageId,
            $"fallback-{suffix}",
            DateTime.UtcNow,
            east.Name,
            "support-east",
            "Alice",
            "question",
            DateTime.UtcNow,
            "stable",
            true), TestContext.Current.CancellationToken);
        var messageId = await db.ConversationMessages.Where(item => item.WorkToolMessageId == externalMessageId)
            .Select(item => item.Id).SingleAsync(TestContext.Current.CancellationToken);

        var decision = await scope.ServiceProvider.GetRequiredService<IGroundedConversationRepository>()
            .EvaluateInboundPolicyAsync(messageId, east.Name, "support-east", true, TestContext.Current.CancellationToken);

        Assert.Equal(InboundPolicyDecisionKind.Proceed, decision.Kind);
        Assert.Equal(east.Id, decision.GroupProfileId);
        await DeletePendingJobAsync(db, messageId);
    }

    [Fact]
    public async Task Unique_visible_name_matches_when_callback_omits_configured_remark()
    {
        using var services = Services();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var suffix = Guid.NewGuid().ToString("N");
        var robot = new RobotConfigEntity { Name = suffix, WorkToolRobotId = suffix, CallbackSecretHash = "test" };
        var group = new GroupProfileEntity
        {
            RobotConfigId = robot.Id,
            ExternalGroupId = $"legacy-{suffix}",
            Name = $"unique-{suffix}",
            WorkToolGroupRemark = $"configured-remark-{suffix}"
        };
        db.AddRange(robot, group);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = scope.ServiceProvider.GetRequiredService<IDurableJobRepository>();
        var externalMessageId = $"missing-remark-{suffix}";
        await repository.IngestInboundMessageAsync(new InboundMessageIngestRequest(
            robot.Id,
            externalMessageId,
            $"fallback-{suffix}",
            DateTime.UtcNow,
            group.Name,
            null,
            "Alice",
            "question",
            DateTime.UtcNow,
            "stable",
            true), TestContext.Current.CancellationToken);
        var messageId = await db.ConversationMessages.Where(item => item.WorkToolMessageId == externalMessageId)
            .Select(item => item.Id).SingleAsync(TestContext.Current.CancellationToken);

        var decision = await scope.ServiceProvider.GetRequiredService<IGroundedConversationRepository>()
            .EvaluateInboundPolicyAsync(messageId, group.Name, null, true, TestContext.Current.CancellationToken);

        Assert.Equal(InboundPolicyDecisionKind.Proceed, decision.Kind);
        Assert.Equal(group.Id, decision.GroupProfileId);
        await DeletePendingJobAsync(db, messageId);
    }

    [Fact]
    public async Task Duplicate_visible_names_without_a_usable_remark_are_rejected_as_ambiguous()
    {
        using var services = Services();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var suffix = Guid.NewGuid().ToString("N");
        var robot = new RobotConfigEntity { Name = suffix, WorkToolRobotId = suffix, CallbackSecretHash = "test" };
        var first = new GroupProfileEntity
        {
            RobotConfigId = robot.Id,
            ExternalGroupId = $"legacy-first-{suffix}",
            Name = $"duplicate-{suffix}"
        };
        var second = new GroupProfileEntity
        {
            RobotConfigId = robot.Id,
            ExternalGroupId = $"legacy-second-{suffix}",
            Name = first.Name
        };
        db.AddRange(robot, first, second);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = scope.ServiceProvider.GetRequiredService<IDurableJobRepository>();
        var externalMessageId = $"ambiguous-{suffix}";
        await repository.IngestInboundMessageAsync(new InboundMessageIngestRequest(
            robot.Id,
            externalMessageId,
            $"fallback-{suffix}",
            DateTime.UtcNow,
            first.Name,
            null,
            "Alice",
            "question",
            DateTime.UtcNow), TestContext.Current.CancellationToken);
        var messageId = await db.ConversationMessages.Where(item => item.WorkToolMessageId == externalMessageId)
            .Select(item => item.Id).SingleAsync(TestContext.Current.CancellationToken);

        var decision = await scope.ServiceProvider.GetRequiredService<IGroundedConversationRepository>()
            .EvaluateInboundPolicyAsync(messageId, first.Name, null, true, TestContext.Current.CancellationToken);

        Assert.Equal(InboundPolicyDecisionKind.NoReply, decision.Kind);
        Assert.Null(decision.GroupProfileId);
        Assert.Equal("group_identity_ambiguous", decision.Reason);
        await DeletePendingJobAsync(db, messageId);
    }

    [Fact]
    public async Task Obsolete_external_group_id_is_never_used_as_a_callback_identity_fallback()
    {
        using var services = Services();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var suffix = Guid.NewGuid().ToString("N");
        var callbackName = $"callback-{suffix}";
        var robot = new RobotConfigEntity { Name = suffix, WorkToolRobotId = suffix, CallbackSecretHash = "test" };
        var group = new GroupProfileEntity
        {
            RobotConfigId = robot.Id,
            ExternalGroupId = callbackName,
            Name = $"different-{suffix}",
            WorkToolGroupRemark = "configured"
        };
        db.AddRange(robot, group);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = scope.ServiceProvider.GetRequiredService<IDurableJobRepository>();
        var externalMessageId = $"legacy-{suffix}";
        await repository.IngestInboundMessageAsync(new InboundMessageIngestRequest(
            robot.Id,
            externalMessageId,
            $"fallback-{suffix}",
            DateTime.UtcNow,
            callbackName,
            "configured",
            "Alice",
            "question",
            DateTime.UtcNow), TestContext.Current.CancellationToken);
        var messageId = await db.ConversationMessages.Where(item => item.WorkToolMessageId == externalMessageId)
            .Select(item => item.Id).SingleAsync(TestContext.Current.CancellationToken);

        var decision = await scope.ServiceProvider.GetRequiredService<IGroundedConversationRepository>()
            .EvaluateInboundPolicyAsync(messageId, callbackName, "configured", true, TestContext.Current.CancellationToken);

        Assert.Equal(InboundPolicyDecisionKind.NoReply, decision.Kind);
        Assert.Equal("group_rule_unmatched", decision.Reason);
        await DeletePendingJobAsync(db, messageId);
    }

    private static Task<int> DeletePendingJobAsync(
        WechatRobotDbContext database,
        Guid messageId) =>
        database.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM `durable_job` WHERE `RelatedConversationMessageId` = {messageId};",
            TestContext.Current.CancellationToken);

    private ServiceProvider Services() => new ServiceCollection()
        .AddDbContext<WechatRobotDbContext>(options => options.UseMySQL(fixture.ConnectionString))
        .AddScoped<IDurableJobRepository, DurableJobRepository>()
        .AddScoped<IGroundedConversationRepository, GroundedConversationRepository>()
        .AddSingleton<ISecretProtector, PassThroughProtector>()
        .AddScoped<ModelConfigurationService>()
        .AddSingleton<ConversationContextService>()
        .AddSingleton<AnswerOutputFirewall>()
        .AddSingleton(new RetrievalQueryOptions())
        .AddSingleton<RetrievalQueryBuilder>()
        .AddSingleton<IQueryRewriteAgent, PassThroughQueryRewriteAgent>()
        .AddScoped<MultiTurnRetrievalService>()
        .AddSingleton<IConversationSummarizer, NoOpSummarizer>()
        .AddSingleton<CountingEvidence>()
        .AddSingleton<IRetrievalEvidenceProvider>(provider => provider.GetRequiredService<CountingEvidence>())
        .AddSingleton<CountingChat>()
        .AddSingleton<IChatCompletionClient>(provider => provider.GetRequiredService<CountingChat>())
        .AddSingleton(new GroundedAnswerOptions(.7, 8, "insufficient", "failure", "handoff"))
        .AddScoped<GroundedAnswerService>()
        .AddScoped<InboundMessageProcessor>()
        .AddSingleton(TimeProvider.System)
        .BuildServiceProvider();

    private sealed class PassThroughProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string ciphertext) => ciphertext;
    }

    private sealed class CountingEvidence : IRetrievalEvidenceProvider
    {
        private int callCount;
        public int CallCount => Volatile.Read(ref callCount);
        public Task<KnowledgeTagScope> ResolveScopeAsync(IReadOnlyList<Guid> requestedTagIds, CancellationToken token) =>
            Task.FromResult(new KnowledgeTagScope(requestedTagIds, requestedTagIds, "tag_ids:any-of-effective-visible-tags"));
        public Task<IReadOnlyList<RetrievalEvidence>> RetrieveAsync(string question, KnowledgeTagScope scope, int limit, CancellationToken token)
        {
            Interlocked.Increment(ref callCount);
            return Task.FromResult<IReadOnlyList<RetrievalEvidence>>([new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, .99, [], "safe", "evidence")]);
        }
    }

    private sealed class CountingChat : IChatCompletionClient
    {
        private int callCount;
        public int CallCount => Volatile.Read(ref callCount);
        public Task<ChatCompletionResponse> CompleteAsync(ModelProviderConfiguration configuration, ChatCompletionRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref callCount);
            return Task.FromResult(new ChatCompletionResponse("safe answer"));
        }
    }

    private sealed class NoOpSummarizer : IConversationSummarizer
    {
        public Task<string> SummarizeAsync(ModelProviderConfiguration configuration, string? existingSummary, IReadOnlyList<ConversationHistoryMessage> evictedMessages, CancellationToken token) =>
            Task.FromResult(existingSummary ?? "summary");
    }

    private sealed class PassThroughQueryRewriteAgent : IQueryRewriteAgent
    {
        public Task<QueryRewriteResult> RewriteAsync(
            QueryRewriteRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new QueryRewriteResult(
                QueryRewriteDecision.Search,
                request.CurrentQuestion,
                null,
                QueryRewriteReasonCode.StandaloneQuestion));
    }
}
