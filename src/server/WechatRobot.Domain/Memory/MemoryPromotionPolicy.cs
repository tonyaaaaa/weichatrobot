namespace WechatRobot.Domain.Memory;

public static class MemoryPromotionPolicy
{
    public const double MinimumConfidence = 0.80;
    public const int MinimumDistinctSessions = 3;
    public const int MinimumDistinctDays = 2;

    public static bool CanPromote(
        MemoryType type,
        bool isExplicit,
        double confidence,
        int distinctSessionCount,
        int distinctDayCount,
        bool hasUnresolvedConflict)
    {
        if (type is MemoryType.BusinessFact ||
            hasUnresolvedConflict ||
            confidence < MinimumConfidence)
        {
            return false;
        }

        return isExplicit ||
               (distinctSessionCount >= MinimumDistinctSessions &&
                distinctDayCount >= MinimumDistinctDays);
    }
}
