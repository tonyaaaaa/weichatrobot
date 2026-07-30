namespace WechatRobot.Infrastructure.Persistence.Entities;

public sealed class PrivateKnowledgeIngestBatchEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RobotConfigId { get; set; }
    public Guid SourceConversationMessageId { get; set; }
    public int RoomType { get; set; }
    public string SourceActorDisplayName { get; set; } = string.Empty;
    public string Status { get; set; } = "Received";
    public Guid? ModelConfigurationId { get; set; }
    public int? ModelConfigurationVersion { get; set; }
    public int TotalCount { get; set; }
    public int NewCount { get; set; }
    public int DuplicateCount { get; set; }
    public int SupplementCount { get; set; }
    public int CorrectionCount { get; set; }
    public string? FailureCode { get; set; }
    public string ReceivedNotificationState { get; set; } = "Pending";
    public string FinalNotificationState { get; set; } = "Pending";
    public int Version { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
