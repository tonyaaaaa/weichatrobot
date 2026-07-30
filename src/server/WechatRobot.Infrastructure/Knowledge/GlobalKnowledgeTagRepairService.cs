using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.Infrastructure.Knowledge;

public sealed record GlobalKnowledgeTagRepairResult(
    bool Changed,
    int RemovedDuplicateCount,
    Guid CanonicalTagId);

public sealed class GlobalKnowledgeTagRepairService(
    WechatRobotDbContext database,
    TimeProvider timeProvider)
{
    private const string InMemoryProviderName =
        "Microsoft.EntityFrameworkCore.InMemory";

    public async Task<GlobalKnowledgeTagRepairResult> RepairAsync(
        CancellationToken cancellationToken)
    {
        IDbContextTransaction? transaction = null;
        if (!UsesInMemoryProvider(database))
        {
            transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        }

        try
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var tags = await database.KnowledgeTags
                .ToArrayAsync(cancellationToken);
            var canonical = tags.SingleOrDefault(
                tag => tag.SystemKind == GlobalKnowledgeTag.SystemKind)
                ?? tags.FirstOrDefault(tag => tag.Id == GlobalKnowledgeTag.DefaultId)
                ?? tags.FirstOrDefault(
                    tag => tag.NormalizedName == GlobalKnowledgeTag.NormalizedName);
            var changed = false;
            if (canonical is null)
            {
                canonical = GlobalKnowledgeTag.Create(now);
                database.KnowledgeTags.Add(canonical);
                tags = [.. tags, canonical];
                changed = true;
            }

            var duplicates = tags
                .Where(tag => tag.Id != canonical.Id)
                .Where(tag =>
                    GlobalKnowledgeTag.IsReservedDisplayName(tag.Name) ||
                    tag.NormalizedName == GlobalKnowledgeTag.NormalizedName)
                .ToArray();
            var duplicateIds = duplicates.Select(tag => tag.Id).ToHashSet();

            if (canonical.Name != GlobalKnowledgeTag.DisplayName)
            {
                canonical.Name = GlobalKnowledgeTag.DisplayName;
                changed = true;
            }
            if (canonical.NormalizedName != GlobalKnowledgeTag.NormalizedName)
            {
                canonical.NormalizedName = GlobalKnowledgeTag.NormalizedName;
                changed = true;
            }
            if (canonical.SystemKind != GlobalKnowledgeTag.SystemKind)
            {
                canonical.SystemKind = GlobalKnowledgeTag.SystemKind;
                changed = true;
            }
            if (!canonical.IsEnabled)
            {
                canonical.IsEnabled = true;
                changed = true;
            }
            if (!canonical.IsGlobalPublic)
            {
                canonical.IsGlobalPublic = true;
                changed = true;
            }

            if (duplicateIds.Count > 0)
            {
                await MergeRelationalReferencesAsync(
                    canonical.Id,
                    duplicateIds,
                    now,
                    cancellationToken);
                await MergeJsonReferencesAsync(
                    canonical.Id,
                    duplicateIds,
                    cancellationToken);
                database.KnowledgeTags.RemoveRange(duplicates);
                changed = true;
            }

            if (changed)
            {
                canonical.Version++;
                database.AdministrationAudits.Add(new AdministrationAuditEntity
                {
                    Actor = "system",
                    Action = "knowledge-tag.system-global.repair",
                    TargetType = "knowledge-tag",
                    TargetId = canonical.Id.ToString("D"),
                    SanitizedDetailJson = JsonSerializer.Serialize(new
                    {
                        removedDuplicateCount = duplicateIds.Count
                    }),
                    CreatedAtUtc = now
                });
                await database.SaveChangesAsync(cancellationToken);
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
            return new(changed, duplicateIds.Count, canonical.Id);
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    public static bool UsesInMemoryProvider(WechatRobotDbContext context) =>
        string.Equals(
            context.Database.ProviderName,
            InMemoryProviderName,
            StringComparison.Ordinal);

    private async Task MergeRelationalReferencesAsync(
        Guid canonicalId,
        IReadOnlySet<Guid> duplicateIds,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var tagIds = duplicateIds.Append(canonicalId).ToArray();
        var groupBindings = new List<GroupProfileTagEntity>();
        foreach (var batch in GuidBatchQuery.CreateBatches(tagIds))
        {
            var predicate = GuidBatchQuery.BuildPredicate<GroupProfileTagEntity>(
                batch,
                binding => binding.KnowledgeTagId);
            groupBindings.AddRange(await database.GroupProfileTags
                .Where(predicate)
                .ToArrayAsync(cancellationToken));
        }
        var canonicalGroups = groupBindings
            .Where(binding => binding.KnowledgeTagId == canonicalId)
            .Select(binding => binding.GroupProfileId)
            .ToHashSet();
        var groupsToAdd = groupBindings
            .Where(binding => duplicateIds.Contains(binding.KnowledgeTagId))
            .Select(binding => binding.GroupProfileId)
            .Where(groupId => !canonicalGroups.Contains(groupId))
            .Distinct()
            .ToArray();
        database.GroupProfileTags.RemoveRange(
            groupBindings.Where(binding => duplicateIds.Contains(binding.KnowledgeTagId)));
        database.GroupProfileTags.AddRange(groupsToAdd.Select(groupId =>
            new GroupProfileTagEntity
            {
                GroupProfileId = groupId,
                KnowledgeTagId = canonicalId,
                CreatedAtUtc = now
            }));

        var chunkBindings = new List<KnowledgeChunkTagEntity>();
        foreach (var batch in GuidBatchQuery.CreateBatches(tagIds))
        {
            var predicate = GuidBatchQuery.BuildPredicate<KnowledgeChunkTagEntity>(
                batch,
                binding => binding.KnowledgeTagId);
            chunkBindings.AddRange(await database.KnowledgeChunkTags
                .Where(predicate)
                .ToArrayAsync(cancellationToken));
        }
        var canonicalChunks = chunkBindings
            .Where(binding => binding.KnowledgeTagId == canonicalId)
            .Select(binding => binding.KnowledgeChunkId)
            .ToHashSet();
        var chunksToAdd = chunkBindings
            .Where(binding => duplicateIds.Contains(binding.KnowledgeTagId))
            .Select(binding => binding.KnowledgeChunkId)
            .Where(chunkId => !canonicalChunks.Contains(chunkId))
            .Distinct()
            .ToArray();
        database.KnowledgeChunkTags.RemoveRange(
            chunkBindings.Where(binding => duplicateIds.Contains(binding.KnowledgeTagId)));
        database.KnowledgeChunkTags.AddRange(chunksToAdd.Select(chunkId =>
            new KnowledgeChunkTagEntity
            {
                KnowledgeChunkId = chunkId,
                KnowledgeTagId = canonicalId,
                CreatedAtUtc = now
            }));
    }

    private async Task MergeJsonReferencesAsync(
        Guid canonicalId,
        IReadOnlySet<Guid> duplicateIds,
        CancellationToken cancellationToken)
    {
        var reviews = new Dictionary<Guid, KnowledgeReviewEntity>();
        var jobs = new Dictionary<Guid, KnowledgeIndexJobEntity>();
        var ingestItems = new Dictionary<Guid, PrivateKnowledgeIngestItemEntity>();
        foreach (var duplicateId in duplicateIds)
        {
            var value = duplicateId.ToString("D");
            foreach (var entity in await database.KnowledgeReviews
                         .Where(item => item.TagIdsJson.Contains(value))
                         .ToArrayAsync(cancellationToken))
            {
                reviews[entity.Id] = entity;
            }
            foreach (var entity in await database.KnowledgeIndexJobs
                         .Where(item => item.PendingTagIdsJson.Contains(value))
                         .ToArrayAsync(cancellationToken))
            {
                jobs[entity.Id] = entity;
            }
            foreach (var entity in await database.PrivateKnowledgeIngestItems
                         .Where(item => item.ResolvedTagIdsJson.Contains(value))
                         .ToArrayAsync(cancellationToken))
            {
                ingestItems[entity.Id] = entity;
            }
        }

        foreach (var review in reviews.Values)
        {
            review.TagIdsJson = ReplaceTagIds(
                review.TagIdsJson,
                canonicalId,
                duplicateIds);
        }
        foreach (var job in jobs.Values)
        {
            job.PendingTagIdsJson = ReplaceTagIds(
                job.PendingTagIdsJson,
                canonicalId,
                duplicateIds);
        }
        foreach (var item in ingestItems.Values)
        {
            item.ResolvedTagIdsJson = ReplaceTagIds(
                item.ResolvedTagIdsJson,
                canonicalId,
                duplicateIds);
        }
    }

    private static string ReplaceTagIds(
        string json,
        Guid canonicalId,
        IReadOnlySet<Guid> duplicateIds)
    {
        Guid[] values;
        try
        {
            values = JsonSerializer.Deserialize<Guid[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return json;
        }

        var replaced = values
            .Select(value => duplicateIds.Contains(value) ? canonicalId : value)
            .Distinct()
            .ToArray();
        return JsonSerializer.Serialize(replaced);
    }
}
