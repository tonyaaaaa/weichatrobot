using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using WechatRobot.Application.Agents;
using WechatRobot.Application.Conversations;
using WechatRobot.Application.Models;
using WechatRobot.Infrastructure.Agents;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace WechatRobot.UnitTests.Agents;

public sealed class QueryRewriteAgentTests
{
    [Fact]
    public async Task Follow_up_submission_becomes_structured_search_result()
    {
        var client = new RewriteChatClient(
            "Search",
            "办理日本三年签证需要准备什么材料？",
            null,
            "contextual_follow_up");
        var agent = new QueryRewriteAgent(new StubFactory(client));

        var result = await agent.RewriteAsync(
            Request(History()),
            TestContext.Current.CancellationToken);

        Assert.Equal(QueryRewriteDecision.Search, result.Decision);
        Assert.Equal(
            "办理日本三年签证需要准备什么材料？",
            result.StandaloneQuery);
        Assert.Equal(
            QueryRewriteReasonCode.ContextualFollowUp,
            result.ReasonCode);
        Assert.Null(result.FailureCode);
        Assert.Equal(256, client.LastMaxOutputTokens);
    }

    [Fact]
    public async Task Literal_null_clarification_is_normalized_for_search_submission()
    {
        var agent = new QueryRewriteAgent(new StubFactory(
            new RewriteChatClient(
                "Search",
                "日本三年签证是否可以办理？",
                "null",
                "standalone_question")));

        var result = await agent.RewriteAsync(
            Request(History()),
            TestContext.Current.CancellationToken);

        Assert.Equal(QueryRewriteDecision.Search, result.Decision);
        Assert.Equal(
            "日本三年签证是否可以办理？",
            result.StandaloneQuery);
        Assert.Null(result.ClarificationQuestion);
        Assert.Null(result.FailureCode);
    }

    [Fact]
    public async Task Literal_null_query_is_normalized_for_clarification_submission()
    {
        var agent = new QueryRewriteAgent(new StubFactory(
            new RewriteChatClient(
                "Clarification",
                "null",
                "请确认您咨询的具体签证类型？",
                "ambiguous_reference")));

        var result = await agent.RewriteAsync(
            Request(History()),
            TestContext.Current.CancellationToken);

        Assert.Equal(QueryRewriteDecision.Clarification, result.Decision);
        Assert.Null(result.StandaloneQuery);
        Assert.Equal(
            "请确认您咨询的具体签证类型？",
            result.ClarificationQuestion);
        Assert.Null(result.FailureCode);
    }

    [Fact]
    public async Task Prompt_contains_only_formal_context_with_participant_labels()
    {
        var client = new RewriteChatClient(
            "Clarification",
            null,
            "请确认具体签证类型？",
            "ambiguous_reference");
        var agent = new QueryRewriteAgent(new StubFactory(client));

        await agent.RewriteAsync(
            Request(History()),
            TestContext.Current.CancellationToken);

        Assert.Contains("participant", client.LastPrompt, StringComparison.Ordinal);
        Assert.Contains("用户A", client.LastPrompt, StringComparison.Ordinal);
        Assert.Contains("日本三年签证", client.LastPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "rawSameGroupMessages",
            client.LastPrompt,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Evidence data",
            client.LastPrompt,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Unknown", "contextual_follow_up")]
    [InlineData("Search", "unknown_reason")]
    public async Task Unknown_structured_values_fail_closed(
        string decision,
        string reasonCode)
    {
        var agent = new QueryRewriteAgent(new StubFactory(
            new RewriteChatClient(
                decision,
                "query",
                null,
                reasonCode)));

        var result = await agent.RewriteAsync(
            Request(History()),
            TestContext.Current.CancellationToken);

        Assert.Equal(QueryRewriteDecision.Failure, result.Decision);
        Assert.Equal(QueryRewriteReasonCode.InvalidOutput, result.ReasonCode);
        Assert.Equal("query_rewrite_invalid_output", result.FailureCode);
    }

    [Fact]
    public async Task Provider_exception_returns_stable_failure_without_leaking_message()
    {
        var agent = new QueryRewriteAgent(new StubFactory(
            new ThrowingChatClient(
                new InvalidOperationException("secret upstream body"))));

        var result = await agent.RewriteAsync(
            Request(History()),
            TestContext.Current.CancellationToken);

        Assert.Equal(QueryRewriteDecision.Failure, result.Decision);
        Assert.Equal(QueryRewriteReasonCode.ProviderFailure, result.ReasonCode);
        Assert.Equal("query_rewrite_provider_failure", result.FailureCode);
        Assert.DoesNotContain(
            "secret",
            result.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Caller_cancellation_is_propagated()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var agent = new QueryRewriteAgent(new StubFactory(
            new ThrowingChatClient(
                new OperationCanceledException(cancellation.Token))));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            agent.RewriteAsync(Request(History()), cancellation.Token));
    }

    private static QueryRewriteRequest Request(
        ConversationContextResult context) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            ConversationChannelType.Group,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "group",
            "用户B",
            "需要什么材料？",
            context,
            new ModelProviderConfiguration(
                "https://example.test",
                "chat",
                "secret",
                TimeSpan.FromSeconds(30),
                0),
            Guid.NewGuid());

    private static ConversationContextResult History() =>
        new(
            [
                new ConversationHistoryMessage(
                    "user",
                    "group",
                    "日本三年签证你们能办吗？",
                    DateTime.UtcNow.AddMinutes(-1),
                    Guid.NewGuid(),
                    1,
                    "用户A"),
                new ConversationHistoryMessage(
                    "assistant",
                    "group",
                    "可以办理。",
                    DateTime.UtcNow,
                    Guid.NewGuid(),
                    2,
                    "机器人")
            ],
            null,
            false,
            false);

    private sealed class StubFactory(IChatClient client)
        : IAgentChatClientFactory
    {
        public Task<IChatClient> CreateAsync(
            Guid modelConfigurationId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(client);
    }

    private sealed class RewriteChatClient(
        string decision,
        string? standaloneQuery,
        string? clarificationQuestion,
        string reasonCode) : IChatClient
    {
        private int calls;
        public string LastPrompt { get; private set; } = string.Empty;
        public int? LastMaxOutputTokens { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<AiChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            calls++;
            LastMaxOutputTokens = options?.MaxOutputTokens;
            LastPrompt = string.Join(
                '\n',
                messages.SelectMany(message => message.Contents)
                    .OfType<TextContent>()
                    .Select(content => content.Text));
            if (calls == 1)
            {
                return Task.FromResult(new ChatResponse(new AiChatMessage(
                    ChatRole.Assistant,
                    [
                        new FunctionCallContent(
                            "rewrite-1",
                            "submit_query_rewrite",
                            new Dictionary<string, object?>
                            {
                                ["decision"] = decision,
                                ["standaloneQuery"] = standaloneQuery,
                                ["clarificationQuestion"] = clarificationQuestion,
                                ["reasonCode"] = reasonCode
                            })
                    ])));
            }

            return Task.FromResult(new ChatResponse(
                new AiChatMessage(ChatRole.Assistant, "done")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate>
            GetStreamingResponseAsync(
                IEnumerable<AiChatMessage> messages,
                ChatOptions? options = null,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(
            Type serviceType,
            object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class ThrowingChatClient(Exception exception) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<AiChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ChatResponse>(exception);

        public async IAsyncEnumerable<ChatResponseUpdate>
            GetStreamingResponseAsync(
                IEnumerable<AiChatMessage> messages,
                ChatOptions? options = null,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(
            Type serviceType,
            object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
