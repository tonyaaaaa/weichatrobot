namespace WechatRobot.Infrastructure.Persistence.Entities;

public sealed class MemoryCandidateEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ScopeType { get; set; } = "User";
    public string ScopeHash { get; set; } = string.Empty;
    public Guid? RobotConfigId { get; set; }
    public Guid? GroupProfileId { get; set; }
    public string? SubjectKey { get; set; }
    public string? SubjectDisplayName { get; set; }
    public string MemoryType { get; set; } = "UserPreference";
    public string Content { get; set; } = string.Empty;
    public string NormalizedKey { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public bool IsExplicit { get; set; }
    public int ObservationCount { get; set; }
    public int DistinctSessionCount { get; set; }
    public int DistinctDayCount { get; set; }
    public bool HasUnresolvedConflict { get; set; }
    public string Status { get; set; } = "pending";
    public Guid? PromotedMemoryEntryId { get; set; }
    public Guid? KnowledgeCandidateId { get; set; }
    public int Version { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
