namespace WechatRobot.Infrastructure.Persistence.Entities;

public sealed class HandoffMessageEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HandoffCaseId { get; set; }
    public string? ExternalMessageId { get; set; }
    public string SenderDisplayName { get; set; } = string.Empty;
    public Guid? AuthenticatedUserId { get; set; }
    public string AuthenticationKind { get; set; } = "worktool_display_name_unverified";
    public string Text { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}
