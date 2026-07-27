namespace WechatRobot.Application.Knowledge;

public sealed record KnowledgeTagRecord(
    Guid Id,
    string Name,
    bool IsEnabled,
    bool IsGlobalPublic,
    int Version,
    DateTime CreatedAtUtc);

public sealed record KnowledgeTagPage(
    IReadOnlyList<KnowledgeTagRecord> Items,
    int Total,
    int Page,
    int PageSize);

public sealed record KnowledgeTagOption(
    Guid Id,
    string Name,
    bool IsGlobalPublic);

public sealed record KnowledgeTagDraft(
    string Name,
    bool IsGlobalPublic);

public sealed record KnowledgeTagUpdate(
    string Name,
    bool IsGlobalPublic,
    int ExpectedVersion);

public sealed record KnowledgeTagStateUpdate(
    bool IsEnabled,
    int ExpectedVersion);

public enum KnowledgeTagMutationStatus
{
    Succeeded,
    InvalidInput,
    NotFound,
    NameConflict,
    ConcurrencyConflict,
    Referenced
}

public sealed record KnowledgeTagMutationResult(
    KnowledgeTagMutationStatus Status,
    KnowledgeTagRecord? Tag = null,
    KnowledgeTagReferenceSummary? References = null,
    string? Error = null);

public sealed record KnowledgeTagReferenceSummary(
    int Groups,
    int Chunks,
    int Reviews,
    int IndexJobs)
{
    public bool IsReferenced => Groups + Chunks + Reviews + IndexJobs > 0;
}
