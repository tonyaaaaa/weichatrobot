using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
                Name = $"chat-{suffix}", Provider = "fake", ConfigurationType = "chat", BaseUrl = "https://fake.invalid",
                Model = "fake", EncryptedApiKey = "fake", IsDefault = true
            });
            db.GroupRules.AddRange(persistedRules);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            var externalMessageId = $"rules-message-{suffix}";
            await scope.ServiceProvider.GetRequiredService<IDurableJobRepository>().IngestInboundMessageAsync(new(
                robot.Id, externalMessageId, $"rules-fallback-{suffix}", DateTime.UtcNow, inboundGroupName, "Alice", "question",
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
            Assert.Equal(1, await database.SendCommands.CountAsync(item => item.GroupProfileId == groupId, TestContext.Current.CancellationToken));
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
            var suffix = Guid.NewGuid().ToString("N");
            var visibleName = $"visible-{suffix}";
            var robot = new RobotConfigEntity { Name = suffix, WorkToolRobotId = suffix, CallbackSecretHash = "test" };
            var intended = new GroupProfileEntity { RobotConfigId = robot.Id, ExternalGroupId = $"actual-{suffix}", Name = visibleName };
            var hijacker = new GroupProfileEntity { RobotConfigId = robot.Id, ExternalGroupId = visibleName, Name = $"unrelated-{suffix}" };
            db.AddRange(robot, intended, hijacker,
                new GroupRuleEntity { GroupProfileId = intended.Id, RuleKind = 0, IncludePattern = visibleName, IncludePatternKind = (int)GroupRulePatternKind.Exact },
                new GroupRuleEntity { GroupProfileId = hijacker.Id, RuleKind = 0, IncludePattern = hijacker.Name, IncludePatternKind = (int)GroupRulePatternKind.Exact },
                new ModelConfigEntity { Name = $"chat-{suffix}", Provider = "fake", ConfigurationType = "chat", BaseUrl = "https://fake.invalid", Model = "fake", EncryptedApiKey = "fake", IsDefault = true });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            await scope.ServiceProvider.GetRequiredService<IDurableJobRepository>().IngestInboundMessageAsync(new(
                robot.Id, $"message-{suffix}", $"fallback-{suffix}", DateTime.UtcNow, visibleName, "Alice", "question", DateTime.UtcNow,
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
}
