using WechatRobot.Application.Conversations;

namespace WechatRobot.UnitTests.Conversations;

public sealed class PrivateConversationScopeTests
{
    [Fact]
    public void Same_robot_room_type_and_normalized_display_name_share_scope()
    {
        var robotId = Guid.NewGuid();
        var first = PrivateConversationScope.Create(robotId, 4, "  Alice   Chen ");
        var second = PrivateConversationScope.Create(robotId, 4, "alice chen");

        Assert.Equal(first.ScopeHash, second.ScopeHash);
        Assert.Equal("Alice Chen", first.PeerDisplayName);
    }

    [Fact]
    public void Different_room_types_do_not_share_scope()
    {
        var robotId = Guid.NewGuid();

        Assert.NotEqual(
            PrivateConversationScope.Create(robotId, 2, "Alice").ScopeHash,
            PrivateConversationScope.Create(robotId, 4, "Alice").ScopeHash);
    }
}
