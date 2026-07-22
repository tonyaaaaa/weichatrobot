using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WechatRobot.Application.Conversations;
using WechatRobot.Application.Jobs;
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
                    "Support", "alice", "How long is the warranty?", DateTime.UtcNow), TestContext.Current.CancellationToken);
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
        Assert.Equal(1, await database.SendCommands.CountAsync(item => item.GroupProfileId == groupId && item.Status == "pending", TestContext.Current.CancellationToken));
        Assert.Equal(1, await database.DurableJobs.CountAsync(item => item.Status == "completed", TestContext.Current.CancellationToken));
        var repository = verify.ServiceProvider.GetRequiredService<IGroundedConversationRepository>();
        Assert.Equal(2, (await repository.GetHistoryAsync(groupId, 1, 20, TestContext.Current.CancellationToken)).Total);
        Assert.Equal(0, (await repository.GetHistoryAsync(otherGroupId, 1, 20, TestContext.Current.CancellationToken)).Total);

        var clearedAt = DateTime.UtcNow.AddSeconds(1);
        Assert.True(await repository.ClearContextAsync(groupId, "alice", clearedAt, TestContext.Current.CancellationToken) > 0);
        await verify.ServiceProvider.GetRequiredService<IDurableJobRepository>().IngestInboundMessageAsync(
            new(robotId, $"after-clear-{Guid.NewGuid():N}", $"after-clear-fallback-{Guid.NewGuid():N}", clearedAt.AddSeconds(1),
                "Support", "alice", "new question", clearedAt.AddSeconds(1)), TestContext.Current.CancellationToken);
        var latest = await database.ConversationMessages.Where(item => item.RobotConfigId == robotId && item.Text == "new question")
            .Select(item => item.Id).SingleAsync(TestContext.Current.CancellationToken);
        var afterClear = await repository.LoadForProcessingAsync(latest, TestContext.Current.CancellationToken);
        Assert.Empty(new ConversationContextService().Build(afterClear.History, afterClear.ContextPolicy, "alice", clearedAt.AddSeconds(1), afterClear.Summary).Messages);
    }

    private ServiceProvider Services() => new ServiceCollection()
        .AddDbContext<WechatRobotDbContext>(options => options.UseMySQL(fixture.ConnectionString))
        .AddScoped<IDurableJobRepository, DurableJobRepository>()
        .AddScoped<IGroundedConversationRepository, GroundedConversationRepository>()
        .AddSingleton<ISecretProtector, PassThroughProtector>()
        .AddScoped<ModelConfigurationService>()
        .AddSingleton<ConversationContextService>()
        .AddSingleton<IRetrievalEvidenceProvider, FakeEvidence>()
        .AddSingleton<IChatCompletionClient, FakeChat>()
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
        public Task<IReadOnlyList<RetrievalEvidence>> RetrieveAsync(string question, IReadOnlyList<Guid> allowedTagIds, int limit, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<RetrievalEvidence>>([new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 4, .94, [], "manual.pdf", "Warranty is two years.")]);
    }

    private sealed class FakeChat : IChatCompletionClient
    {
        public Task<ChatCompletionResponse> CompleteAsync(ModelProviderConfiguration configuration, ChatCompletionRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatCompletionResponse("Warranty is two years. [source: manual.pdf]"));
    }
}
