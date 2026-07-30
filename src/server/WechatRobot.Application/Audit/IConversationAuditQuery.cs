namespace WechatRobot.Application.Audit;

public sealed record ConversationAuditRequest(Guid? GroupId, string? ChannelType, DateTime? FromUtc, DateTime? ToUtc, int Page, int PageSize);
public sealed record ConversationAuditPage(IReadOnlyList<ConversationAuditItem> Items, int Total, int Page, int PageSize);
public sealed record ConversationAuditItem(
    Guid Id, Guid? GroupProfileId, string ChannelType, Guid MessageId, Guid? ModelConfigurationId, string? WorkToolMessageId, string Question, string? Answer,
    string Decision, double ConfidenceThreshold, double? ConfidenceValue, string ContextPolicy, string? FailureCode,
    string AnswerSource, string? WebSearchFailureCode, string WebSearchSourcesJson,
    string MemoryRecallJson, string EvidenceJson, string InputSummaryJson, ConversationAuditSend? Send,
    ConversationAuditCandidate? KnowledgeCandidate, DateTime CreatedAtUtc);
public sealed record ConversationAuditSend(string Status, int AttemptCount, DateTime? SentAtUtc, DateTime? CompletedAtUtc);
public sealed record ConversationAuditCandidate(
    string Status, Guid? KnowledgeDocumentVersionId, DateTime? PublishedAtUtc, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);

public interface IConversationAuditQuery
{
    Task<ConversationAuditPage> ListAsync(ConversationAuditRequest request, CancellationToken token);
}
