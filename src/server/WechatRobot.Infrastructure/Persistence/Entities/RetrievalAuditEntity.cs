namespace WechatRobot.Infrastructure.Persistence.Entities;

public sealed class RetrievalAuditEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConversationMessageId { get; set; }
    public Guid GroupProfileId { get; set; }
    public Guid? ModelConfigurationId { get; set; }
    public string Decision { get; set; } = string.Empty;
    public double ConfidenceThreshold { get; set; }
    public double? ConfidenceValue { get; set; }
    public string ContextPolicy { get; set; } = string.Empty;
    public string? FailureCode { get; set; }
    public string EvidenceJson { get; set; } = "[]";
    public string InputSummaryJson { get; set; } = "{}";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
