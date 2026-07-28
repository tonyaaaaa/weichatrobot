namespace WechatRobot.Infrastructure.Persistence.Entities;

public sealed class MemoryEntryEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ScopeType { get; set; } = "User";
    public Guid? RobotConfigId { get; set; }
    public Guid? GroupProfileId { get; set; }
    public string? SubjectKey { get; set; }
    public string? SubjectDisplayName { get; set; }
    public string MemoryType { get; set; } = "UserPreference";
    public string Content { get; set; } = string.Empty;
    public string NormalizedKey { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public string Status { get; set; } = "active";
    public Guid? SupersedesMemoryEntryId { get; set; }
    public Guid? SourceCandidateId { get; set; }
    public DateTime ValidFromUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public int RecallCount { get; set; }
    public DateTime? LastRecalledAtUtc { get; set; }
    public int StatusVersion { get; set; }
    public int IndexGeneration { get; set; }
    public int Version { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
