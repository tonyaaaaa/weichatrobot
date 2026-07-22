using System.Text.Json;
using WechatRobot.Application.Conversations;
using WechatRobot.Application.Groups;
using WechatRobot.Application.Jobs;
using WechatRobot.Application.Messaging;
using WechatRobot.Application.Models;

namespace WechatRobot.UnitTests.Conversations;

public sealed class InboundMessageProcessorTests
{
    [Fact]
    public async Task Evicted_history_is_summarized_persisted_and_traced()
    {
        var repository = new FakeRepository(Request());
        var processor = Processor(repository, new FakeSummarizer("bounded summary"));

        await processor.ProcessAsync(Job(repository.Request.MessageId), TestContext.Current.CancellationToken);

        Assert.Equal("bounded summary", repository.Result!.UpdatedSummary);
        Assert.Contains("SummaryHash", repository.Result.Audit.InputSummaryJson, StringComparison.Ordinal);
        Assert.True(repository.RenewCount >= 3);
    }

    [Fact]
    public async Task Summary_provider_failure_continues_without_summary_and_is_audited()
    {
        var repository = new FakeRepository(Request());
        var processor = Processor(repository, new FakeSummarizer(new ModelUnavailableException("summary unavailable")));

        await processor.ProcessAsync(Job(repository.Request.MessageId), TestContext.Current.CancellationToken);

        Assert.Null(repository.Result!.UpdatedSummary);
        Assert.Contains("summary_provider_unavailable", repository.Result.Audit.InputSummaryJson, StringComparison.Ordinal);
        Assert.Equal(AnswerDecisionKind.Answer, repository.Result.Decision.Kind);
    }

    private static InboundMessageProcessor Processor(FakeRepository repository, IConversationSummarizer summarizer)
    {
        var answer = new GroundedAnswerService(new FakeRetrieval(), new FakeChat(), new GroundedAnswerOptions(), new AnswerOutputFirewall());
        return new(repository, new ConversationContextService(), new RetrievalQueryBuilder(new(256)), summarizer, answer, TimeProvider.System);
    }

    private static ConversationProcessingRequest Request()
    {
        var messageId = Guid.NewGuid();
        var scope = new ConversationScope("sender:stable", false, null);
        var history = Enumerable.Range(1, 8).SelectMany(index => new[]
        {
            new ConversationHistoryMessage("user", scope.ScopeKey, $"u{index}", DateTime.UtcNow.AddMinutes(index - 20), Guid.NewGuid()),
            new ConversationHistoryMessage("assistant", scope.ScopeKey, $"a{index}", DateTime.UtcNow.AddMinutes(index - 20).AddSeconds(1), Guid.NewGuid())
        }).ToArray();
        return new(messageId, Guid.NewGuid(), "robot", Guid.NewGuid(), "Support", "Alice", "stable-a", scope, "question", DateTime.UtcNow,
            [], history, null, new GroupContextSettings(true, 2, 30, 3000, true, true),
            new ModelProviderConfiguration("https://fake.test", "fake", "encrypted", TimeSpan.FromSeconds(1), 0), Guid.NewGuid(), Guid.NewGuid(), "lease", 1);
    }

    private static LeasedDurableJob Job(Guid messageId) => new(Guid.NewGuid(), "ProcessInboundMessage",
        JsonSerializer.Serialize(new { messageId, robotConfigId = Guid.NewGuid(), groupName = "Support" }), 0, "durable-owner");

    private sealed class FakeRepository(ConversationProcessingRequest request) : IGroundedConversationRepository
    {
        public ConversationProcessingRequest Request { get; } = request;
        public GroundedAnswerResult? Result { get; private set; }
        public int RenewCount { get; private set; }
        public Task<ConversationProcessingRequest> LoadForProcessingAsync(Guid messageId, CancellationToken token) => Task.FromResult(Request);
        public Task<ConversationProcessingRequest> LeaseForProcessingAsync(Guid messageId, string leaseOwner, DateTime nowUtc, TimeSpan leaseDuration, CancellationToken token) => Task.FromResult(Request with { SessionLeaseOwner = leaseOwner });
        public Task<bool> RenewLeaseAsync(Guid sessionId, string leaseOwner, DateTime nowUtc, TimeSpan leaseDuration, CancellationToken token) { RenewCount++; return Task.FromResult(true); }
        public Task ReleaseLeaseAsync(Guid sessionId, string leaseOwner, CancellationToken token) => Task.CompletedTask;
        public Task PersistAnswerAndEnqueueAsync(ConversationProcessingRequest request, GroundedAnswerResult result, CancellationToken token) { Result = result; return Task.CompletedTask; }
        public Task<int> ClearContextAsync(Guid groupProfileId, string? senderExternalUserId, DateTime clearedAtUtc, CancellationToken token) => Task.FromResult(0);
        public Task<PageResult<ConversationPageItem>> GetHistoryAsync(Guid groupProfileId, int page, int pageSize, CancellationToken token) => throw new NotSupportedException();
        public Task<PageResult<RetrievalAuditPageItem>> GetAuditsAsync(Guid groupProfileId, int page, int pageSize, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class FakeSummarizer : IConversationSummarizer
    {
        private readonly string? result;
        private readonly Exception? exception;
        public FakeSummarizer(string result) => this.result = result;
        public FakeSummarizer(Exception exception) => this.exception = exception;
        public Task<string> SummarizeAsync(ModelProviderConfiguration configuration, string? existingSummary, IReadOnlyList<ConversationHistoryMessage> evictedMessages, CancellationToken token)
        {
            if (exception is not null) throw exception;
            return Task.FromResult(result!);
        }
    }

    private sealed class FakeRetrieval : IRetrievalEvidenceProvider
    {
        public Task<IReadOnlyList<RetrievalEvidence>> RetrieveAsync(string question, IReadOnlyList<Guid> allowedTagIds, int limit, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<RetrievalEvidence>>([new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, .9, [], "internal", "evidence")]);
    }

    private sealed class FakeChat : IChatCompletionClient
    {
        public Task<ChatCompletionResponse> CompleteAsync(ModelProviderConfiguration configuration, ChatCompletionRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatCompletionResponse("clean answer"));
    }
}
