namespace WechatRobot.Application.Knowledge;

public sealed record KnowledgeWorkbenchChunk(
    Guid Id,
    int Sequence,
    string Text,
    int? PageNumber,
    string? Question,
    IReadOnlyList<string> Synonyms,
    string? Answer,
    string Status);

public sealed record KnowledgeWorkbenchSourceEvidence(
    string ChannelType,
    int? RoomType,
    string ActorDisplayName,
    string Text,
    DateTime ReceivedAtUtc);

public sealed record KnowledgeWorkbenchRevisionLink(
    Guid VersionId,
    int Version,
    int PreviewRevision);

public sealed record KnowledgeWorkbenchVersion(
    Guid Id,
    int Version,
    string Status,
    bool IsPublished,
    string SourceKind,
    string? SourceActorDisplayName,
    Guid? SourceBatchId,
    string ChangeKind,
    Guid? SupersedesVersionId,
    IReadOnlyList<KnowledgeDocumentTagSummary> Tags,
    IReadOnlyList<KnowledgeDocumentIndexJobSummary> IndexJobs,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record KnowledgeDocumentWorkbench(
    Guid DocumentId,
    string DocumentTitle,
    string DocumentStatus,
    int DocumentStateVersion,
    Guid? ActiveVersionId,
    KnowledgeWorkbenchVersion Version,
    IReadOnlyList<KnowledgeWorkbenchChunk> Chunks,
    KnowledgeWorkbenchSourceEvidence? SourceEvidence,
    string? SourceEvidenceUnavailableReason,
    KnowledgeWorkbenchRevisionLink? EditableRevision,
    bool CanCreateRevision);

public sealed record CreateKnowledgeRevisionCommand(
    Guid DocumentId,
    Guid SourceVersionId,
    int ExpectedDocumentStateVersion,
    string ActorId,
    string ActorDisplayName);

public sealed record KnowledgeRevisionResult(
    Guid DocumentId,
    Guid VersionId,
    int Version,
    int PreviewRevision);

public sealed class KnowledgeRevisionConflictException(
    string error,
    KnowledgeRevisionResult? existingRevision = null) : Exception(error)
{
    public string Error { get; } = error;
    public KnowledgeRevisionResult? ExistingRevision { get; } = existingRevision;
}

public sealed class KnowledgeRevisionStateException(string error) : Exception(error)
{
    public string Error { get; } = error;
}
