namespace WechatRobot.Application.Knowledge;

public enum VectorDistance { Cosine, Dot, Euclid }

public sealed record VectorCollection(string Name, int Dimension, VectorDistance Distance);
public sealed record VectorPoint(Guid Id, Guid DocumentId, Guid VersionId, IReadOnlyList<Guid> TagIds, IReadOnlyList<float> Vector, bool Active);
public sealed record VectorSearchRequest(VectorCollection Collection, IReadOnlyList<float> Vector, IReadOnlyList<Guid> AllowedTagIds,
    IReadOnlyList<Guid> ActiveVersionIds, Guid? GlobalPublicTagId, int Limit = 8);
public sealed record VectorSearchHit(Guid ChunkId, Guid DocumentId, Guid VersionId, double Score);

public interface IVectorStore
{
    Task EnsureCollectionAsync(VectorCollection collection, CancellationToken cancellationToken);
    Task UpsertAsync(VectorCollection collection, IReadOnlyList<VectorPoint> points, CancellationToken cancellationToken);
    Task SetVersionActiveAsync(VectorCollection collection, Guid versionId, bool active, CancellationToken cancellationToken);
    Task DeleteVersionAsync(VectorCollection collection, Guid versionId, CancellationToken cancellationToken);
    Task<long> CountVersionAsync(VectorCollection collection, Guid versionId, bool activeOnly, CancellationToken cancellationToken);
    Task<IReadOnlyList<VectorSearchHit>> SearchAsync(VectorSearchRequest request, CancellationToken cancellationToken);
}

public sealed class VectorStoreUnavailableException(string message, Exception? innerException = null) : Exception(message, innerException);
public sealed class VectorCollectionConfigurationException(string message) : Exception(message);
