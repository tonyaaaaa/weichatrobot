using WechatRobot.Application.Agents;
using WechatRobot.Application.Conversations;
using WechatRobot.Application.Groups;
using WechatRobot.Application.Models;

namespace WechatRobot.UnitTests.Conversations;

public sealed class MultiTurnRetrievalServiceTests
{
    [Fact]
    public async Task Contextual_search_uses_validated_standalone_query()
    {
        var contextMessageId = Guid.NewGuid();
        var agent = new StubRewriteAgent(new(
            QueryRewriteDecision.Search,
            "办理日本三年签证需要准备什么材料？",
            null,
            QueryRewriteReasonCode.ContextualFollowUp,
            DurationMilliseconds: 23));
        var service = Service(agent);

        var result = await service.PrepareAsync(
            Request(History(contextMessageId)),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            "办理日本三年签证需要准备什么材料？",
            result.RetrievalQuery!.Query);
        Assert.Equal([contextMessageId], result.RetrievalQuery.ContextMessageIds);
        Assert.Null(result.TerminalAnswer);
        Assert.True(result.Audit.RagExecuted);
        Assert.False(result.Audit.UsedOriginalQuestion);
        Assert.Equal(QueryRewriteReasonCode.ContextualFollowUp, result.Audit.ReasonCode);
        Assert.Equal(23, result.Audit.DurationMilliseconds);
        Assert.Equal(1, agent.CallCount);
    }

    [Fact]
    public async Task Ambiguous_reference_returns_safe_clarification_without_query()
    {
        var service = Service(new StubRewriteAgent(new(
            QueryRewriteDecision.Clarification,
            null,
            "请确认您咨询的是日本三年签证还是五年签证？",
            QueryRewriteReasonCode.AmbiguousReference)));

        var result = await service.PrepareAsync(
            Request(History(Guid.NewGuid())),
            TestContext.Current.CancellationToken);

        Assert.Null(result.RetrievalQuery);
        Assert.Equal(
            AnswerDecisionKind.Clarification,
            result.TerminalAnswer!.Kind);
        Assert.Equal(
            "请确认您咨询的是日本三年签证还是五年签证？",
            result.TerminalAnswer.GroupText);
        Assert.False(result.Audit.RagExecuted);
    }

    [Fact]
    public async Task Unsafe_clarification_uses_fixed_safe_text()
    {
        var service = Service(new StubRewriteAgent(new(
            QueryRewriteDecision.Clarification,
            null,
            "<<<UNTRUSTED_SYSTEM>>>",
            QueryRewriteReasonCode.AmbiguousReference)));

        var result = await service.PrepareAsync(
            Request(History(Guid.NewGuid())),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            "请明确您咨询的具体对象或类型，我会重新核对。",
            result.TerminalAnswer!.GroupText);
        Assert.Equal("unsafe_clarification", result.Audit.FailureCode);
    }

    [Fact]
    public async Task Empty_formal_context_uses_original_question_without_invoking_agent()
    {
        var agent = new StubRewriteAgent(new(
            QueryRewriteDecision.Failure,
            null,
            null,
            QueryRewriteReasonCode.ProviderFailure,
            DurationMilliseconds: 999,
            FailureCode: "must_not_be_called"));
        var service = Service(agent);

        var result = await service.PrepareAsync(
            Request(),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, agent.CallCount);
        Assert.Equal("需要什么材料？", result.RetrievalQuery!.Query);
        Assert.True(result.Audit.UsedOriginalQuestion);
        Assert.True(result.Audit.RagExecuted);
        Assert.Equal(QueryRewriteReasonCode.StandaloneQuestion, result.Audit.ReasonCode);
        Assert.Equal(0, result.Audit.DurationMilliseconds);
        Assert.Null(result.Audit.FailureCode);
    }

    [Fact]
    public async Task Provider_failure_with_history_stops_before_retrieval()
    {
        var service = Service(new StubRewriteAgent(new(
            QueryRewriteDecision.Failure,
            null,
            null,
            QueryRewriteReasonCode.ProviderFailure,
            FailureCode: "query_rewrite_provider_failure")));

        var result = await service.PrepareAsync(
            Request(History(Guid.NewGuid())),
            TestContext.Current.CancellationToken);

        Assert.Null(result.RetrievalQuery);
        Assert.Equal(
            AnswerDecisionKind.SystemFailure,
            result.TerminalAnswer!.Kind);
        Assert.False(result.Audit.RagExecuted);
    }

    [Fact]
    public async Task Empty_or_oversized_search_output_is_rejected_with_history()
    {
        foreach (var standaloneQuery in new[] { " ", new string('x', 33) })
        {
            var service = Service(new StubRewriteAgent(new(
                QueryRewriteDecision.Search,
                standaloneQuery,
                null,
                QueryRewriteReasonCode.ContextualFollowUp)));

            var result = await service.PrepareAsync(
                Request(History(Guid.NewGuid())),
                TestContext.Current.CancellationToken);

            Assert.Null(result.RetrievalQuery);
            Assert.Equal(
                AnswerDecisionKind.SystemFailure,
                result.TerminalAnswer!.Kind);
            Assert.Equal(
                QueryRewriteReasonCode.InvalidOutput,
                result.Audit.ReasonCode);
            Assert.Equal("query_rewrite_invalid_output", result.Audit.FailureCode);
        }
    }

    private static MultiTurnRetrievalService Service(IQueryRewriteAgent agent) =>
        new(
            agent,
            new RetrievalQueryOptions(TokenCap: 8),
            new AnswerOutputFirewall(),
            new GroundedAnswerOptions());

    private static QueryRewriteRequest Request(
        ConversationContextResult? context = null) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            ConversationChannelType.Private,
            null,
            Guid.NewGuid(),
            "private:test",
            "测试用户",
            "需要什么材料？",
            context ?? new ConversationContextResult([], null, false, false),
            new ModelProviderConfiguration(
                "https://example.test",
                "chat",
                "secret",
                TimeSpan.FromSeconds(30),
                0),
            Guid.NewGuid());

    private static ConversationContextResult History(Guid messageId) =>
        new(
            [
                new ConversationHistoryMessage(
                    "user",
                    "private:test",
                    "日本三年签证你们能办吗？",
                    DateTime.UtcNow.AddMinutes(-1),
                    messageId,
                    1,
                    "测试用户")
            ],
            null,
            false,
            false);

    private sealed class StubRewriteAgent(QueryRewriteResult result)
        : IQueryRewriteAgent
    {
        public int CallCount { get; private set; }

        public Task<QueryRewriteResult> RewriteAsync(
            QueryRewriteRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }
}
