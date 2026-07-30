namespace WechatRobot.Infrastructure.Persistence.Entities;

public sealed class MessageIntentAuditEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConversationMessageId { get; set; }
    public Guid GroupProfileId { get; set; }
    public string IntentDecision { get; set; } = string.Empty;
    public string IntentCategory { get; set; } = string.Empty;
    public string IntentReasonCode { get; set; } = string.Empty;
    public decimal IntentConfidence { get; set; }
    public string? FailureCode { get; set; }
    public string IntentRuntimeMode { get; set; } = string.Empty;
    public string IntentAgentVersion { get; set; } = string.Empty;
    public Guid? IntentModelConfigurationId { get; set; }
    public int? IntentModelVersion { get; set; }
    public int IntentLatencyMilliseconds { get; set; }
    public bool FormalConversationIncluded { get; set; }
    public DateTime IntentDecidedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
