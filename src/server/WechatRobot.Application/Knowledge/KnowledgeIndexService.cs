using WechatRobot.Application.Models;

namespace WechatRobot.Application.Knowledge;

public sealed record KnowledgeIndexOptions(int Dimension, VectorDistance Distance, int BatchSize = 32, int MaximumAttempts = 3)
{
    public const string SectionName = "KnowledgeIndex";
    public string CollectionName => $"kb_{Distance.ToString().ToLowerInvariant()}_{Dimension}";
}

public sealed class EmbeddingDimensionMismatchException(int expected, int actual)
    : Exception($"Embedding dimension mismatch. Expected {expected}, received {actual}.");
public sealed class KnowledgeActivationConflictException : Exception;

public sealed class KnowledgeIndexService(
    IEmbeddingClient embeddingClient,
    IVectorStore vectorStore,
    IKnowledgeService knowledge,
    KnowledgeIndexOptions options)
{
    public async Task IndexAsync(Guid jobId, CancellationToken cancellationToken)
    {
        try
        {
            var work = await knowledge.LoadIndexWorkAsync(jobId, cancellationToken);
            Validate(work);
            var collection = new VectorCollection(work.CollectionName, work.Dimension, work.Distance);
            await vectorStore.EnsureCollectionAsync(collection, cancellationToken);
            var provider = await knowledge.LoadEmbeddingConfigurationAsync(cancellationToken);

            foreach (var batch in work.Chunks.Chunk(options.BatchSize))
            {
                var points = new List<VectorPoint>(batch.Length);
                foreach (var chunk in batch)
                {
                    var response = await embeddingClient.CreateEmbeddingAsync(provider, new EmbeddingRequest(chunk.Text), cancellationToken);
                    if (response.Vector.Count != work.Dimension)
                        throw new EmbeddingDimensionMismatchException(work.Dimension, response.Vector.Count);
                    points.Add(new VectorPoint(chunk.Id, chunk.DocumentId, chunk.VersionId, chunk.TagIds, response.Vector, false));
                }
                await RetryVectorAsync(() => vectorStore.UpsertAsync(collection, points, cancellationToken));
            }

            await RetryVectorAsync(() => vectorStore.SetVersionActiveAsync(collection, work.VersionId, true, cancellationToken));
            if (!await knowledge.ActivateVersionAsync(work, cancellationToken))
            {
                await vectorStore.SetVersionActiveAsync(collection, work.VersionId, false, cancellationToken);
                throw new KnowledgeActivationConflictException();
            }
            await knowledge.EnqueueCleanupAsync(work, cancellationToken);
        }
        catch (EmbeddingDimensionMismatchException exception)
        {
            await knowledge.MarkIndexFailedAsync(jobId, exception.Message, false, CancellationToken.None);
            throw;
        }
        catch (VectorStoreUnavailableException exception)
        {
            await knowledge.MarkIndexFailedAsync(jobId, exception.Message, true, CancellationToken.None);
            throw;
        }
        catch (VectorCollectionConfigurationException exception)
        {
            await knowledge.MarkIndexFailedAsync(jobId, exception.Message, false, CancellationToken.None);
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            await knowledge.MarkIndexFailedAsync(jobId, exception.Message, true, CancellationToken.None);
            throw;
        }
    }

    private void Validate(KnowledgeIndexWork work)
    {
        if (options.Dimension <= 0 || options.BatchSize <= 0 || options.MaximumAttempts <= 0)
            throw new InvalidOperationException("Knowledge index options are invalid.");
        if (work.Dimension != options.Dimension || work.Distance != options.Distance || work.CollectionName != options.CollectionName)
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
