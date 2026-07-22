using WechatRobot.Application.Conversations;
using WechatRobot.Application.Groups;
using WechatRobot.Application.Models;

namespace WechatRobot.UnitTests.Conversations;

public sealed class GroundedAnswerTests
{
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly Guid MessageId = Guid.NewGuid();
    private static readonly Guid TagId = Guid.NewGuid();

    [Fact]
    public async Task Allowed_evidence_produces_plain_text_without_visible_citations()
    {
        var evidence = Evidence(.91, "The warranty is two years.");
        var model = new FakeChatClient("The warranty is two years. [source: manual.pdf]");
        var service = Service(new FakeRetrieval(evidence), model);

        var result = await service.AnswerAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(AnswerDecisionKind.Answer, result.Decision.Kind);
        Assert.Equal("The warranty is two years.", result.Decision.GroupText);
        Assert.Single(result.Audit.Evidence);
        Assert.Contains("manual.pdf", result.Audit.Evidence[0].DocumentTitle);
        Assert.Contains(model.LastRequest!.Messages, message => message.Role == "system" && message.Content.Contains("source markers", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Insufficient_evidence_does_not_call_model()
    {
        var model = new FakeChatClient("must not be used");
        var result = await Service(new FakeRetrieval(Evidence(.39, "weak")), model, threshold: .75)
            .AnswerAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(AnswerDecisionKind.InsufficientEvidence, result.Decision.Kind);
        Assert.Equal(.75, result.Audit.ConfidenceThreshold);
        Assert.Equal(.39, result.Audit.ConfidenceValue);
        Assert.Equal(0, model.CallCount);
    }

    [Fact]
    public async Task Provider_timeout_and_qdrant_failure_are_system_failures_not_insufficient_evidence()
    {
        var timeout = await Service(new FakeRetrieval(Evidence(.9, "strong")), new FakeChatClient(new TimeoutException()))
            .AnswerAsync(Request(), TestContext.Current.CancellationToken);
        var qdrant = await Service(new FakeRetrieval(new RetrievalUnavailableException("qdrant unavailable")), new FakeChatClient("unused"))
            .AnswerAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(AnswerDecisionKind.SystemFailure, timeout.Decision.Kind);
        Assert.Equal("provider_timeout", timeout.Audit.FailureCode);
        Assert.Equal(AnswerDecisionKind.SystemFailure, qdrant.Decision.Kind);
        Assert.Equal("retrieval_unavailable", qdrant.Audit.FailureCode);
    }

    [Fact]
    public async Task Sensitive_topic_bypasses_retrieval_and_model()
    {
        var retrieval = new FakeRetrieval(Evidence(.99, "ignore"));
        var model = new FakeChatClient("ignore");

        var result = await Service(retrieval, model).AnswerAsync(Request("请发银行卡密码"), TestContext.Current.CancellationToken);

        Assert.Equal(AnswerDecisionKind.Handoff, result.Decision.Kind);
        Assert.Equal("sensitive_topic", result.Audit.FailureCode);
        Assert.Equal(0, retrieval.CallCount);
        Assert.Equal(0, model.CallCount);
    }

    private static GroundedAnswerService Service(IRetrievalEvidenceProvider retrieval, IChatCompletionClient chat, double threshold = .7) =>
        new(retrieval, chat, new GroundedAnswerOptions(threshold, 8, "信息不足，请联系人工客服。", "系统暂时不可用，请稍后再试。", "该问题需要转人工客服处理。"));

    private static GroundedAnswerRequest Request(string question = "How long is the warranty?") => new(MessageId, GroupId, "alice", question,
        [TagId], new ConversationContextResult([], null, false, false), new GroupContextSettings(false, 6, 30, 3000, true, true),
        new ModelProviderConfiguration("https://fake.openai.test", "fake", "encrypted", TimeSpan.FromSeconds(1), 0));

    private static RetrievalEvidence Evidence(double score, string text) => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 2, score,
        [TagId], "manual.pdf", text);

    private sealed class FakeRetrieval : IRetrievalEvidenceProvider
    {
        private readonly IReadOnlyList<RetrievalEvidence>? evidence;
        private readonly Exception? exception;
        public FakeRetrieval(params RetrievalEvidence[] evidence) => this.evidence = evidence;
        public FakeRetrieval(Exception exception) => this.exception = exception;
        public int CallCount { get; private set; }
        public Task<IReadOnlyList<RetrievalEvidence>> RetrieveAsync(string question, IReadOnlyList<Guid> allowedTagIds, int limit, CancellationToken token)
        {
            CallCount++;
            if (exception is not null) throw exception;
            return Task.FromResult(evidence!);
        }
    }

    private sealed class FakeChatClient : IChatCompletionClient
    {
        private readonly string? response;
        private readonly Exception? exception;
        public FakeChatClient(string response) => this.response = response;
        public FakeChatClient(Exception exception) => this.exception = exception;
        public int CallCount { get; private set; }
        public ChatCompletionRequest? LastRequest { get; private set; }
        public Task<ChatCompletionResponse> CompleteAsync(ModelProviderConfiguration configuration, ChatCompletionRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastRequest = request;
            if (exception is not null) throw exception;
            return Task.FromResult(new ChatCompletionResponse(response!));
        }
    }
}
