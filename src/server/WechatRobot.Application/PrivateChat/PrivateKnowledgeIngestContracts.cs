namespace WechatRobot.Application.PrivateChat;

public enum KnowledgeSourceKind { DocumentUpload, ConversationReview, PrivateChatDirect, LegacyUnknown }
public enum KnowledgeChangeKind { New, Duplicate, Supplement, Correction }
public enum PrivateKnowledgeIngestStatus { Received, Extracting, Comparing, Staged, Indexing, Activated, Retryable, Failed }

public sealed record PrivateKnowledgeIngestBatch(
    Guid Id,
    Guid RobotConfigId,
    Guid SourceConversationMessageId,
    int RoomType,
    string SourceActorDisplayName,
    PrivateKnowledgeIngestStatus Status,
    int TotalCount,
    int NewCount,
    int DuplicateCount,
    int SupplementCount,
    int CorrectionCount,
    string? FailureCode,
    int Version,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record ProposedKnowledgeItem(
    string Question,
    string Answer,
    IReadOnlyList<string> ExplicitTags,
    Guid? SuggestedTagId,
    Guid? SimilarVersionId,
    KnowledgeChangeKind ChangeKind);

public sealed class PrivateKnowledgeIngestConcurrencyException()
    : Exception("The private knowledge ingest batch was modified by another process.");

public sealed class PrivateKnowledgeIngestRetryException(string code)
    : Exception(code)
{
    public string Code { get; } = code;
}
