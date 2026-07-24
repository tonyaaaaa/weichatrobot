namespace WechatRobot.Application.Knowledge;

public sealed record KnowledgeDocumentSummary(
    Guid Id,
    string Title,
    string Status,
    int StateVersion,
    Guid? ActiveVersionId,
    int VersionCount,
    Guid? LatestVersionId,
    int? LatestVersion,
    string? LatestVersionStatus,
    string? LatestFailureReason,
    bool CanRetryUpload,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record KnowledgeDocumentPage(
    IReadOnlyList<KnowledgeDocumentSummary> Items,
    int Total,
    int Page,
    int PageSize);

public sealed record KnowledgeDocumentJobSummary(
    Guid Id,
    string JobType,
    string Status,
    int AttemptCount,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record KnowledgeDocumentIndexJobSummary(
    Guid Id,
    string Operation,
    string Status,
    int AttemptCount,
    bool HasFailure,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record KnowledgeDocumentVersionSummary(
    Guid Id,
    int Version,
    string OriginalFileName,
    string SafeFileName,
    string ContentType,
    long SizeBytes,
    string Status,
    string? FailureReason,
    bool IsPublished,
    bool HasPublicObject,
    int PreviewRevision,
    int PreviewCount,
    int ApprovedChunkCount,
    int OcrPageCount,
    int OcrFailedPageCount,
    IReadOnlyList<KnowledgeDocumentJobSummary> UploadAndParseJobs,
    IReadOnlyList<KnowledgeDocumentIndexJobSummary> IndexJobs,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record KnowledgeDocumentDetail(
    KnowledgeDocumentSummary Document,
    IReadOnlyList<KnowledgeDocumentVersionSummary> Versions);

public sealed record KnowledgeDocumentStateRequest(int ExpectedStateVersion);
