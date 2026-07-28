namespace WechatRobot.Domain.Memory;

public enum MemoryType
{
    UserPreference,
    GroupRule,
    RobotExperience,
    BusinessFact
}

public enum MemoryCandidateStatus
{
    Pending,
    Accumulating,
    Promoted,
    RoutedToKnowledge,
    Rejected,
    Expired
}

public sealed record MemoryCandidate(
    Guid Id,
    MemoryScope Scope,
    MemoryType Type,
    string Content,
    string NormalizedKey,
    double Confidence,
    bool IsExplicit,
    int ObservationCount,
    int DistinctSessionCount,
    int DistinctDayCount,
    MemoryCandidateStatus Status,
    int Version);
