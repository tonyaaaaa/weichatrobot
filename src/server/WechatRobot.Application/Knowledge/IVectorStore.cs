namespace WechatRobot.Application.Knowledge;

public enum VectorDistance { Cosine, Dot, Euclid }

public sealed record VectorCollection(string Name, int Dimension, VectorDistance Distance);
public sealed record VectorPoint(Guid Id, Guid DocumentId, Guid VersionId, IReadOnlyList<Guid> TagIds, IReadOnlyList<float> Vector, bool Active, int Generation = 1);
public sealed record VectorSearchRequest(VectorCollection Collection, IReadOnlyList<float> Vector, IReadOnlyList<Guid> EffectiveVisibleTagIds,
    IReadOnlyList<Guid> ActiveVersionIds, int Limit = 8)
{
    public VectorSearchRequest(VectorCollection collection, IReadOnlyList<float> vector, IReadOnlyList<Guid> allowedTagIds,
        IReadOnlyList<Guid> activeVersionIds, Guid? globalPublicTagId, int limit = 8)
        : this(collection, vector,
            allowedTagIds.Concat(globalPublicTagId is { } global ? [global] : []).Distinct().Order().ToArray(),
            activeVersionIds, limit)
    {
    }
}
public sealed record VectorSearchHit(Guid ChunkId, Guid DocumentId, Guid VersionId, double Score);
public sealed record VectorPointMetadata(Guid ChunkId, Guid DocumentId, Guid VersionId, IReadOnlyList<Guid> TagIds, bool Active, int Generation);

public interface IVectorStore
{
    Task EnsureCollectionAsync(VectorCollection collection, CancellationToken cancellationToken);
    Task UpsertAsync(VectorCollection collection, IReadOnlyList<VectorPoint> points, CancellationToken cancellationToken);
    Task SetVersionActiveAsync(VectorCollection collection, Guid versionId, bool active, CancellationToken cancellationToken);
    Task DeleteCollectionAsync(VectorCollection collection, CancellationToken cancellationToken);
    Task<VectorCollection?> InspectCollectionAsync(string collectionName, CancellationToken cancellationToken);
    Task DeleteVersionAsync(VectorCollection collection, Guid versionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<VectorPointMetadata>> InspectVersionAsync(VectorCollection collection, Guid versionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<VectorSearchHit>> SearchAsync(VectorSearchRequest request, CancellationToken cancellationToken);
}

public sealed class VectorStoreUnavailableException(string message, Exception? innerException = null) : Exception(message, innerException);
public sealed class VectorCollectionConfigurationException(string message) : Exception(message);
