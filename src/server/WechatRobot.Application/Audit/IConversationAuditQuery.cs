namespace WechatRobot.Application.Audit;

public sealed record ConversationAuditRequest(Guid? GroupId, DateTime? FromUtc, DateTime? ToUtc, int Page, int PageSize);
public sealed record ConversationAuditPage(IReadOnlyList<ConversationAuditItem> Items, int Total, int Page, int PageSize);
public sealed record ConversationAuditItem(
    Guid Id, Guid GroupProfileId, Guid MessageId, string? WorkToolMessageId, string Question, string? Answer,
    string Decision, double ConfidenceThreshold, double? ConfidenceValue, string ContextPolicy, string? FailureCode,
    string EvidenceJson, string InputSummaryJson, ConversationAuditSend? Send, ConversationAuditHandoff? Handoff,
    ConversationAuditCandidate? KnowledgeCandidate, DateTime CreatedAtUtc);
public sealed record ConversationAuditSend(string Status, int AttemptCount, DateTime? SentAtUtc, DateTime? CompletedAtUtc);
public sealed record ConversationAuditHandoff(
    string State, string ReasonCode, string PauseScope, string EvidenceJson, DateTime CreatedAtUtc, DateTime UpdatedAtUtc,
    IReadOnlyList<ConversationAuditTransition> Transitions);
public sealed record ConversationAuditTransition(int Sequence, string FromState, string ToState, string ReasonCode, DateTime CreatedAtUtc);
public sealed record ConversationAuditCandidate(
    string Status, Guid? KnowledgeDocumentVersionId, DateTime? PublishedAtUtc, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);

public interface IConversationAuditQuery
{
    Task<ConversationAuditPage> ListAsync(ConversationAuditRequest request, CancellationToken token);
}
