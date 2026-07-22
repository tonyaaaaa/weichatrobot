using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MySql.Data.MySqlClient;
using WechatRobot.Application.Knowledge;
using WechatRobot.Application.Models;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.Infrastructure.Knowledge;

public sealed record LeasedKnowledgeIndexJob(Guid Id, string Operation, string CollectionName, int Dimension, VectorDistance Distance,
    Guid DocumentId, Guid VersionId, string LeaseOwner, int Generation);
public sealed record KnowledgeIndexStatus(Guid DocumentId, Guid? ActiveVersionId, string DocumentStatus, string? CollectionName,
    int ApprovedChunkCount, int? ActivePointCount, string Consistency, IReadOnlyList<string> DriftDetails, IReadOnlyList<KnowledgeIndexJobStatus> Jobs);
public sealed record KnowledgeIndexJobStatus(Guid Id, Guid VersionId, string Operation, string Status, int AttemptCount, string? FailureReason);

public sealed class QdrantKnowledgeService(
    WechatRobotDbContext database,
    ModelConfigurationService modelConfigurations,
    KnowledgeIndexOptions options,
    TimeProvider timeProvider) : IKnowledgeService
{
    public async Task<Guid> QueueIndexAsync(Guid documentId, Guid versionId, IReadOnlyList<Guid> tagIds, bool explicitReindex, CancellationToken token)
    {
        if (tagIds.Count == 0) throw new ArgumentException("At least one knowledge tag is required.", nameof(tagIds));
        var distinctTags = tagIds.Distinct().ToArray();
        if (await database.KnowledgeTags.CountAsync(tag => tag.IsEnabled && distinctTags.Contains(tag.Id), token) != distinctTags.Length)
            throw new ArgumentException("Only enabled knowledge tags may be indexed.", nameof(tagIds));
        var document = await database.KnowledgeDocuments.SingleOrDefaultAsync(item => item.Id == documentId, token) ?? throw new KeyNotFoundException();
        var version = await database.KnowledgeDocumentVersions.SingleOrDefaultAsync(item => item.Id == versionId && item.KnowledgeDocumentId == documentId, token) ?? throw new KeyNotFoundException();
        if (document.IsDeleteRequested || document.Status == "disabled") throw new InvalidOperationException("The document is disabled.");
        if (version.Status is not ("approved" or "indexing" or "active" or "indexed")) throw new InvalidOperationException("Only approved versions can be indexed.");
        var incompatible = document.ActiveVersionId is not null &&
            (document.ActiveEmbeddingDimension != options.Dimension || !string.Equals(document.ActiveDistance, DistanceValue(options.Distance), StringComparison.OrdinalIgnoreCase));
        if (incompatible && !explicitReindex)
            throw new InvalidOperationException("The active embedding dimension or distance differs; use explicit reindex to migrate the active contract.");

        var id = StableJobId(versionId);
        var existing = await database.KnowledgeIndexJobs.SingleOrDefaultAsync(item => item.Id == id, token);
        if (existing is not null && !explicitReindex) return existing.Id;
        if (existing?.Status is "leased" or "activating") throw new InvalidOperationException("An index worker is already processing this version.");
        var generation = (existing?.Generation ?? 0) + 1;
        var stagingCollection = StagingCollection(options.CollectionName, id, generation);
        await using var transaction = await BeginTransactionAsync(token);
        database.KnowledgeChunkTags.RemoveRange(await database.KnowledgeChunkTags
            .Where(binding => database.KnowledgeChunks.Where(chunk => chunk.KnowledgeDocumentVersionId == versionId).Select(chunk => chunk.Id).Contains(binding.KnowledgeChunkId))
            .ToArrayAsync(token));
        var chunks = await database.KnowledgeChunks.Where(chunk => chunk.KnowledgeDocumentVersionId == versionId && chunk.Status == "approved").ToArrayAsync(token);
        if (chunks.Length == 0) throw new InvalidOperationException("The version has no approved chunks.");
        database.KnowledgeChunkTags.AddRange(chunks.SelectMany(chunk => distinctTags.Select(tagId => new KnowledgeChunkTagEntity { KnowledgeChunkId = chunk.Id, KnowledgeTagId = tagId })));
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (existing is null)
        {
            existing = new KnowledgeIndexJobEntity { Id = id, KnowledgeDocumentId = documentId, KnowledgeDocumentVersionId = versionId };
            database.KnowledgeIndexJobs.Add(existing);
        }
        existing.PreviousActiveVersionId = document.ActiveVersionId;
        existing.PreviousActiveCollectionName = document.ActiveCollectionName;
        existing.PreviousActiveEmbeddingDimension = document.ActiveEmbeddingDimension;
        existing.PreviousActiveDistance = document.ActiveDistance;
        existing.Generation = generation;
        existing.Operation = explicitReindex ? "reindex" : "index";
        existing.CollectionName = stagingCollection;
        existing.Dimension = options.Dimension;
        existing.Distance = DistanceValue(options.Distance);
        existing.Status = "pending";
        existing.AttemptCount = 0;
        existing.NextAttemptAtUtc = now;
        existing.LeaseOwner = null;
        existing.LeaseExpiresAtUtc = null;
        existing.FailureReason = null;
        existing.Version++;
        existing.UpdatedAtUtc = now;
        if (version.Status != "active") version.Status = "indexing";
        document.Status = document.ActiveVersionId is null ? "indexing" : "active";
        document.UpdatedAtUtc = version.UpdatedAtUtc = now;
        try
        {
            await database.SaveChangesAsync(token);
            if (transaction is not null) await transaction.CommitAsync(token);
            return id;
        }
        catch (DbUpdateException exception) when (exception.InnerException is MySqlException { Number: 1062 })
        {
            if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None);
            database.ChangeTracker.Clear();
            return id;
        }
    }

    public async Task<LeasedKnowledgeIndexJob?> LeaseNextAsync(string owner, DateTime nowUtc, TimeSpan duration, CancellationToken token)
    {
        var candidate = await database.KnowledgeIndexJobs.AsNoTracking()
            .Where(job => ((job.Status == "pending" || job.Status == "retrying") && job.NextAttemptAtUtc <= nowUtc) ||
                          (job.Status == "leased" && job.LeaseExpiresAtUtc <= nowUtc))
            .OrderBy(job => job.NextAttemptAtUtc).ThenBy(job => job.CreatedAtUtc).Select(job => new { job.Id, job.Version }).FirstOrDefaultAsync(token);
        if (candidate is null) return null;
        var changed = await database.KnowledgeIndexJobs.Where(job => job.Id == candidate.Id && job.Version == candidate.Version &&
                (((job.Status == "pending" || job.Status == "retrying") && job.NextAttemptAtUtc <= nowUtc) || (job.Status == "leased" && job.LeaseExpiresAtUtc <= nowUtc)))
            .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.Status, "leased").SetProperty(job => job.LeaseOwner, owner)
                .SetProperty(job => job.LeaseExpiresAtUtc, nowUtc.Add(duration)).SetProperty(job => job.Version, job => job.Version + 1).SetProperty(job => job.UpdatedAtUtc, nowUtc), token);
        if (changed != 1) return null;
        var job = await database.KnowledgeIndexJobs.AsNoTracking().SingleAsync(item => item.Id == candidate.Id, token);
        return new LeasedKnowledgeIndexJob(job.Id, job.Operation, job.CollectionName, job.Dimension, ParseDistance(job.Distance), job.KnowledgeDocumentId,
            job.KnowledgeDocumentVersionId, owner, job.Generation);
    }

    public async Task<bool> RenewLeaseAsync(Guid jobId, string owner, DateTime nowUtc, TimeSpan duration, CancellationToken token)
    {
        var changed = await database.KnowledgeIndexJobs.Where(job => job.Id == jobId && (job.Status == "leased" || job.Status == "activating") && job.LeaseOwner == owner)
            .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.LeaseExpiresAtUtc, nowUtc.Add(duration))
                .SetProperty(job => job.Version, job => job.Version + 1).SetProperty(job => job.UpdatedAtUtc, nowUtc), token);
        return changed == 1;
    }

    public async Task<KnowledgeIndexWork> LoadIndexWorkAsync(Guid jobId, CancellationToken token)
    {
        var job = await database.KnowledgeIndexJobs.AsNoTracking().SingleOrDefaultAsync(item => item.Id == jobId, token) ?? throw new KeyNotFoundException();
        if (job.Operation == "cleanup" || job.Status != "leased" || job.LeaseOwner is null) throw new InvalidOperationException("The index job is not owned by an active worker.");
        var chunks = await database.KnowledgeChunks.AsNoTracking().Where(chunk => chunk.KnowledgeDocumentVersionId == job.KnowledgeDocumentVersionId && chunk.Status == "approved")
            .OrderBy(chunk => chunk.Sequence).Select(chunk => new { chunk.Id, chunk.Text }).ToArrayAsync(token);
        var chunkIds = chunks.Select(chunk => chunk.Id).ToArray();
        var tagRows = await (from binding in database.KnowledgeChunkTags.AsNoTracking()
                             join tag in database.KnowledgeTags.AsNoTracking() on binding.KnowledgeTagId equals tag.Id
                             where chunkIds.Contains(binding.KnowledgeChunkId) && tag.IsEnabled
                             select new { binding.KnowledgeChunkId, binding.KnowledgeTagId }).ToArrayAsync(token);
        var tags = tagRows.ToLookup(item => item.KnowledgeChunkId, item => item.KnowledgeTagId);
        return new KnowledgeIndexWork(job.Id, job.KnowledgeDocumentId, job.KnowledgeDocumentVersionId, job.PreviousActiveVersionId,
            job.CollectionName, job.Dimension, ParseDistance(job.Distance), chunks.Select(chunk => new KnowledgeIndexChunk(chunk.Id, job.KnowledgeDocumentId,
                job.KnowledgeDocumentVersionId, chunk.Text, tags[chunk.Id].ToArray())).ToArray(), job.LeaseOwner, job.Generation,
            job.PreviousActiveCollectionName, job.PreviousActiveEmbeddingDimension,
            job.PreviousActiveDistance is null ? null : ParseDistance(job.PreviousActiveDistance));
    }

    public async Task<ModelProviderConfiguration> LoadEmbeddingConfigurationAsync(CancellationToken token)
    {
        var config = await database.ModelConfigs.AsNoTracking().Where(item => item.ConfigurationType == "embedding" && item.IsEnabled)
            .OrderByDescending(item => item.IsDefault).ThenBy(item => item.CreatedAtUtc).FirstOrDefaultAsync(token)
            ?? throw new InvalidOperationException("No enabled embedding model configuration exists.");
        return modelConfigurations.ToProviderConfiguration(new ModelConfigurationRecord(config.Id, config.Name, config.Provider, config.BaseUrl,
            config.Model, config.EncryptedApiKey, config.TimeoutSeconds, config.MaxRetries, config.IsEnabled, config.IsDefault));
    }

    public async Task<bool> ActivateVersionAsync(KnowledgeIndexWork work, CancellationToken token)
    {
        if (work.LeaseOwner is null) return false;
        await using var transaction = await BeginTransactionAsync(token);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var ownerChanged = await database.KnowledgeIndexJobs.Where(job => job.Id == work.JobId && job.Status == "leased" && job.LeaseOwner == work.LeaseOwner)
            .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.Status, "activating").SetProperty(job => job.Version, job => job.Version + 1)
                .SetProperty(job => job.UpdatedAtUtc, now), token);
        if (ownerChanged != 1) { if (transaction is not null) await transaction.RollbackAsync(token); return false; }
        var documentChanged = await database.KnowledgeDocuments.Where(document => document.Id == work.DocumentId && !document.IsDeleteRequested && document.Status != "disabled" &&
                ((work.PreviousActiveVersionId == null && document.ActiveVersionId == null) ||
                 (document.ActiveVersionId == work.PreviousActiveVersionId && document.ActiveCollectionName == work.PreviousActiveCollectionName)))
            .ExecuteUpdateAsync(setters => setters.SetProperty(document => document.ActiveVersionId, work.VersionId)
                .SetProperty(document => document.ActiveCollectionName, work.CollectionName).SetProperty(document => document.ActiveEmbeddingDimension, work.Dimension)
                .SetProperty(document => document.ActiveDistance, DistanceValue(work.Distance)).SetProperty(document => document.ActiveIndexGeneration, work.Generation)
                .SetProperty(document => document.Status, "active").SetProperty(document => document.UpdatedAtUtc, now), token);
        if (documentChanged != 1) { if (transaction is not null) await transaction.RollbackAsync(token); return false; }
        var versionChanged = await database.KnowledgeDocumentVersions.Where(version => version.Id == work.VersionId && version.KnowledgeDocumentId == work.DocumentId && version.Status != "disabled")
            .ExecuteUpdateAsync(setters => setters.SetProperty(version => version.Status, "active").SetProperty(version => version.IsPublished, true)
                .SetProperty(version => version.IndexCollectionName, work.CollectionName).SetProperty(version => version.EmbeddingDimension, work.Dimension)
                .SetProperty(version => version.VectorDistance, DistanceValue(work.Distance)).SetProperty(version => version.IndexGeneration, work.Generation)
                .SetProperty(version => version.UpdatedAtUtc, now), token);
        if (versionChanged != 1) { if (transaction is not null) await transaction.RollbackAsync(token); return false; }
        if (work.PreviousActiveVersionId is { } oldVersion && work.PreviousActiveCollectionName is { } oldCollection && oldCollection != work.CollectionName)
        {
            if (oldVersion != work.VersionId)
                await database.KnowledgeDocumentVersions.Where(version => version.Id == oldVersion).ExecuteUpdateAsync(setters => setters
                    .SetProperty(version => version.Status, "indexed").SetProperty(version => version.IsPublished, false).SetProperty(version => version.UpdatedAtUtc, now), token);
            await AddCleanupJobAsync(work.DocumentId, oldVersion, oldCollection, work.PreviousActiveEmbeddingDimension ?? work.Dimension,
                work.PreviousActiveDistance ?? work.Distance, 0, now, token);
        }
        await database.KnowledgeIndexJobs.Where(job => job.Id == work.JobId && job.Status == "activating" && job.LeaseOwner == work.LeaseOwner)
            .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.Status, "completed").SetProperty(job => job.LeaseOwner, (string?)null)
                .SetProperty(job => job.LeaseExpiresAtUtc, (DateTime?)null).SetProperty(job => job.FailureReason, (string?)null)
                .SetProperty(job => job.Version, job => job.Version + 1).SetProperty(job => job.UpdatedAtUtc, now), token);
        await database.SaveChangesAsync(token);
        if (transaction is not null) await transaction.CommitAsync(token);
        return true;
    }

    public async Task EnqueueCleanupAsync(KnowledgeIndexWork work, CancellationToken token)
    {
        if (work.PreviousActiveVersionId is not { } version || work.PreviousActiveCollectionName is not { } collection || collection == work.CollectionName) return;
        await AddCleanupJobAsync(work.DocumentId, version, collection, work.PreviousActiveEmbeddingDimension ?? work.Dimension,
            work.PreviousActiveDistance ?? work.Distance, 0, timeProvider.GetUtcNow().UtcDateTime, token);
        try { await database.SaveChangesAsync(token); }
        catch (DbUpdateException exception) when (exception.InnerException is MySqlException { Number: 1062 }) { database.ChangeTracker.Clear(); }
    }

    public async Task MarkIndexFailedAsync(Guid jobId, string? leaseOwner, string reason, bool retryable, CancellationToken token)
    {
        var job = await database.KnowledgeIndexJobs.SingleOrDefaultAsync(item => item.Id == jobId && item.LeaseOwner == leaseOwner &&
            (item.Status == "leased" || item.Status == "activating"), token);
        if (job is null) return;
        job.AttemptCount++;
        job.FailureReason = reason.Length <= 1024 ? reason : reason[..1024];
        job.Status = retryable && job.AttemptCount < 4 ? "retrying" : "failed";
        job.NextAttemptAtUtc = timeProvider.GetUtcNow().UtcDateTime.AddSeconds(job.AttemptCount switch { 1 => 5, 2 => 15, _ => 45 });
        job.LeaseOwner = null; job.LeaseExpiresAtUtc = null; job.Version++; job.UpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        await database.SaveChangesAsync(token);
    }

    public async Task RetryAsync(Guid jobId, CancellationToken token)
    {
        var job = await database.KnowledgeIndexJobs.SingleOrDefaultAsync(item => item.Id == jobId, token) ?? throw new KeyNotFoundException();
        if (job.Status is not ("failed" or "retrying")) throw new InvalidOperationException("Only failed or retrying index jobs can be retried.");
        job.Status = "pending"; job.NextAttemptAtUtc = timeProvider.GetUtcNow().UtcDateTime; job.LeaseOwner = null; job.LeaseExpiresAtUtc = null;
        job.FailureReason = null; job.Version++; job.UpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        await database.SaveChangesAsync(token);
    }

    public async Task DisableAsync(Guid documentId, CancellationToken token)
    {
        var document = await database.KnowledgeDocuments.SingleOrDefaultAsync(item => item.Id == documentId, token) ?? throw new KeyNotFoundException();
        if (document.ActiveVersionId is { } versionId && document.ActiveCollectionName is { } collection)
            await AddCleanupJobAsync(documentId, versionId, collection, document.ActiveEmbeddingDimension ?? options.Dimension,
                document.ActiveDistance is null ? options.Distance : ParseDistance(document.ActiveDistance), document.ActiveIndexGeneration ?? 0,
                timeProvider.GetUtcNow().UtcDateTime, token);
        document.Status = "disabled"; document.IsDeleteRequested = true; document.ActiveVersionId = null; document.UpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        await database.KnowledgeDocumentVersions.Where(version => version.KnowledgeDocumentId == documentId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(version => version.Status, "disabled").SetProperty(version => version.IsPublished, false)
                .SetProperty(version => version.UpdatedAtUtc, document.UpdatedAtUtc), token);
        await database.SaveChangesAsync(token);
    }

    public async Task CompleteCleanupAsync(Guid jobId, string owner, CancellationToken token) => await database.KnowledgeIndexJobs
        .Where(job => job.Id == jobId && job.Status == "leased" && job.LeaseOwner == owner)
        .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.Status, "completed").SetProperty(job => job.LeaseOwner, (string?)null)
            .SetProperty(job => job.LeaseExpiresAtUtc, (DateTime?)null).SetProperty(job => job.Version, job => job.Version + 1)
            .SetProperty(job => job.UpdatedAtUtc, timeProvider.GetUtcNow().UtcDateTime), token);

    public async Task<KnowledgeIndexStatus> GetStatusAsync(Guid documentId, IVectorStore vectors, bool checkConsistency, CancellationToken token)
    {
        var document = await database.KnowledgeDocuments.AsNoTracking().SingleOrDefaultAsync(item => item.Id == documentId, token) ?? throw new KeyNotFoundException();
        var jobs = await database.KnowledgeIndexJobs.AsNoTracking().Where(item => item.KnowledgeDocumentId == documentId).OrderByDescending(item => item.CreatedAtUtc)
            .Select(item => new KnowledgeIndexJobStatus(item.Id, item.KnowledgeDocumentVersionId, item.Operation, item.Status, item.AttemptCount, item.FailureReason)).ToArrayAsync(token);
        if (!checkConsistency)
            return new KnowledgeIndexStatus(document.Id, document.ActiveVersionId, document.Status, document.ActiveCollectionName, 0, null, "not-checked", [], jobs);
        if (document.ActiveVersionId is not { } active || document.ActiveCollectionName is not { } name || document.ActiveEmbeddingDimension is not { } dimension || document.ActiveDistance is not { } distance)
            return new KnowledgeIndexStatus(document.Id, document.ActiveVersionId, document.Status, document.ActiveCollectionName, 0, 0, "inactive", [], jobs);
        var expected = await database.KnowledgeChunks.AsNoTracking().Where(chunk => chunk.KnowledgeDocumentVersionId == active && chunk.Status == "approved")
            .Select(chunk => new { chunk.Id, chunk.KnowledgeDocumentVersionId }).ToArrayAsync(token);
        var ids = expected.Select(chunk => chunk.Id).ToArray();
        var tags = (await (from binding in database.KnowledgeChunkTags.AsNoTracking()
                           join tag in database.KnowledgeTags.AsNoTracking() on binding.KnowledgeTagId equals tag.Id
                           where ids.Contains(binding.KnowledgeChunkId) && tag.IsEnabled
                           select new { binding.KnowledgeChunkId, binding.KnowledgeTagId }).ToArrayAsync(token)).ToLookup(row => row.KnowledgeChunkId, row => row.KnowledgeTagId);
        var actual = await vectors.InspectVersionAsync(new VectorCollection(name, dimension, ParseDistance(distance)), active, token);
        var actualById = actual.ToDictionary(point => point.ChunkId);
        var drift = new List<string>();
        foreach (var chunk in expected)
        {
            if (!actualById.TryGetValue(chunk.Id, out var point)) { drift.Add($"missing:{chunk.Id:D}"); continue; }
            if (point.DocumentId != documentId || point.VersionId != active || !point.Active || point.Generation != (document.ActiveIndexGeneration ?? point.Generation) ||
                !point.TagIds.ToHashSet().SetEquals(tags[chunk.Id])) drift.Add($"payload:{chunk.Id:D}");
        }
        foreach (var unexpected in actual.Where(point => !ids.Contains(point.ChunkId))) drift.Add($"unexpected:{unexpected.ChunkId:D}");
        return new KnowledgeIndexStatus(document.Id, active, document.Status, name, expected.Length, actual.Count,
            drift.Count == 0 ? "consistent" : "drift", drift, jobs);
    }

    public async Task<IReadOnlyList<VectorSearchHit>> SearchVisibleAsync(IReadOnlyList<float> vector, IReadOnlyList<Guid> allowedTagIds,
        IVectorStore vectors, int limit, CancellationToken token)
    {
        var enabledTags = await database.KnowledgeTags.AsNoTracking().Where(tag => tag.IsEnabled).ToArrayAsync(token);
        var global = enabledTags.SingleOrDefault(tag => tag.IsGlobalPublic)?.Id;
        var enabledAllowed = enabledTags.Where(tag => !tag.IsGlobalPublic && allowedTagIds.Contains(tag.Id)).Select(tag => tag.Id).ToArray();
        if (enabledAllowed.Length == 0 && global is null) return [];
        var activeDocuments = await database.KnowledgeDocuments.AsNoTracking().Where(document => !document.IsDeleteRequested && document.Status == "active" &&
            document.ActiveVersionId != null && document.ActiveCollectionName != null && document.ActiveEmbeddingDimension == vector.Count && document.ActiveDistance != null)
            .Select(document => new { document.ActiveVersionId, document.ActiveCollectionName, document.ActiveEmbeddingDimension, document.ActiveDistance }).ToArrayAsync(token);
        var candidates = new List<VectorSearchHit>();
        foreach (var contract in activeDocuments.GroupBy(document => new { document.ActiveCollectionName, document.ActiveEmbeddingDimension, document.ActiveDistance }))
        {
            var request = new VectorSearchRequest(new VectorCollection(contract.Key.ActiveCollectionName!, contract.Key.ActiveEmbeddingDimension!.Value,
                ParseDistance(contract.Key.ActiveDistance!)), vector, enabledAllowed, contract.Select(item => item.ActiveVersionId!.Value).Distinct().ToArray(), global, limit);
            candidates.AddRange(await vectors.SearchAsync(request, token));
        }
        if (candidates.Count == 0) return [];
        var candidateIds = candidates.Select(hit => hit.ChunkId).Distinct().ToArray();
        var visibleIds = await (from chunk in database.KnowledgeChunks.AsNoTracking()
                                join version in database.KnowledgeDocumentVersions.AsNoTracking() on chunk.KnowledgeDocumentVersionId equals version.Id
                                join document in database.KnowledgeDocuments.AsNoTracking() on version.KnowledgeDocumentId equals document.Id
                                where candidateIds.Contains(chunk.Id) && chunk.Status == "approved" && version.Status == "active" && version.IsPublished &&
                                      document.Status == "active" && !document.IsDeleteRequested && document.ActiveVersionId == version.Id &&
                                      database.KnowledgeChunkTags.Any(binding => binding.KnowledgeChunkId == chunk.Id &&
                                          ((global != null && binding.KnowledgeTagId == global) || enabledAllowed.Contains(binding.KnowledgeTagId)))
                                select chunk.Id).ToArrayAsync(token);
        var allowed = visibleIds.ToHashSet();
        return candidates.Where(hit => allowed.Contains(hit.ChunkId)).OrderByDescending(hit => hit.Score).DistinctBy(hit => hit.ChunkId).Take(limit).ToArray();
    }

    public async Task<IReadOnlyList<(VectorCollection Collection, Guid VersionId)>> GetDocumentVectorContractsAsync(Guid documentId, CancellationToken token)
    {
        var versions = await database.KnowledgeDocumentVersions.AsNoTracking().Where(version => version.KnowledgeDocumentId == documentId &&
            version.IndexCollectionName != null && version.EmbeddingDimension != null && version.VectorDistance != null)
            .Select(version => new VectorContractRow(version.Id, version.IndexCollectionName!, version.EmbeddingDimension!.Value, version.VectorDistance!)).ToArrayAsync(token);
        var pending = await database.KnowledgeIndexJobs.AsNoTracking().Where(job => job.KnowledgeDocumentId == documentId && job.CollectionName != "")
            .Select(job => new VectorContractRow(job.KnowledgeDocumentVersionId, job.CollectionName, job.Dimension, job.Distance)).ToArrayAsync(token);
        return versions.Concat(pending).Select(item => (new VectorCollection(item.CollectionName, item.Dimension, ParseDistance(item.Distance)), item.VersionId)).Distinct().ToArray();
    }

    private async Task AddCleanupJobAsync(Guid documentId, Guid versionId, string collection, int dimension, VectorDistance distance,
        int generation, DateTime now, CancellationToken token)
    {
        var id = CleanupJobId(versionId, collection);
        if (await database.KnowledgeIndexJobs.AnyAsync(job => job.Id == id, token)) return;
        database.KnowledgeIndexJobs.Add(new KnowledgeIndexJobEntity
        {
            Id = id, KnowledgeDocumentId = documentId, KnowledgeDocumentVersionId = versionId, Operation = "cleanup", CollectionName = collection,
            Dimension = dimension, Distance = DistanceValue(distance), Generation = generation, NextAttemptAtUtc = now
        });
    }

    private async Task<IDbContextTransaction?> BeginTransactionAsync(CancellationToken token) =>
        database.Database.IsRelational() ? await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token) : null;
    private static Guid StableJobId(Guid versionId) => HashGuid($"index:{versionId:N}");
    private static Guid CleanupJobId(Guid versionId, string collection) => HashGuid($"cleanup-index:{versionId:N}:{collection}");
    private static Guid HashGuid(string input) => new(SHA256.HashData(Encoding.UTF8.GetBytes(input)).AsSpan(0, 16));
    private static string StagingCollection(string baseName, Guid jobId, int generation) => $"{baseName}_g{generation}_{jobId:N}";
    private static string DistanceValue(VectorDistance distance) => distance.ToString().ToLowerInvariant();
    private static VectorDistance ParseDistance(string value) => Enum.Parse<VectorDistance>(value, true);
    private sealed record VectorContractRow(Guid VersionId, string CollectionName, int Dimension, string Distance);
}
