using WechatRobot.Application.Conversations;
using WechatRobot.Application.Models;

namespace WechatRobot.UnitTests.Conversations;

public sealed class ConversationSummarizerTests
{
    [Fact]
    public async Task Summary_prompt_and_output_are_bounded()
    {
        var chat = new FakeChat(new string('s', 500));
        var service = new ChatConversationSummarizer(chat, new ConversationSummaryOptions(16, 80));

        var result = await service.SummarizeAsync(Config(), "old summary",
            [new("user", "scope", new string('x', 500), DateTime.UtcNow)], TestContext.Current.CancellationToken);

        Assert.Equal(80, result.Length);
        Assert.True(chat.Request!.Messages.Sum(message => message.Content.Length) <= 16 * 4 + 300);
    }

    [Fact]
    public async Task Typed_model_failure_is_preserved_for_safe_pipeline_classification()
    {
        var service = new ChatConversationSummarizer(new FakeChat(new ModelUnavailableException("bad response")), new ConversationSummaryOptions());

        await Assert.ThrowsAsync<ModelUnavailableException>(() => service.SummarizeAsync(Config(), null,
            [new("user", "scope", "old", DateTime.UtcNow)], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Summary_prompt_preserves_role_authoritative_participant_attribution()
    {
        var chat = new FakeChat("summary");
        var service = new ChatConversationSummarizer(chat, new ConversationSummaryOptions());

        await service.SummarizeAsync(Config(), null, [
            new("user", "scope", "我偏好结论优先", DateTime.UtcNow, SenderDisplayName: "张伟"),
            new("assistant", "scope", "已经记录", DateTime.UtcNow, SenderDisplayName: "错误成员")
        ], TestContext.Current.CancellationToken);

        var system = Assert.Single(chat.Request!.Messages, message => message.Role == "system");
        var data = Assert.Single(chat.Request.Messages, message => message.Role == "user");
        Assert.DoesNotContain("张伟", system.Content, StringComparison.Ordinal);
        Assert.Contains("UNTRUSTED_CONVERSATION_DATA_BEGIN", data.Content, StringComparison.Ordinal);
        Assert.Contains("张伟: 我偏好结论优先", data.Content, StringComparison.Ordinal);
        Assert.Contains("机器人: 已经记录", data.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("错误成员", data.Content, StringComparison.Ordinal);
        Assert.Contains("observed labels", system.Content, StringComparison.OrdinalIgnoreCase);
    }

    private static ModelProviderConfiguration Config() => new("https://fake.test", "fake", "encrypted", TimeSpan.FromSeconds(1), 0);

    private sealed class FakeChat : IChatCompletionClient
    {
        private readonly string? result;
        private readonly Exception? exception;
        public FakeChat(string result) => this.result = result;
        public FakeChat(Exception exception) => this.exception = exception;
        public ChatCompletionRequest? Request { get; private set; }
        public Task<ChatCompletionResponse> CompleteAsync(ModelProviderConfiguration configuration, ChatCompletionRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            if (exception is not null) throw exception;
            return Task.FromResult(new ChatCompletionResponse(result!));
        }
    }
}
