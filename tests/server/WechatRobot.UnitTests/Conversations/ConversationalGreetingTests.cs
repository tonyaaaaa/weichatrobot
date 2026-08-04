using WechatRobot.Application.Conversations;

namespace WechatRobot.UnitTests.Conversations;

public sealed class ConversationalGreetingTests
{
    [Theory]
    [InlineData("你好")]
    [InlineData("您好！")]
    [InlineData(" 嗨 ")]
    [InlineData("哈喽。")]
    [InlineData("HELLO")]
    [InlineData("hi?")]
    [InlineData("在吗？")]
    public void Exact_greeting_returns_deterministic_answer(string text)
    {
        var matched = ConversationalGreeting.TryCreate(text, out var result);

        Assert.True(matched);
        Assert.Equal(AnswerDecisionKind.Answer, result.Decision.Kind);
        Assert.Equal("您好！请问有什么签证问题需要咨询？", result.Decision.GroupText);
        Assert.Equal("conversational_greeting", result.Audit.AnswerSource);
        Assert.Empty(result.Audit.WebSearchSources ?? []);
    }

    [Theory]
    [InlineData("你好，日本三年签证怎么办")]
    [InlineData("hi 日本签证")]
    [InlineData("在吗，韩国签证需要什么材料")]
    [InlineData("participant")]
    [InlineData("")]
    public void Business_question_or_empty_text_does_not_match(string text)
    {
        Assert.False(ConversationalGreeting.TryCreate(text, out _));
    }
}
