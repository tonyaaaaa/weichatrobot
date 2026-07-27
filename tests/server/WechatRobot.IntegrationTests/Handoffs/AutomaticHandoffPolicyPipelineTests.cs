using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WechatRobot.Application.Conversations;
using WechatRobot.Application.Handoffs;
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

namespace WechatRobot.IntegrationTests.Handoffs;

public sealed class AutomaticHandoffPolicyPipelineTests : IClassFixture<MySqlFixture>
{
    private readonly MySqlFixture fixture;

    public AutomaticHandoffPolicyPipelineTests(MySqlFixture fixture) => this.fixture = fixture;

    [Theory]
    [InlineData("Group", "stable-a", "Group", null, false)]
    [InlineData("Sender", "stable-a", "Sender", "stable-a", false)]
    [InlineData("Sender", null, "Group", null, true)]
    public async Task Real_pipeline_applies_effective_pause_policy_and_stops_before_ai_send(
        string configuredPolicy,
        string? stableSenderId,
        string expectedScope,
        string? expectedStableSenderId,
        bool expectedDegradation)
    {
        using var services = Services();
        Guid groupId;
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
            await db.ModelConfigs.Where(item => item.ConfigurationType == "chat" && item.IsDefault)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsDefault, false), TestContext.Current.CancellationToken);
            var suffix = Guid.NewGuid().ToString("N");
            var robot = new RobotConfigEntity { Name = $"handoff-{suffix}", WorkToolRobotId = $"handoff-{suffix}", CallbackSecretHash = "test" };
            var group = new GroupProfileEntity
            {
                RobotConfigId = robot.Id,
                ExternalGroupId = $"handoff-group-{suffix}",
                Name = $"handoff-group-{suffix}",
                HandoffPausePolicy = configuredPolicy
            };
            db.AddRange(robot, group, new ModelConfigEntity
            {
                Name = $"chat-{suffix}", NormalizedName = $"CHAT-{suffix.ToUpperInvariant()}", Provider = "fake", ConfigurationType = "chat", BaseUrl = "https://fake.invalid",
                Model = "fake", EncryptedApiKey = "fake", IsDefault = true
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            await scope.ServiceProvider.GetRequiredService<IDurableJobRepository>().IngestInboundMessageAsync(new(
                robot.Id, $"handoff-message-{suffix}", $"handoff-fallback-{suffix}", DateTime.UtcNow, group.Name, null, "Alice",
                "请转人工", DateTime.UtcNow, stableSenderId, true), TestContext.Current.CancellationToken);
            groupId = group.Id;
        }

        Assert.True(await new DurableJobWorker(services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System)
            .ProcessOnceAsync(TestContext.Current.CancellationToken));

        await using var verify = services.CreateAsyncScope();
        var database = verify.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var handoff = await database.HandoffCases.AsNoTracking().SingleAsync(item => item.GroupProfileId == groupId, TestContext.Current.CancellationToken);
        Assert.Equal(expectedScope, handoff.PauseScope);
        Assert.Equal(expectedStableSenderId, handoff.StableSenderId);
        Assert.Equal(expectedDegradation, handoff.EvidenceJson.Contains("stable_sender_id_unavailable_group_pause", StringComparison.Ordinal));
        Assert.Equal(0, await database.SendCommands.CountAsync(item => item.GroupProfileId == groupId &&
            item.IdempotencyKey.StartsWith("grounded-reply:"), TestContext.Current.CancellationToken));
        var handoffService = verify.ServiceProvider.GetRequiredService<HandoffService>();
        Assert.True(await handoffService.IsPausedAsync(groupId, stableSenderId, TestContext.Current.CancellationToken));
        Assert.Equal(expectedScope == "Group",
            await handoffService.IsPausedAsync(groupId, "different-stable-sender", TestContext.Current.CancellationToken));
    }

    private ServiceProvider Services()
    {
        var handoffOptions = new HandoffTriggerOptions(["转人工"], 3);
        return new ServiceCollection()
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
            .AddSingleton<IRetrievalEvidenceProvider, FakeEvidence>()
            .AddSingleton<IChatCompletionClient, FakeChat>()
            .AddSingleton(new GroundedAnswerOptions(.7, 8, "insufficient", "failure", "handoff"))
            .AddScoped<GroundedAnswerService>()
            .AddScoped<IHandoffStore, EfHandoffStore>()
            .AddScoped<HandoffService>()
            .AddSingleton(handoffOptions)
            .AddSingleton<HandoffTriggerEvaluator>()
            .AddScoped<IHandoffOrchestrator, HandoffOrchestrator>()
            .AddScoped<InboundMessageProcessor>()
            .AddSingleton(TimeProvider.System)
            .BuildServiceProvider();
    }

    private sealed class PassThroughProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string ciphertext) => ciphertext;
    }

    private sealed class FakeEvidence : IRetrievalEvidenceProvider
    {
        public Task<KnowledgeTagScope> ResolveScopeAsync(IReadOnlyList<Guid> requestedTagIds, CancellationToken token) =>
            Task.FromResult(new KnowledgeTagScope(requestedTagIds, requestedTagIds, "tag_ids:any-of-effective-visible-tags"));
        public Task<IReadOnlyList<RetrievalEvidence>> RetrieveAsync(string question, KnowledgeTagScope scope, int limit, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<RetrievalEvidence>>([new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, .99, [], "safe", "evidence")]);
    }

    private sealed class FakeChat : IChatCompletionClient
    {
        public Task<ChatCompletionResponse> CompleteAsync(ModelProviderConfiguration configuration, ChatCompletionRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatCompletionResponse("safe answer"));
    }

    private sealed class NoOpSummarizer : IConversationSummarizer
    {
        public Task<string> SummarizeAsync(ModelProviderConfiguration configuration, string? existingSummary, IReadOnlyList<ConversationHistoryMessage> evictedMessages, CancellationToken token) =>
            Task.FromResult(existingSummary ?? "summary");
    }
}
