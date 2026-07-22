using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Conversations;
using WechatRobot.Application.Knowledge;
using WechatRobot.Application.Models;
using WechatRobot.Infrastructure.Knowledge;
using WechatRobot.Infrastructure.Persistence;

namespace WechatRobot.Infrastructure.Conversations;

public sealed class KnowledgeRetrievalEvidenceProvider(
    WechatRobotDbContext database,
    QdrantKnowledgeService knowledge,
    IEmbeddingClient embeddings,
    IVectorStore vectors) : IRetrievalEvidenceProvider
{
    public async Task<IReadOnlyList<RetrievalEvidence>> RetrieveAsync(string question, IReadOnlyList<Guid> allowedTagIds, int limit, CancellationToken token)
    {
        try
        {
            var configuration = await knowledge.LoadEmbeddingConfigurationAsync(token);
            var embedding = await embeddings.CreateEmbeddingsAsync(configuration, new([question]), token);
            var vector = embedding.Vectors.SingleOrDefault() ?? throw new RetrievalUnavailableException("Embedding provider returned no vector.");
            var hits = await knowledge.SearchVisibleAsync(vector, allowedTagIds, vectors, limit, token);
            if (hits.Count == 0) return [];
            var ids = hits.Select(hit => hit.ChunkId).Distinct().ToArray();
            var enabledTags = await database.KnowledgeTags.AsNoTracking().Where(tag => tag.IsEnabled).ToArrayAsync(token);
            var globalId = enabledTags.SingleOrDefault(tag => tag.IsGlobalPublic)?.Id;
            var visibleTags = allowedTagIds.Intersect(enabledTags.Where(tag => tag.IsEnabled).Select(tag => tag.Id)).ToHashSet();
            if (globalId is { } global) visibleTags.Add(global);
            var rows = await (from chunk in database.KnowledgeChunks.AsNoTracking().Where(chunk => ids.Contains(chunk.Id))
                              join version in database.KnowledgeDocumentVersions.AsNoTracking() on chunk.KnowledgeDocumentVersionId equals version.Id
                              join document in database.KnowledgeDocuments.AsNoTracking() on version.KnowledgeDocumentId equals document.Id
                              where chunk.Status == "approved" && version.Status == "active" && version.IsPublished &&
                                    document.Status == "active" && !document.IsDeleteRequested && document.ActiveVersionId == version.Id
                              select new { Chunk = chunk, Version = version, Document = document }).ToArrayAsync(token);
            var tags = await database.KnowledgeChunkTags.AsNoTracking().Where(item => ids.Contains(item.KnowledgeChunkId))
                .Select(item => new { item.KnowledgeChunkId, item.KnowledgeTagId }).ToArrayAsync(token);
            var tagsByChunk = tags.GroupBy(item => item.KnowledgeChunkId).ToDictionary(group => group.Key, group => group.Select(item => item.KnowledgeTagId).ToArray());
            var hitByChunk = hits.ToDictionary(hit => hit.ChunkId);
            return rows.Where(row => hitByChunk.TryGetValue(row.Chunk.Id, out var hit) && hit.DocumentId == row.Document.Id && hit.VersionId == row.Version.Id &&
                    tagsByChunk.TryGetValue(row.Chunk.Id, out var chunkTags) && chunkTags.Any(visibleTags.Contains))
                .Select(row => new RetrievalEvidence(row.Document.Id, row.Version.Id, row.Chunk.Id, row.Chunk.PageNumber,
                    hitByChunk[row.Chunk.Id].Score, tagsByChunk[row.Chunk.Id].Where(visibleTags.Contains).ToArray(), row.Document.Title, row.Chunk.Text))
                .OrderByDescending(item => item.Similarity).Take(limit).ToArray();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (RetrievalUnavailableException) { throw; }
        catch (Exception exception) when (exception is VectorStoreUnavailableException or HttpRequestException or TimeoutException or OperationCanceledException)
        {
            throw new RetrievalUnavailableException("Knowledge retrieval is unavailable.", exception);
        }
    }
}
