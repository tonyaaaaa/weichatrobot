using WechatRobot.Domain.Memory;

namespace WechatRobot.UnitTests.Memory;

public sealed class MemoryPromotionPolicyTests
{
    [Fact]
    public void Ordinary_behavior_requires_three_sessions_two_days_and_confidence()
    {
        Assert.False(MemoryPromotionPolicy.CanPromote(
            MemoryType.UserPreference, false, 0.79, 3, 2, false));
        Assert.False(MemoryPromotionPolicy.CanPromote(
            MemoryType.UserPreference, false, 0.80, 2, 2, false));
        Assert.False(MemoryPromotionPolicy.CanPromote(
            MemoryType.UserPreference, false, 0.80, 3, 1, false));
        Assert.True(MemoryPromotionPolicy.CanPromote(
            MemoryType.UserPreference, false, 0.80, 3, 2, false));
    }

    [Fact]
    public void Explicit_behavior_can_promote_immediately()
    {
        Assert.True(MemoryPromotionPolicy.CanPromote(
            MemoryType.GroupRule, true, 0.80, 1, 1, false));
    }

    [Fact]
    public void Business_fact_and_unresolved_conflict_never_promote()
    {
        Assert.False(MemoryPromotionPolicy.CanPromote(
            MemoryType.BusinessFact, true, 1, 10, 10, false));
        Assert.False(MemoryPromotionPolicy.CanPromote(
            MemoryType.RobotExperience, true, 1, 10, 10, true));
    }
}
