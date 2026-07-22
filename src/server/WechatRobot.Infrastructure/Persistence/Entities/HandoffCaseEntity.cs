namespace WechatRobot.Infrastructure.Persistence.Entities;

public sealed class HandoffCaseEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid QuestionMessageId { get; set; }
    public Guid RobotConfigId { get; set; }
    public Guid GroupProfileId { get; set; }
    public string State { get; set; } = "WaitingHuman";
    public string ReasonCode { get; set; } = string.Empty;
    public string EvidenceJson { get; set; } = "{}";
    public string PauseScope { get; set; } = "Group";
    public string? StableSenderId { get; set; }
    public Guid? AssigneeUserId { get; set; }
    public Guid? ResolvedByUserId { get; set; }
    public string? FinalAnswer { get; set; }
    public int Version { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
