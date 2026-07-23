using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WechatRobot.Application.Conversations;
using WechatRobot.Application.Jobs;
using WechatRobot.Application.Knowledge;
using WechatRobot.Application.Messaging;
using WechatRobot.Application.Models;
using WechatRobot.Application.Security;
using WechatRobot.Infrastructure.Conversations;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.IntegrationTests.Infrastructure;
using WechatRobot.Worker.Jobs;

namespace WechatRobot.IntegrationTests.Conversations;

public sealed class RagReplyPipelineTests : IClassFixture<MySqlFixture>
{
    private readonly MySqlFixture fixture;
    public RagReplyPipelineTests(MySqlFixture fixture) => this.fixture = fixture;

    [Fact]
    public async Task Fake_grounded_reply_durably_persists_history_audit_and_send_before_job_completion()
    {
        using var services = Services();
        Guid robotId;
        Guid groupId;
        Guid otherGroupId;
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
            var robot = new RobotConfigEntity { Name = $"rag-{Guid.NewGuid():N}", WorkToolRobotId = $"rag-{Guid.NewGuid():N}", CallbackSecretHash = "test" };
            var group = new GroupProfileEntity { RobotConfigId = robot.Id, ExternalGroupId = "Support", Name = "Support", ContextSenderIsolated = true };
            var other = new GroupProfileEntity { RobotConfigId = robot.Id, ExternalGroupId = "Other", Name = "Other" };
            db.AddRange(robot, group, other, new ModelConfigEntity
            {
                Name = $"chat-{Guid.NewGuid():N}", Provider = "fake", ConfigurationType = "chat", BaseUrl = "https://fake.test",
                Model = "fake", EncryptedApiKey = "fake", IsDefault = true
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            await scope.ServiceProvider.GetRequiredService<IDurableJobRepository>().IngestInboundMessageAsync(
                new(robot.Id, $"rag-message-{Guid.NewGuid():N}", $"rag-fallback-{Guid.NewGuid():N}", DateTime.UtcNow,
                    "Support", "alice", "How long is the warranty?", DateTime.UtcNow, null, true), TestContext.Current.CancellationToken);
            robotId = robot.Id;
            groupId = group.Id;
            otherGroupId = other.Id;
        }

        var worker = new DurableJobWorker(services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
        Assert.True(await worker.ProcessOnceAsync(TestContext.Current.CancellationToken));

        await using var verify = services.CreateAsyncScope();
        var database = verify.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var messages = await database.ConversationMessages.Where(item => item.RobotConfigId == robotId).OrderBy(item => item.CreatedAtUtc).ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, messages.Length);
        Assert.Equal("inbound", messages[0].Direction);
        Assert.Equal("outbound", messages[1].Direction);
        Assert.Equal(messages[0].Id, messages[1].InReplyToMessageId);
        Assert.DoesNotContain("manual.pdf", messages[1].Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await database.RetrievalAudits.CountAsync(item => item.GroupProfileId == groupId && item.EvidenceJson.Contains("manual.pdf"), TestContext.Current.CancellationToken));
        var audit = await database.RetrievalAudits.SingleAsync(item => item.GroupProfileId == groupId, TestContext.Current.CancellationToken);
        Assert.Contains("stable_sender_id_unavailable", audit.InputSummaryJson, StringComparison.Ordinal);
        Assert.DoesNotContain("fake.test", audit.InputSummaryJson, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await database.SendCommands.CountAsync(item => item.GroupProfileId == groupId && item.Status == "pending", TestContext.Current.CancellationToken));
        Assert.Equal(1, await database.DurableJobs.CountAsync(item => item.Status == "completed", TestContext.Current.CancellationToken));
        var repository = verify.ServiceProvider.GetRequiredService<IGroundedConversationRepository>();
        Assert.Equal(2, (await repository.GetHistoryAsync(groupId, 1, 20, TestContext.Current.CancellationToken)).Total);
        Assert.Equal(0, (await repository.GetHistoryAsync(otherGroupId, 1, 20, TestContext.Current.CancellationToken)).Total);

        var clearedAt = DateTime.UtcNow.AddSeconds(1);
        Assert.True(await repository.ClearGroupContextAsync(groupId, clearedAt, TestContext.Current.CancellationToken) > 0);
        await verify.ServiceProvider.GetRequiredService<IDurableJobRepository>().IngestInboundMessageAsync(
            new(robotId, $"after-clear-{Guid.NewGuid():N}", $"after-clear-fallback-{Guid.NewGuid():N}", clearedAt.AddSeconds(1),
                "Support", "alice", "new question", clearedAt.AddSeconds(1), null, true), TestContext.Current.CancellationToken);
        var latest = await database.ConversationMessages.Where(item => item.RobotConfigId == robotId && item.Text == "new question")
            .Select(item => item.Id).SingleAsync(TestContext.Current.CancellationToken);
        var afterClear = await repository.LoadForProcessingAsync(latest, TestContext.Current.CancellationToken);
        Assert.Empty(new ConversationContextService().Build(afterClear.History, afterClear.ContextPolicy, afterClear.Scope.ScopeKey, clearedAt.AddSeconds(1), afterClear.Summary).Messages);
    }

    [Fact]
    public async Task Idle_reset_worker_commit_clears_summary_and_advances_sequence_without_reusing_old_context()
    {
        using var services = Services();
        Guid sessionId;
        Guid newMessageId;
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
            var oldAt = DateTime.UtcNow.AddMinutes(-31);
            var robot = new RobotConfigEntity { Name = $"idle-{Guid.NewGuid():N}", WorkToolRobotId = $"idle-{Guid.NewGuid():N}", CallbackSecretHash = "test" };
            var group = new GroupProfileEntity { RobotConfigId = robot.Id, ExternalGroupId = "IdleSupport", Name = "IdleSupport", ContextSummaryEnabled = true };
            var session = new ConversationSessionEntity
            {
                GroupProfileId = group.Id, SenderScopeKey = "group", Summary = "OLD-SUMMARY-INJECTION", NextSequence = 2,
                LastActivityAtUtc = oldAt, CreatedAtUtc = oldAt, UpdatedAtUtc = oldAt
            };
            var oldUser = new ConversationMessageEntity
            {
                RobotConfigId = robot.Id, GroupProfileId = group.Id, ConversationSessionId = session.Id, SessionSequence = 1, GroupName = group.Name,
                Direction = "inbound", Role = "user", FallbackHash = Guid.NewGuid().ToString("N"), FallbackWindowStartUtc = oldAt,
                SenderDisplayName = "Alice", Text = "OLD-HISTORY-INJECTION", ReceivedAtUtc = oldAt, CreatedAtUtc = oldAt
            };
            var oldAnswer = new ConversationMessageEntity
            {
                RobotConfigId = robot.Id, GroupProfileId = group.Id, ConversationSessionId = session.Id, SessionSequence = 2, GroupName = group.Name,
                Direction = "outbound", Role = "assistant", InReplyToMessageId = oldUser.Id, FallbackHash = Guid.NewGuid().ToString("N"),
                FallbackWindowStartUtc = oldAt, SenderDisplayName = "Alice", Text = "OLD-ANSWER-INJECTION", ReceivedAtUtc = oldAt, CreatedAtUtc = oldAt
            };
            db.AddRange(robot, group, session, oldUser, oldAnswer, new RetrievalAuditEntity
            {
                ConversationMessageId = oldUser.Id, GroupProfileId = group.Id, Decision = "Answer", ConfidenceThreshold = .7,
                ContextPolicy = "historical", EvidenceJson = "[]", InputSummaryJson = "{}", CreatedAtUtc = oldAt
            }, new ModelConfigEntity
            {
                Name = $"chat-{Guid.NewGuid():N}", Provider = "fake", ConfigurationType = "chat", BaseUrl = "https://fake.test",
                Model = "fake", EncryptedApiKey = "fake", IsDefault = true
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            var workToolMessageId = $"idle-message-{Guid.NewGuid():N}";
            Assert.Equal(InboundMessageIngestResult.Accepted, await scope.ServiceProvider.GetRequiredService<IDurableJobRepository>().IngestInboundMessageAsync(
                new(robot.Id, workToolMessageId, $"idle-fallback-{Guid.NewGuid():N}", DateTime.UtcNow,
                    group.Name, "Alice", "current question", DateTime.UtcNow, null, true), TestContext.Current.CancellationToken));
            sessionId = session.Id;
            newMessageId = await db.ConversationMessages.Where(item => item.WorkToolMessageId == workToolMessageId)
                .Select(item => item.Id).SingleAsync(TestContext.Current.CancellationToken);
        }

        var worker = new DurableJobWorker(services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
        var processedTarget = false;
        for (var attempt = 0; attempt < 20 && !processedTarget; attempt++)
        {
            if (!await worker.ProcessOnceAsync(TestContext.Current.CancellationToken)) break;
            await using var check = services.CreateAsyncScope();
            processedTarget = await check.ServiceProvider.GetRequiredService<WechatRobotDbContext>().RetrievalAudits.AsNoTracking()
                .AnyAsync(item => item.ConversationMessageId == newMessageId, TestContext.Current.CancellationToken);
        }
        Assert.True(processedTarget);

        await using var verify = services.CreateAsyncScope();
        var database = verify.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var persisted = await database.ConversationSessions.AsNoTracking().SingleAsync(item => item.Id == sessionId, TestContext.Current.CancellationToken);
        Assert.Null(persisted.Summary);
        Assert.Equal(2, persisted.ClearedThroughSequence);
        Assert.Equal(1, await database.RetrievalAudits.CountAsync(item => item.ConversationMessageId == newMessageId, TestContext.Current.CancellationToken));
        var retrieval = services.GetRequiredService<FakeEvidence>();
        Assert.DoesNotContain(retrieval.Queries, query => query.Contains("OLD-", StringComparison.Ordinal));
        var chat = services.GetRequiredService<FakeChat>();
        Assert.DoesNotContain(chat.Requests.SelectMany(request => request.Messages), message => message.Content.Contains("OLD-", StringComparison.Ordinal));
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
        .AddSingleton<FakeEvidence>()
        .AddSingleton<IRetrievalEvidenceProvider>(provider => provider.GetRequiredService<FakeEvidence>())
        .AddSingleton<FakeChat>()
        .AddSingleton<IChatCompletionClient>(provider => provider.GetRequiredService<FakeChat>())
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

    private sealed class FakeEvidence : IRetrievalEvidenceProvider
    {
        public List<string> Queries { get; } = [];
        public Task<KnowledgeTagScope> ResolveScopeAsync(IReadOnlyList<Guid> requestedTagIds, CancellationToken token) =>
            Task.FromResult(new KnowledgeTagScope(requestedTagIds, requestedTagIds, "tag_ids:any-of-effective-visible-tags"));
        public Task<IReadOnlyList<RetrievalEvidence>> RetrieveAsync(string question, KnowledgeTagScope scope, int limit, CancellationToken token)
        {
            Queries.Add(question);
            return Task.FromResult<IReadOnlyList<RetrievalEvidence>>([new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 4, .94, [], "manual.pdf", "Warranty is two years.")]);
        }
    }

    private sealed class FakeChat : IChatCompletionClient
    {
        public List<ChatCompletionRequest> Requests { get; } = [];
        public Task<ChatCompletionResponse> CompleteAsync(ModelProviderConfiguration configuration, ChatCompletionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new ChatCompletionResponse("Warranty is two years."));
        }
    }

    private sealed class NoOpSummarizer : IConversationSummarizer
    {
        public Task<string> SummarizeAsync(ModelProviderConfiguration configuration, string? existingSummary, IReadOnlyList<ConversationHistoryMessage> evictedMessages, CancellationToken token) =>
            Task.FromResult(existingSummary ?? "summary");
    }
}
