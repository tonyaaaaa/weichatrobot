namespace WechatRobot.Application.Agents;

public enum IntentRuntimeMode { Legacy, Shadow, AgentFramework, Paused }
public enum AnswerRuntimeMode { Legacy, Shadow, AgentFramework }
public enum PrivateChatRuntimeMode { Disabled, AgentFramework }
public enum TemplateRoutingRuntimeMode { Disabled, Shadow, AgentFramework }

public sealed class AgentRuntimeOptions
{
    public const string SectionName = "AgentRuntime";

    public IntentRuntimeMode IntentRuntimeMode { get; init; } = IntentRuntimeMode.Legacy;
    public AnswerRuntimeMode AnswerRuntimeMode { get; init; } = AnswerRuntimeMode.Legacy;
    public PrivateChatRuntimeMode PrivateChatRuntimeMode { get; init; } = PrivateChatRuntimeMode.AgentFramework;
    public TemplateRoutingRuntimeMode TemplateRoutingRuntimeMode { get; init; } = TemplateRoutingRuntimeMode.AgentFramework;
    public Guid? IntentModelConfigurationId { get; init; }
    public decimal IntentMinimumConfidence { get; init; } = .8m;
    public int IntentTimeoutSeconds { get; init; } = 15;
    public int IntentHistoryMessageCount { get; init; } = 12;
    public int IntentHistoryMinutes { get; init; } = 10;
    public int IntentMaximumInputCharacters { get; init; } = 6000;

    public void Validate()
    {
        if (!Enum.IsDefined(IntentRuntimeMode)
            || !Enum.IsDefined(AnswerRuntimeMode)
            || !Enum.IsDefined(PrivateChatRuntimeMode)
            || !Enum.IsDefined(TemplateRoutingRuntimeMode)
            || IntentMinimumConfidence is < 0 or > 1
            || IntentTimeoutSeconds is < 1 or > 120
            || IntentHistoryMessageCount is < 1 or > 50
            || IntentHistoryMinutes is < 1 or > 1440
            || IntentMaximumInputCharacters is < 256 or > 32000)
        {
            throw new InvalidOperationException("Agent runtime options are invalid.");
        }
    }
}
