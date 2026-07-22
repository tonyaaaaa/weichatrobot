using WechatRobot.Application.Conversations;
using WechatRobot.Application.Groups;

namespace WechatRobot.UnitTests.Conversations;

public sealed class ConversationContextTests
{
    private static readonly DateTime Now = new(2026, 7, 22, 4, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Default_policy_keeps_six_latest_turns()
    {
        var messages = Enumerable.Range(1, 8).SelectMany(Turn).ToArray();

        var result = new ConversationContextService().Build(messages, Policy(), "alice", Now);

        Assert.Equal(12, result.Messages.Count);
        Assert.DoesNotContain(result.Messages, message => message.Content is "u1" or "a1");
        Assert.Equal("u3", result.Messages[0].Content);
    }

    [Fact]
    public void Sender_isolation_never_includes_another_senders_messages()
    {
        var messages = new[]
        {
            Message("user", "alice", "alice question", Now.AddMinutes(-3)),
            Message("assistant", "alice", "alice answer", Now.AddMinutes(-2)),
            Message("user", "bob", "bob secret", Now.AddMinutes(-1))
        };

        var result = new ConversationContextService().Build(messages, Policy(senderIsolated: true), "alice", Now);

        Assert.DoesNotContain(result.Messages, message => message.Content.Contains("bob", StringComparison.Ordinal));
    }

    [Fact]
    public void Idle_timeout_resets_context()
    {
        var messages = new[] { Message("user", "alice", "old", Now.AddMinutes(-31)) };

        var result = new ConversationContextService().Build(messages, Policy(), "alice", Now);

        Assert.Empty(result.Messages);
        Assert.True(result.WasIdleReset);
    }

    [Fact]
    public void Token_cap_trims_oldest_messages_and_optional_bot_history()
    {
        var messages = new[]
        {
            Message("user", "alice", new string('a', 40), Now.AddMinutes(-3)),
            Message("assistant", "alice", new string('b', 40), Now.AddMinutes(-2)),
            Message("user", "alice", new string('c', 40), Now.AddMinutes(-1))
        };

        var result = new ConversationContextService().Build(messages, Policy(tokenCap: 12, includeBotHistory: false), "alice", Now);

        Assert.Single(result.Messages);
        Assert.Equal(new string('c', 40), result.Messages[0].Content);
        Assert.True(result.WasTokenLimited);
    }

    [Fact]
    public void Bot_history_disabled_keeps_six_user_turns_not_three_pairs()
    {
        var messages = Enumerable.Range(1, 8).SelectMany(Turn).ToArray();

        var result = new ConversationContextService().Build(messages, Policy(includeBotHistory: false), "stable:alice", Now);

        Assert.Equal(["u3", "u4", "u5", "u6", "u7", "u8"], result.Messages.Select(message => message.Content));
    }

    [Fact]
    public void One_oversized_message_is_dropped_to_enforce_token_cap()
    {
        var result = new ConversationContextService().Build(
            [Message("user", "stable:alice", new string('x', 400), Now.AddMinutes(-1))], Policy(tokenCap: 12), "stable:alice", Now);

        Assert.Empty(result.Messages);
        Assert.True(result.WasTokenLimited);
        Assert.Single(result.EvictedMessages);
    }

    [Fact]
    public void Summary_is_included_only_when_enabled()
    {
        var messages = new[] { Message("user", "alice", "recent", Now.AddMinutes(-1)) };

        var enabled = new ConversationContextService().Build(messages, Policy(summaryEnabled: true), "alice", Now, "earlier summary");
        var disabled = new ConversationContextService().Build(messages, Policy(summaryEnabled: false), "alice", Now, "earlier summary");

        Assert.Equal("earlier summary", enabled.Summary);
        Assert.Null(disabled.Summary);
    }

    private static IEnumerable<ConversationHistoryMessage> Turn(int number)
    {
        yield return Message("user", "alice", $"u{number}", Now.AddMinutes(number - 20));
        yield return Message("assistant", "alice", $"a{number}", Now.AddMinutes(number - 20).AddSeconds(1));
    }

    private static ConversationHistoryMessage Message(string role, string sender, string text, DateTime at) => new(role, sender, text, at);
    private static GroupContextSettings Policy(bool senderIsolated = false, int historyTurns = 6, int tokenCap = 3000,
        bool summaryEnabled = true, bool includeBotHistory = true) => new(senderIsolated, historyTurns, 30, tokenCap, summaryEnabled, includeBotHistory);
}
