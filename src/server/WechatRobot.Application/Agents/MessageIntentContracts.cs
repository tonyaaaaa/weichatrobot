namespace WechatRobot.Application.Agents;

public enum IntentDecision { Reply, NoReply, Uncertain }

public enum IntentCategory
{
    DirectedToBot,
    FollowUpToBot,
    HumanConversation,
    SocialChatter,
    Uncertain
}

public sealed record MessageIntentRequest(
    Guid MessageId,
    Guid GroupProfileId,
    bool WasMentioned);

public sealed record MessageIntentResult(
    IntentDecision Decision,
    IntentCategory Category,
    string ReasonCode,
    decimal Confidence,
    string? FailureCode,
    Guid? ModelConfigurationId = null,
    int? ModelConfigurationVersion = null,
    int LatencyMilliseconds = 0,
    string AgentVersion = "message-intent-v1");

public interface IMessageIntentAgent
{
    Task<MessageIntentResult> DecideAsync(
        MessageIntentRequest request,
        CancellationToken cancellationToken);
}

public sealed record MessageIntentAuditRecord(
    Guid MessageId,
    Guid GroupProfileId,
    IntentRuntimeMode RuntimeMode,
    MessageIntentResult Result,
    bool FormalConversationIncluded,
    DateTime DecidedAtUtc);

public interface IMessageIntentAuditStore
{
    Task RecordAsync(
        MessageIntentAuditRecord record,
        CancellationToken cancellationToken);
}

public sealed record MessageIntentDiagnosticsRequest(
    Guid? GroupProfileId,
    IntentRuntimeMode? RuntimeMode,
    IntentDecision? Decision,
    DateTime? FromUtc,
    DateTime? ToUtc,
    int Page,
    int PageSize);

public sealed record MessageIntentDiagnosticsItem(
    Guid Id,
    Guid ConversationMessageId,
    Guid GroupProfileId,
    string GroupName,
    string SenderDisplayName,
    IntentDecision Decision,
    IntentCategory Category,
    string ReasonCode,
    decimal Confidence,
    string? FailureCode,
    IntentRuntimeMode RuntimeMode,
    string AgentVersion,
    Guid? ModelConfigurationId,
    int? ModelConfigurationVersion,
    int LatencyMilliseconds,
    bool FormalConversationIncluded,
    DateTime DecidedAtUtc);

public sealed record MessageIntentDiagnosticsPage(
    IReadOnlyList<MessageIntentDiagnosticsItem> Items,
    int Total,
    int Page,
    int PageSize);

public interface IMessageIntentDiagnosticsQuery
{
    Task<MessageIntentDiagnosticsPage> ListAsync(
        MessageIntentDiagnosticsRequest request,
        CancellationToken cancellationToken);
}
