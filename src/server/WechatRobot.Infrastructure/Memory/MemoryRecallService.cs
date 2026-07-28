using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Knowledge;
using WechatRobot.Application.Memory;
using WechatRobot.Application.Models;
using WechatRobot.Domain.Memory;
using WechatRobot.Infrastructure.Persistence;

namespace WechatRobot.Infrastructure.Memory;

public sealed class MemoryRecallService(
    WechatRobotDbContext database,
    IMemoryVectorIndex vectorIndex,
    IEmbeddingClient embeddingClient,
    ModelConfigurationService modelConfigurationService,
    TimeProvider timeProvider) : IMemoryRecallService
{
    private const int MaximumMemories = 5;
    private const int MaximumCharacters = 2000;

    public async Task<MemoryRecallResult> RecallAsync(
        string question,
        Guid robotConfigId,
        Guid groupProfileId,
        string? subjectKey,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var model = await database.ModelConfigs.AsNoTracking()
                .SingleOrDefaultAsync(
                    x => x.ConfigurationType == "embedding" && x.IsEnabled && x.IsDefault,
                    cancellationToken);
            if (model?.EmbeddingDimension is not > 0)
            {
                return new([], "memory_embedding_unavailable");
            }

            var configuration = modelConfigurationService.ToProviderConfiguration(
                new ModelConfigurationRecord(
                    model.Id,
                    model.Name,
                    model.Provider,
                    model.BaseUrl,
                    model.Model,
                    model.EncryptedApiKey,
                    model.TimeoutSeconds,
                    model.MaxRetries,
                    model.IsEnabled,
                    model.IsDefault,
                    model.EmbeddingDimension,
                    model.WebSearchMode));
            var embedding = await embeddingClient.CreateEmbeddingsAsync(
                configuration,
                new EmbeddingBatchRequest([question]),
                cancellationToken);
            var vector = embedding.Vectors.Single();
            if (vector.Count != model.EmbeddingDimension)
            {
                return new([], "memory_embedding_dimension_mismatch");
            }

            var hits = await vectorIndex.SearchAsync(
                vector,
                model.EmbeddingDimension.Value,
                VectorDistance.Cosine,
                1,
                20,
                cancellationToken);
            if (hits.Count == 0)
            {
                return new([]);
            }

            var scores = hits.ToDictionary(x => x.MemoryEntryId, x => x.Score);
            var ids = scores.Keys.ToArray();
            var normalizedSubject = MemoryScope.NormalizeSubject(subjectKey);
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var entries = await database.MemoryEntries.AsNoTracking()
                .Where(x => ids.Contains(x.Id) &&
                            x.Status == "active" &&
                            (x.ExpiresAtUtc == null || x.ExpiresAtUtc > now) &&
                            (
                                x.ScopeType == "Global" ||
                                x.ScopeType == "Robot" && x.RobotConfigId == robotConfigId ||
                                x.ScopeType == "Group" && x.RobotConfigId == robotConfigId && x.GroupProfileId == groupProfileId ||
                                x.ScopeType == "User" && x.RobotConfigId == robotConfigId && x.GroupProfileId == groupProfileId && x.SubjectKey == normalizedSubject
                            ))
                .ToArrayAsync(cancellationToken);

            var characters = 0;
            var recalled = new List<RecalledMemory>();
            foreach (var entry in entries
                         .OrderBy(x => ScopePriority(x.ScopeType))
                         .ThenByDescending(x => scores[x.Id]))
            {
                if (recalled.Count >= MaximumMemories ||
                    characters + entry.Content.Length > MaximumCharacters)
                {
                    continue;
                }
                characters += entry.Content.Length;
                recalled.Add(new RecalledMemory(
                    entry.Id,
                    entry.ScopeType,
                    entry.MemoryType,
                    entry.Content,
                    entry.Version,
                    scores[entry.Id]));
            }

            return new(recalled);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new([], "memory_recall_unavailable");
        }
    }

    private static int ScopePriority(string scopeType) => scopeType switch
    {
        "User" => 0,
        "Group" => 1,
        "Robot" => 2,
        "Global" => 3,
        _ => 4
    };
}
