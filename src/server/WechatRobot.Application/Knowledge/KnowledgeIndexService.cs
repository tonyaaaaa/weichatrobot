using WechatRobot.Application.Models;

namespace WechatRobot.Application.Knowledge;

public sealed record KnowledgeIndexOptions(
    int Dimension,
    VectorDistance Distance,
    int BatchSize = 20,
    int MaximumAttempts = 3,
    int MaximumCollectionsPerSearch = 64)
{
    public const string SectionName = "KnowledgeIndex";
    public string CollectionName => $"kb_{Distance.ToString().ToLowerInvariant()}_{Dimension}";

    public void Validate()
    {
        if (Dimension <= 0 || BatchSize <= 0 || MaximumAttempts <= 0 || MaximumCollectionsPerSearch is < 1 or > 256)
            throw new InvalidOperationException("Knowledge index configuration is invalid.");
    }
}

public sealed class EmbeddingDimensionMismatchException(int expected, int actual)
    : Exception($"Embedding dimension mismatch. Expected {expected}, received {actual}.")
{
    public int Expected { get; } = expected;
    public int Actual { get; } = actual;
}
public sealed class KnowledgeActivationConflictException : Exception;
public sealed class KnowledgeSearchCapacityException(int eligibleCollectionCount, int maximumCollections)
    : Exception($"Knowledge search requires {eligibleCollectionCount} eligible collections, exceeding the configured limit of {maximumCollections}.")
{
    public int EligibleCollectionCount { get; } = eligibleCollectionCount;
    public int MaximumCollections { get; } = maximumCollections;
}

public sealed class KnowledgeIndexService(
    IEmbeddingClient embeddingClient,
    IVectorStore vectorStore,
    IKnowledgeService knowledge,
    KnowledgeIndexOptions options)
{
    public async Task IndexAsync(Guid jobId, CancellationToken cancellationToken)
    {
        KnowledgeIndexWork? work = null;
        try
        {
            work = await knowledge.LoadIndexWorkAsync(jobId, cancellationToken);
            Validate(work);
            var collection = new VectorCollection(work.CollectionName, work.Dimension, work.Distance);
            await vectorStore.EnsureCollectionAsync(collection, cancellationToken);
            var provider = await knowledge.LoadEmbeddingConfigurationAsync(
                work.ModelConfigurationId,
                work.ModelConfigurationVersion,
                cancellationToken);
            await EnsureLeaseOwnedAsync(work, collection);

            foreach (var batch in work.Chunks.Chunk(options.BatchSize))
            {
                var response = await embeddingClient.CreateEmbeddingsAsync(provider,
                    new EmbeddingBatchRequest(batch.Select(chunk => chunk.Text).ToArray()), cancellationToken);
                if (response.Vectors.Count != batch.Length)
                    throw new InvalidDataException($"Embedding response count mismatch. Expected {batch.Length}, received {response.Vectors.Count}.");
                var points = new List<VectorPoint>(batch.Length);
                for (var index = 0; index < batch.Length; index++)
                {
                    var chunk = batch[index];
                    var vector = response.Vectors[index];
                    if (vector.Count != work.Dimension)
                        throw new EmbeddingDimensionMismatchException(work.Dimension, vector.Count);
                    points.Add(new VectorPoint(chunk.Id, chunk.DocumentId, chunk.VersionId, chunk.TagIds, vector, false, work.Generation));
                }
                await RetryVectorAsync(() => vectorStore.UpsertAsync(collection, points, cancellationToken));
                await EnsureLeaseOwnedAsync(work, collection);
            }

            await RetryVectorAsync(() => vectorStore.SetVersionActiveAsync(collection, work.VersionId, true, cancellationToken));
            await EnsureLeaseOwnedAsync(work, collection);
            if (!await knowledge.CompleteIndexAsync(work, cancellationToken))
            {
                await vectorStore.SetVersionActiveAsync(collection, work.VersionId, false, cancellationToken);
                throw new KnowledgeActivationConflictException();
            }
            await knowledge.EnqueueCleanupAsync(work, cancellationToken);
        }
        catch (EmbeddingDimensionMismatchException exception)
        {
            await knowledge.MarkIndexFailedAsync(jobId, work?.LeaseOwner, exception.Message, false, CancellationToken.None);
            throw;
        }
        catch (VectorStoreUnavailableException exception)
        {
            await knowledge.MarkIndexFailedAsync(jobId, work?.LeaseOwner, exception.Message, true, CancellationToken.None);
            throw;
        }
        catch (VectorCollectionConfigurationException exception)
        {
            await knowledge.MarkIndexFailedAsync(jobId, work?.LeaseOwner, exception.Message, false, CancellationToken.None);
            throw;
        }
        catch (InvalidDataException exception)
        {
            await knowledge.MarkIndexFailedAsync(jobId, work?.LeaseOwner, exception.Message, false, CancellationToken.None);
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            await knowledge.MarkIndexFailedAsync(jobId, work?.LeaseOwner, exception.Message, true, CancellationToken.None);
            throw;
        }
    }

    private async Task EnsureLeaseOwnedAsync(KnowledgeIndexWork work, VectorCollection collection)
    {
        if (work.LeaseOwner is not null && await knowledge.IsIndexLeaseOwnedAsync(work.JobId, work.LeaseOwner, CancellationToken.None)) return;
        if (work.IsCollectionExclusive) await vectorStore.DeleteCollectionAsync(collection, CancellationToken.None);
        else await vectorStore.DeleteVersionAsync(collection, work.VersionId, CancellationToken.None);
        throw new KnowledgeActivationConflictException();
    }

    private void Validate(KnowledgeIndexWork work)
    {
        if (work.Dimension <= 0 || options.BatchSize <= 0 || options.MaximumAttempts <= 0)
            throw new InvalidOperationException("Knowledge index options are invalid.");
        var expectedCollection = $"kb_{options.Distance.ToString().ToLowerInvariant()}_{work.Dimension}";
        if (work.Distance != options.Distance ||
            !(work.CollectionName == expectedCollection || work.CollectionName.StartsWith(expectedCollection + "_g", StringComparison.Ordinal)))
            throw new VectorCollectionConfigurationException("The queued index job does not match the configured collection. Explicit reindex is required.");
    }

    private async Task RetryVectorAsync(Func<Task> operation)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { await operation(); return; }
            catch (VectorStoreUnavailableException) when (attempt < options.MaximumAttempts) { }
        }
    }
}
