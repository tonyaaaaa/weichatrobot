namespace WechatRobot.Infrastructure.Persistence.Entities;

public sealed class ConversationMessageEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RobotConfigId { get; set; }
    public Guid? GroupProfileId { get; set; }
    public string? WorkToolMessageId { get; set; }
    public string FallbackHash { get; set; } = string.Empty;
    public DateTime FallbackWindowStartUtc { get; set; }
    public string SenderExternalUserId { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public DateTime ReceivedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
