namespace WechatRobot.Infrastructure.Persistence.Entities;

public sealed class MemoryObservationEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MemoryCandidateId { get; set; }
    public Guid ConversationSessionId { get; set; }
    public Guid ConversationMessageId { get; set; }
    public string SourceContentHash { get; set; } = string.Empty;
    public string EvidenceSummary { get; set; } = string.Empty;
    public DateTime ObservedAtUtc { get; set; }
    public Guid ModelConfigurationId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
