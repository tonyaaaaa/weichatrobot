using WechatRobot.Domain.Handoffs;

namespace WechatRobot.UnitTests.Handoffs;

public sealed class HandoffStateMachineTests
{
    [Fact]
    public void Case_follows_the_supported_lifecycle_and_duplicate_commands_are_idempotent()
    {
        var handoff = HandoffCase.Start(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "需要人工", "{}", PauseScope.Group, null, DateTime.UtcNow);

        Assert.Equal(HandoffState.WaitingHuman, handoff.State);
        Assert.True(handoff.Assign(Guid.NewGuid(), DateTime.UtcNow));
        Assert.Equal(HandoffState.HumanHandling, handoff.State);
        Assert.True(handoff.Resolve("最终答案", DateTime.UtcNow));
        Assert.Equal(HandoffState.Resolved, handoff.State);
        Assert.False(handoff.Resolve("最终答案", DateTime.UtcNow));
        Assert.True(handoff.RestoreAi(DateTime.UtcNow));
        Assert.Equal(HandoffState.AIActive, handoff.State);
        Assert.False(handoff.RestoreAi(DateTime.UtcNow));
    }

    [Fact]
    public void Invalid_transition_is_rejected()
    {
        var handoff = HandoffCase.Start(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "reason", "[]", PauseScope.Group, null, DateTime.UtcNow);

        Assert.Throws<InvalidHandoffTransitionException>(() => handoff.Resolve("answer", DateTime.UtcNow));
    }

    [Fact]
    public void Sender_pause_requires_a_stable_sender_identifier_and_never_uses_display_name()
    {
        Assert.Throws<ArgumentException>(() => HandoffCase.Start(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "reason", "[]", PauseScope.Sender, null, DateTime.UtcNow));

        var groupId = Guid.NewGuid();
        var handoff = HandoffCase.Start(Guid.NewGuid(), groupId, Guid.NewGuid(), "reason", "[]", PauseScope.Sender, "stable-1", DateTime.UtcNow);

        Assert.True(handoff.IsPaused(groupId, "stable-1"));
        Assert.False(handoff.IsPaused(groupId, "员工显示名"));
        Assert.False(handoff.IsPaused(groupId, null));
    }
}
