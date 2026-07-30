using System.Text.Json;
using WechatRobot.Application.Memory;
using WechatRobot.Application.Models;
using WechatRobot.Domain.Memory;
using WechatRobot.Infrastructure.Memory;

namespace WechatRobot.UnitTests.Memory;

public sealed class ChatMemoryExtractorTests
{
    [Fact]
    public async Task Untrusted_payload_preserves_role_authoritative_sender_labels()
    {
        var chat = new FakeChat();
        var extractor = new ChatMemoryExtractor(chat, new MemoryExtractionValidator());
        var scope = MemoryScope.Create(
            MemoryScopeType.User,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "张伟",
            "张伟");

        await extractor.ExtractAsync(
            new ModelProviderConfiguration("https://fake.test", "fake", "encrypted", TimeSpan.FromSeconds(1), 0),
            new MemoryExtractionContext(scope, [
                new(Guid.NewGuid(), "user", "偏好结论优先", DateTime.UtcNow, "张伟"),
                new(Guid.NewGuid(), "assistant", "已经记录", DateTime.UtcNow, "错误成员")
            ]),
            TestContext.Current.CancellationToken);

        var system = Assert.Single(chat.Request!.Messages, message => message.Role == "system");
        var data = Assert.Single(chat.Request.Messages, message => message.Role == "user");
        var json = data.Content[(data.Content.IndexOf('\n') + 1)..data.Content.LastIndexOf('\n')];
        using var payload = JsonDocument.Parse(json);
        var messages = payload.RootElement.GetProperty("messages");
        Assert.DoesNotContain("张伟", system.Content, StringComparison.Ordinal);
        Assert.Equal("张伟", messages[0].GetProperty("senderDisplayName").GetString());
        Assert.Equal("机器人", messages[1].GetProperty("senderDisplayName").GetString());
        Assert.Contains("UNTRUSTED_CONVERSATION_DATA", data.Content, StringComparison.Ordinal);
    }

    private sealed class FakeChat : IChatCompletionClient
    {
        public ChatCompletionRequest? Request { get; private set; }

        public Task<ChatCompletionResponse> CompleteAsync(
            ModelProviderConfiguration configuration,
            ChatCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(new ChatCompletionResponse("""{"memories":[]}"""));
        }
    }
}
