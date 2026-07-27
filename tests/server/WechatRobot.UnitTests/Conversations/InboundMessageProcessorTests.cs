using System.Text.Json;
using WechatRobot.Application.Conversations;
using WechatRobot.Application.Groups;
using WechatRobot.Application.Jobs;
using WechatRobot.Application.Knowledge;
using WechatRobot.Application.Messaging;
using WechatRobot.Application.Models;
using WechatRobot.Application.Handoffs;

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

    [Fact]
    public async Task Idle_reset_does_not_summarize_or_send_old_summary_or_history_to_retrieval_and_model()
    {
        var now = DateTime.UtcNow;
        var request = Request() with
        {
            ReceivedAtUtc = now,
            History = [new("user", "sender:stable", "OLD-HISTORY-INJECTION", now.AddMinutes(-31), Guid.NewGuid())],
            Summary = "OLD-SUMMARY-INJECTION"
        };
        var repository = new FakeRepository(request);
        var summarizer = new FakeSummarizer("must not run");
        var retrieval = new FakeRetrieval();
        var chat = new FakeChat();
        var processor = new InboundMessageProcessor(repository, new ConversationContextService(), new RetrievalQueryBuilder(new(256)), summarizer,
            new GroundedAnswerService(retrieval, chat, new GroundedAnswerOptions(), new AnswerOutputFirewall()), TimeProvider.System);

        await processor.ProcessAsync(Job(request.MessageId), TestContext.Current.CancellationToken);

        Assert.True(repository.Result!.ResetContextBeforeCurrent);
        Assert.Null(repository.Result.UpdatedSummary);
        Assert.Equal(0, summarizer.CallCount);
        Assert.DoesNotContain("OLD-", retrieval.Query, StringComparison.Ordinal);
        Assert.DoesNotContain(chat.LastRequest!.Messages, message => message.Content.Contains("OLD-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Handoff_trigger_suppresses_the_calculated_ai_answer_and_commits_typed_handoff_terminal()
    {
        var repository = new FakeRepository(Request());
        var handoff = new FakeHandoff { Trigger = true };
        var answer = new GroundedAnswerService(new FakeRetrieval(), new FakeChat(), new GroundedAnswerOptions(), new AnswerOutputFirewall());
        var processor = new InboundMessageProcessor(repository, new ConversationContextService(), new RetrievalQueryBuilder(new(256)),
            new FakeSummarizer("summary"), answer, TimeProvider.System, handoff);

        await processor.ProcessAsync(Job(repository.Request.MessageId), TestContext.Current.CancellationToken);

        Assert.Null(repository.Result);
        Assert.NotNull(repository.HandoffResult);
        Assert.Equal(AnswerDecisionKind.Handoff, repository.HandoffResult!.Decision.Kind);
    }

    [Fact]
    public async Task Explicit_transfer_starts_handoff_before_retrieval_or_model_work()
    {
        var repository = new FakeRepository(Request() with { Question = "请转人工" });
        var handoff = new FakeHandoff { ExplicitTrigger = true };
        var retrieval = new FakeRetrieval();
        var chat = new FakeChat();
        var processor = new InboundMessageProcessor(repository, new ConversationContextService(), new RetrievalQueryBuilder(new(256)),
            new FakeSummarizer("summary"), new GroundedAnswerService(retrieval, chat, new GroundedAnswerOptions(), new AnswerOutputFirewall()),
            TimeProvider.System, handoff);

        await processor.ProcessAsync(Job(repository.Request.MessageId), TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, retrieval.Query);
        Assert.Null(chat.LastRequest);
        Assert.Null(repository.Result);
        Assert.Equal("explicit_transfer", repository.HandoffResult?.Audit.FailureCode);
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
        public GroundedAnswerResult? HandoffResult { get; private set; }
        public int RenewCount { get; private set; }
        public Task<InboundPolicyDecision> EvaluateInboundPolicyAsync(Guid messageId, string groupName, string? groupRemark, bool wasMentioned, CancellationToken token) =>
            Task.FromResult(new InboundPolicyDecision(messageId, InboundPolicyDecisionKind.Proceed, Request.GroupProfileId, null, "{}"));
        public Task PersistNoReplyTerminalAsync(InboundPolicyDecision decision, CancellationToken token) => Task.CompletedTask;
        public Task<ConversationProcessingRequest> LoadForProcessingAsync(Guid messageId, CancellationToken token) => Task.FromResult(Request);
        public Task<ConversationProcessingRequest> LeaseForProcessingAsync(Guid messageId, string leaseOwner, DateTime nowUtc, TimeSpan leaseDuration, CancellationToken token) => Task.FromResult(Request with { SessionLeaseOwner = leaseOwner });
        public Task<bool> RenewLeaseAsync(Guid sessionId, string leaseOwner, DateTime nowUtc, TimeSpan leaseDuration, CancellationToken token) { RenewCount++; return Task.FromResult(true); }
        public Task ReleaseLeaseAsync(Guid sessionId, string leaseOwner, CancellationToken token) => Task.CompletedTask;
        public Task PersistAnswerAndEnqueueAsync(ConversationProcessingRequest request, GroundedAnswerResult result, CancellationToken token) { Result = result; return Task.CompletedTask; }
        public Task PersistHandoffTerminalAsync(ConversationProcessingRequest request, GroundedAnswerResult result, CancellationToken token) { HandoffResult = result; return Task.CompletedTask; }
        public Task<int> ClearGroupContextAsync(Guid groupProfileId, DateTime clearedAtUtc, CancellationToken token) => Task.FromResult(0);
        public Task<PageResult<ConversationPageItem>> GetHistoryAsync(Guid groupProfileId, int page, int pageSize, CancellationToken token) => throw new NotSupportedException();
        public Task<PageResult<RetrievalAuditPageItem>> GetAuditsAsync(Guid groupProfileId, int page, int pageSize, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class FakeHandoff : IHandoffOrchestrator
    {
        public bool Trigger { get; init; }
        public bool ExplicitTrigger { get; init; }
        public Task<bool> IsPausedAsync(ConversationProcessingRequest request, CancellationToken token) => Task.FromResult(false);
        public Task<bool> TryStartExplicitAsync(ConversationProcessingRequest request, CancellationToken token) => Task.FromResult(ExplicitTrigger);
        public Task<bool> HandleDecisionAsync(ConversationProcessingRequest request, GroundedAnswerResult result, CancellationToken token) => Task.FromResult(Trigger);
    }

    private sealed class FakeSummarizer : IConversationSummarizer
    {
        private readonly string? result;
        private readonly Exception? exception;
        public FakeSummarizer(string result) => this.result = result;
        public FakeSummarizer(Exception exception) => this.exception = exception;
        public int CallCount { get; private set; }
        public Task<string> SummarizeAsync(ModelProviderConfiguration configuration, string? existingSummary, IReadOnlyList<ConversationHistoryMessage> evictedMessages, CancellationToken token)
        {
            CallCount++;
            if (exception is not null) throw exception;
            return Task.FromResult(result!);
        }
    }

    private sealed class FakeRetrieval : IRetrievalEvidenceProvider
    {
        public string Query { get; private set; } = string.Empty;
        public Task<KnowledgeTagScope> ResolveScopeAsync(IReadOnlyList<Guid> requestedTagIds, CancellationToken token) =>
            Task.FromResult(new KnowledgeTagScope(requestedTagIds, requestedTagIds, "tag_ids:any-of-effective-visible-tags"));
        public Task<IReadOnlyList<RetrievalEvidence>> RetrieveAsync(string question, KnowledgeTagScope scope, int limit, CancellationToken token)
        {
            Query = question;
            return Task.FromResult<IReadOnlyList<RetrievalEvidence>>([new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, .9, [], "internal", "evidence")]);
        }
    }

    private sealed class FakeChat : IChatCompletionClient
    {
        public ChatCompletionRequest? LastRequest { get; private set; }
        public Task<ChatCompletionResponse> CompleteAsync(ModelProviderConfiguration configuration, ChatCompletionRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(new ChatCompletionResponse("clean answer"));
        }
    }
}
