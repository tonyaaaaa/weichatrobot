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
