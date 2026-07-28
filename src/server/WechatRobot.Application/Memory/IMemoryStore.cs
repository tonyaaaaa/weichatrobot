using WechatRobot.Domain.Memory;
using WechatRobot.Application.Models;

namespace WechatRobot.Application.Memory;

public sealed record MemoryObservationDraft(
    Guid ConversationSessionId,
    Guid ConversationMessageId,
    string SourceContentHash,
    string EvidenceSummary,
    DateTime ObservedAtUtc,
    Guid ModelConfigurationId);

public sealed record MemoryCandidateDraft(
    MemoryScope Scope,
    MemoryType Type,
    string Content,
    string NormalizedKey,
    string Fingerprint,
    double Confidence,
    bool IsExplicit,
    Guid? SupersedesMemoryEntryId = null,
    bool HasUnresolvedConflict = false);

public sealed record ActiveMemorySummary(Guid Id, string Content);

public sealed record MemoryOrganizationResult(
    Guid CandidateId,
    string Status,
    Guid? MemoryEntryId,
    Guid? KnowledgeCandidateId);

public interface IMemoryStore
{
    Task<IReadOnlyList<ActiveMemorySummary>> FindActiveAsync(
        MemoryScope scope,
        MemoryType type,
        int limit,
        CancellationToken cancellationToken = default);

    Task<MemoryOrganizationResult> ObserveAsync(
        MemoryCandidateDraft candidate,
        MemoryObservationDraft observation,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);
}

public enum MemoryRelationship
{
    Same,
    Related,
    Conflict,
    Unrelated
}

public interface IMemoryRelationshipClassifier
{
    Task<IReadOnlyDictionary<Guid, MemoryRelationship>> ClassifyAsync(
        ModelProviderConfiguration configuration,
        string newContent,
        IReadOnlyList<ActiveMemorySummary> existing,
        CancellationToken cancellationToken = default);
}
