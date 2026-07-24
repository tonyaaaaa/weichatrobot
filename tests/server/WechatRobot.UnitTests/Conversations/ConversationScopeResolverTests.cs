using WechatRobot.Application.Conversations;

namespace WechatRobot.UnitTests.Conversations;

public sealed class ConversationScopeResolverTests
{
    [Fact]
    public void Same_display_name_without_stable_ids_never_shares_context()
    {
        var first = ConversationScopeResolver.Resolve(true, null, Guid.NewGuid());
        var second = ConversationScopeResolver.Resolve(true, null, Guid.NewGuid());

        Assert.NotEqual(first.ScopeKey, second.ScopeKey);
        Assert.True(first.IsStatelessDegradation);
        Assert.Equal("stable_sender_id_unavailable", first.DegradationReason);
    }

    [Fact]
    public void Rename_with_same_valid_stable_id_keeps_session_scope()
    {
        var first = ConversationScopeResolver.Resolve(true, "external-user-42", Guid.NewGuid());
        var renamed = ConversationScopeResolver.Resolve(true, "external-user-42", Guid.NewGuid());

        Assert.Equal(first.ScopeKey, renamed.ScopeKey);
        Assert.False(first.IsStatelessDegradation);
    }

    [Fact]
    public void Group_shared_policy_is_unaffected_by_missing_sender_id()
    {
        var first = ConversationScopeResolver.Resolve(false, null, Guid.NewGuid());
        var second = ConversationScopeResolver.Resolve(false, null, Guid.NewGuid());

        Assert.Equal("group", first.ScopeKey);
        Assert.Equal(first.ScopeKey, second.ScopeKey);
        Assert.False(first.IsStatelessDegradation);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad id with spaces")]
    [InlineData("用户/42")]
    public void Invalid_connector_id_degrades_to_stateless(string value)
    {
        Assert.True(ConversationScopeResolver.Resolve(true, value, Guid.NewGuid()).IsStatelessDegradation);
    }
}
