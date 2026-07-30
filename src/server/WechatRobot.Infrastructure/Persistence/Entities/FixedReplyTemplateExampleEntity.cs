namespace WechatRobot.Infrastructure.Persistence.Entities;

public sealed class FixedReplyTemplateExampleEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TemplateId { get; set; }
    public string ExampleText { get; set; } = string.Empty;
    public string NormalizedText { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}
