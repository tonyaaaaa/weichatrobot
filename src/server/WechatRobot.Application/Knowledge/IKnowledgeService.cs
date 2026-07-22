using WechatRobot.Application.Models;

namespace WechatRobot.Application.Knowledge;

public sealed record KnowledgeIndexChunk(Guid Id, Guid DocumentId, Guid VersionId, string Text, IReadOnlyList<Guid> TagIds);
public sealed record KnowledgeIndexWork(Guid JobId, Guid DocumentId, Guid VersionId, Guid? PreviousActiveVersionId,
    string CollectionName, int Dimension, VectorDistance Distance, IReadOnlyList<KnowledgeIndexChunk> Chunks,
    string? LeaseOwner = null, int Generation = 1, string? PreviousActiveCollectionName = null,
    int? PreviousActiveEmbeddingDimension = null, VectorDistance? PreviousActiveDistance = null, bool IsCollectionExclusive = false,
    bool PreviousActiveCollectionExclusive = false);

public interface IKnowledgeService
{
    Task<KnowledgeIndexWork> LoadIndexWorkAsync(Guid jobId, CancellationToken cancellationToken);
    Task<ModelProviderConfiguration> LoadEmbeddingConfigurationAsync(CancellationToken cancellationToken);
    Task<bool> ActivateVersionAsync(KnowledgeIndexWork work, CancellationToken cancellationToken);
    Task<bool> IsIndexLeaseOwnedAsync(Guid jobId, string owner, CancellationToken cancellationToken);
    Task EnqueueCleanupAsync(KnowledgeIndexWork work, CancellationToken cancellationToken);
    Task MarkIndexFailedAsync(Guid jobId, string? leaseOwner, string reason, bool retryable, CancellationToken cancellationToken);
}
