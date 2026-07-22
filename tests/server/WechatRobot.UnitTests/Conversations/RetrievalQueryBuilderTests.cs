using WechatRobot.Application.Conversations;

namespace WechatRobot.UnitTests.Conversations;

public sealed class RetrievalQueryBuilderTests
{
    [Fact]
    public void Follow_up_pronoun_uses_only_allowed_summary_and_recent_context()
    {
        var context = new ConversationContextResult(
            [new("user", "stable:alice", "Tell me about Product A", DateTime.UtcNow, Guid.NewGuid())],
            "We discussed Product A warranty.", false, false, []);

        var result = new RetrievalQueryBuilder(new RetrievalQueryOptions(128)).Build("How long is it?", context);

        Assert.Contains("Product A", result.Query);
        Assert.Contains("How long is it?", result.Query);
        Assert.Single(result.ContextMessageIds);
    }

    [Fact]
    public void Disabled_or_empty_context_uses_current_question_only()
    {
        var result = new RetrievalQueryBuilder(new RetrievalQueryOptions(128)).Build(
            "Current only", new ConversationContextResult([], null, false, false, []));

        Assert.Equal("Current only", result.Query);
        Assert.Empty(result.ContextMessageIds);
    }

    [Fact]
    public void Retrieval_query_is_bounded_by_configured_token_cap()
    {
        var context = new ConversationContextResult(
            [new("user", "stable:alice", new string('x', 400), DateTime.UtcNow, Guid.NewGuid())], null, false, false, []);

        var result = new RetrievalQueryBuilder(new RetrievalQueryOptions(12)).Build("question", context);

        Assert.True(result.Query.Length <= 48);
        Assert.Contains("question", result.Query);
    }
}
