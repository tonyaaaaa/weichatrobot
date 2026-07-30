namespace WechatRobot.Infrastructure.Persistence.Entities;

public sealed class ConversationMessageEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RobotConfigId { get; set; }
    public Guid? GroupProfileId { get; set; }
    public Guid? ConversationSessionId { get; set; }
    public long? SessionSequence { get; set; }
    public string ProcessingState { get; set; } = "completed";
    public string? TerminalDecision { get; set; }
    public string? TerminalReason { get; set; }
    public string? TerminalEvidenceJson { get; set; }
    public string Direction { get; set; } = "inbound";
    public string Role { get; set; } = "user";
    public Guid? InReplyToMessageId { get; set; }
    public string? WorkToolMessageId { get; set; }
    public string FallbackHash { get; set; } = string.Empty;
    public DateTime FallbackWindowStartUtc { get; set; }
    public string GroupName { get; set; } = string.Empty;
    public string ChannelType { get; set; } = "Group";
    public int? RoomType { get; set; } = 1;
    public string? PeerDisplayName { get; set; }
    public string? ScopeHash { get; set; }
    public string? GroupRemark { get; set; }
    public string SenderDisplayName { get; set; } = string.Empty;
    public string? StableSenderId { get; set; }
    public string Text { get; set; } = string.Empty;
    public DateTime ReceivedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
