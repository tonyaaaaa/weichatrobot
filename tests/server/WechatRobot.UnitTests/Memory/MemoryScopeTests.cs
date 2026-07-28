using WechatRobot.Domain.Memory;

namespace WechatRobot.UnitTests.Memory;

public sealed class MemoryScopeTests
{
    [Fact]
    public void Global_scope_rejects_identity_parts()
    {
        Assert.Throws<ArgumentException>(() =>
            MemoryScope.Create(MemoryScopeType.Global, Guid.NewGuid(), null, null, null));
    }

    [Fact]
    public void Robot_scope_requires_robot()
    {
        Assert.Throws<ArgumentException>(() =>
            MemoryScope.Create(MemoryScopeType.Robot, null, null, null, null));
    }

    [Fact]
    public void Group_scope_requires_robot_and_group()
    {
        Assert.Throws<ArgumentException>(() =>
            MemoryScope.Create(MemoryScopeType.Group, Guid.NewGuid(), null, null, null));
    }

    [Fact]
    public void User_scope_normalizes_subject_without_guessing_identity()
    {
        var scope = MemoryScope.Create(
            MemoryScopeType.User,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "  Ａlice  ",
            "Ａlice");

        Assert.Equal("alice", scope.SubjectKey);
        Assert.Equal("Alice", scope.SubjectDisplayName);
    }
}
