using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using MySql.Data.MySqlClient;
using WechatRobot.Application.Knowledge;
using WechatRobot.Application.Models;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.Infrastructure.Knowledge;

public sealed record LeasedKnowledgeIndexJob(Guid Id, string Operation, string CollectionName, int Dimension, VectorDistance Distance,
    Guid DocumentId, Guid VersionId, string LeaseOwner, int Generation, bool IsCollectionExclusive);
public sealed record KnowledgeVectorContract(VectorCollection Collection, Guid VersionId, bool IsCollectionExclusive);
public sealed record KnowledgeIndexStatus(Guid DocumentId, Guid? ActiveVersionId, string DocumentStatus, string? CollectionName,
    int ApprovedChunkCount, int? ActivePointCount, string Consistency, IReadOnlyList<string> DriftDetails, IReadOnlyList<KnowledgeIndexJobStatus> Jobs);
public sealed record KnowledgeIndexJobStatus(Guid Id, Guid VersionId, string Operation, string Status, int AttemptCount, string? FailureReason);

public sealed class QdrantKnowledgeService(
    WechatRobotDbContext database,
    ModelConfigurationService modelConfigurations,
    KnowledgeIndexOptions options,
    TimeProvider timeProvider,
    ILogger<QdrantKnowledgeService>? logger = null) : IKnowledgeService
{
    public async Task<Guid> QueueIndexAsync(Guid documentId, Guid versionId, IReadOnlyList<Guid> tagIds, bool explicitReindex, CancellationToken token)
        => await QueueIndexCoreAsync(documentId, versionId, tagIds, explicitReindex, null, null, null, token);

    public async Task<Guid> QueueCandidateIndexAsync(Guid candidateId, Guid documentId, Guid versionId, IReadOnlyList<Guid> tagIds,
        string publishLeaseOwner, CancellationToken token)
        => await QueueIndexCoreAsync(documentId, versionId, tagIds, false, candidateId, publishLeaseOwner, null, token);

    public async Task<Guid> QueuePrivateBatchIndexAsync(
        Guid batchId,
        Guid documentId,
        Guid versionId,
        IReadOnlyList<Guid> tagIds,
        CancellationToken token) =>
        await QueueIndexCoreAsync(
            documentId,
            versionId,
            tagIds,
            false,
            null,
            null,
            batchId,
            token);

    private async Task<Guid> QueueIndexCoreAsync(Guid documentId, Guid versionId, IReadOnlyList<Guid> tagIds, bool explicitReindex,
        Guid? candidateId, string? publishLeaseOwner, Guid? privateBatchId, CancellationToken token)
    {
        if (tagIds.Count == 0) throw new ArgumentException("At least one knowledge tag is required.", nameof(tagIds));
        var distinctTags = tagIds.Distinct().Order().ToArray();
        if (distinctTags.Length > GuidBatchQuery.MaximumBatchSize)
            throw new ArgumentException($"No more than {GuidBatchQuery.MaximumBatchSize} knowledge tags may be indexed.", nameof(tagIds));
        var tagPredicate = GuidBatchQuery.BuildPredicate<KnowledgeTagEntity>(distinctTags, tag => tag.Id);
        if (await database.KnowledgeTags.Where(tag => tag.IsEnabled).Where(tagPredicate).CountAsync(token) != distinctTags.Length)
            throw new ArgumentException("Only enabled knowledge tags may be indexed.", nameof(tagIds));
        var document = await database.KnowledgeDocuments.SingleOrDefaultAsync(item => item.Id == documentId, token) ?? throw new KeyNotFoundException();
        var version = await database.KnowledgeDocumentVersions.SingleOrDefaultAsync(item => item.Id == versionId && item.KnowledgeDocumentId == documentId, token) ?? throw new KeyNotFoundException();
        var embeddingConfiguration = await database.ModelConfigs.AsNoTracking()
            .Where(item => item.ConfigurationType == "embedding" && item.IsEnabled)
            .OrderByDescending(item => item.IsDefault)
            .ThenBy(item => item.CreatedAtUtc)
            .FirstOrDefaultAsync(token)
            ?? throw new InvalidOperationException("No enabled embedding model configuration exists.");
        var embeddingDimension = embeddingConfiguration.EmbeddingDimension
            ?? throw new InvalidOperationException("The enabled embedding model does not define its vector dimension.");
        var embeddingContract = EmbeddingSpaceContract.Create(
            embeddingConfiguration.Provider,
            embeddingConfiguration.BaseUrl,
            embeddingConfiguration.Model,
            embeddingDimension,
            options.Distance);
        if (document.IsDeleteRequested) throw new InvalidOperationException("The document is pending physical deletion.");
        var reenable = document.Status == "disabled";
        if (reenable && !explicitReindex) throw new InvalidOperationException("A disabled document requires explicit reindex to re-enable it.");
        if (version.Status is not ("approved" or "indexing" or "active" or "indexed") && !(reenable && version.Status == "disabled"))
            throw new InvalidOperationException("Only approved versions can be indexed.");
        var incompatible = document.ActiveVersionId is not null &&
            (document.ActiveEmbeddingDimension != embeddingDimension || !string.Equals(document.ActiveDistance, DistanceValue(options.Distance), StringComparison.OrdinalIgnoreCase));
        if (incompatible && !explicitReindex)
            throw new InvalidOperationException("The active embedding dimension or distance differs; use explicit reindex to migrate the active contract.");

        var id = StableJobId(versionId);
        var existing = await database.KnowledgeIndexJobs.SingleOrDefaultAsync(item => item.Id == id, token);
        var reuseExisting = existing is not null && !explicitReindex && existing.Status is not "failed";
        if (reuseExisting && candidateId is null) return existing!.Id;
        if (existing?.Status is "leased" or "activating") throw new InvalidOperationException("An index worker is already processing this version.");
        var generation = (existing?.Generation ?? 0) + 1;
        var targetCollection = embeddingContract.CollectionName;
        await using var transaction = await BeginTransactionAsync(token);
        KnowledgeCandidateEntity? candidate = null;
        if (candidateId is { } ownedCandidateId)
        {
            var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
            var publishJob = await database.DurableJobs.FromSqlInterpolated(
                    $"SELECT * FROM durable_job WHERE Id = {ownedCandidateId} FOR UPDATE").AsNoTracking().SingleOrDefaultAsync(token);
            if (string.IsNullOrWhiteSpace(publishLeaseOwner) || publishJob is null ||
                publishJob.JobType != "PublishKnowledgeCandidate" || publishJob.Status != "leased" ||
                publishJob.LeaseOwner != publishLeaseOwner || publishJob.LeaseExpiresAtUtc <= nowUtc)
                throw new InvalidOperationException("Candidate publish lease ownership was lost before indexing was queued.");
            candidate = await database.KnowledgeCandidates.SingleOrDefaultAsync(item => item.Id == ownedCandidateId &&
                item.KnowledgeDocumentVersionId == versionId &&
                (item.Status == "approved_pending_index" || item.Status == "indexing"), token)
                ?? throw new InvalidOperationException("Candidate is not eligible for indexing.");
            if (reuseExisting)
            {
                _ = await database.KnowledgeIndexJobs.FromSqlInterpolated(
                    $"SELECT * FROM knowledge_index_job WHERE Id = {existing!.Id} FOR UPDATE").AsNoTracking().SingleAsync(token);
                if (candidate.Status != "indexing")
                {
                    candidate.Status = "indexing"; candidate.Version++; candidate.UpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
                }
                await database.SaveChangesAsync(token);
                if (transaction is not null) await transaction.CommitAsync(token);
                return existing.Id;
            }
        }
        var chunks = await database.KnowledgeChunks.Where(chunk => chunk.KnowledgeDocumentVersionId == versionId && chunk.Status == "approved").ToArrayAsync(token);
        if (chunks.Length == 0) throw new InvalidOperationException("The version has no approved chunks.");
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (existing is { CollectionName.Length: > 0, IsCollectionExclusive: true }
            && existing.CollectionName != document.ActiveCollectionName)
            await AddCleanupJobAsync(documentId, versionId, existing.CollectionName, existing.Dimension, ParseDistance(existing.Distance),
                existing.Generation, now, existing.Id, existing.LeaseExpiresAtUtc, existing.IsCollectionExclusive, token);
        if (existing is null)
        {
            existing = new KnowledgeIndexJobEntity { Id = id, KnowledgeDocumentId = documentId, KnowledgeDocumentVersionId = versionId };
            database.KnowledgeIndexJobs.Add(existing);
        }
        existing.PreviousActiveVersionId = document.ActiveVersionId;
        existing.PrivateKnowledgeIngestBatchId = privateBatchId;
        existing.PreviousActiveCollectionName = document.ActiveCollectionName;
        existing.PreviousActiveEmbeddingContractKey = document.ActiveEmbeddingContractKey;
        existing.PreviousActiveEmbeddingDimension = document.ActiveEmbeddingDimension;
        existing.PreviousActiveDistance = document.ActiveDistance;
        existing.PreviousActiveCollectionExclusive = document.ActiveCollectionExclusive;
        existing.Generation = generation;
        existing.Operation = explicitReindex ? "reindex" : "index";
        existing.CollectionName = targetCollection;
        existing.EmbeddingContractKey = embeddingContract.Key;
        existing.IsCollectionExclusive = false;
        existing.ModelConfigurationId = embeddingConfiguration.Id;
        existing.ModelConfigurationVersion = embeddingConfiguration.Version;
        existing.Dimension = embeddingDimension;
        existing.Distance = DistanceValue(options.Distance);
        existing.PendingTagIdsJson = SerializeTagIds(distinctTags);
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
        document.StateVersion++;
        document.UpdatedAtUtc = version.UpdatedAtUtc = now;
        if (candidate is not null && candidate.Status != "indexing")
        {
            candidate.Status = "indexing";
            candidate.Version++;
            candidate.UpdatedAtUtc = now;
        }
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
            job.KnowledgeDocumentVersionId, owner, job.Generation, job.IsCollectionExclusive);
    }

    public async Task<bool> RenewLeaseAsync(Guid jobId, string owner, DateTime nowUtc, TimeSpan duration, CancellationToken token)
    {
        var changed = await database.KnowledgeIndexJobs.Where(job => job.Id == jobId && (job.Status == "leased" || job.Status == "activating") && job.LeaseOwner == owner)
            .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.LeaseExpiresAtUtc, nowUtc.Add(duration))
                .SetProperty(job => job.Version, job => job.Version + 1).SetProperty(job => job.UpdatedAtUtc, nowUtc), token);
        return changed == 1;
    }

    public async Task<bool> CanPhysicallyDeleteCollectionAsync(
        string collectionName,
        bool isCollectionExclusive,
        CancellationToken token)
    {
        if (!isCollectionExclusive || EmbeddingSpaceContract.IsSharedCollectionName(collectionName)) return false;
        return !await database.KnowledgeDocuments.AsNoTracking().AnyAsync(
            document => document.Status == "active"
                        && document.ActiveVersionId != null
                        && document.ActiveCollectionName == collectionName,
            token);
    }

    public async Task<KnowledgeIndexWork> LoadIndexWorkAsync(Guid jobId, CancellationToken token)
    {
        var job = await database.KnowledgeIndexJobs.AsNoTracking().SingleOrDefaultAsync(item => item.Id == jobId, token) ?? throw new KeyNotFoundException();
        if (job.Operation == "cleanup" || job.Status != "leased" || job.LeaseOwner is null) throw new InvalidOperationException("The index job is not owned by an active worker.");
        var chunks = await database.KnowledgeChunks.AsNoTracking().Where(chunk => chunk.KnowledgeDocumentVersionId == job.KnowledgeDocumentVersionId && chunk.Status == "approved")
            .OrderBy(chunk => chunk.Sequence).Select(chunk => new { chunk.Id, chunk.Text }).ToArrayAsync(token);
        var tags = ParseTagIds(job.PendingTagIdsJson);
        return new KnowledgeIndexWork(job.Id, job.KnowledgeDocumentId, job.KnowledgeDocumentVersionId, job.PreviousActiveVersionId,
            job.CollectionName, job.Dimension, ParseDistance(job.Distance), chunks.Select(chunk => new KnowledgeIndexChunk(chunk.Id, job.KnowledgeDocumentId,
                job.KnowledgeDocumentVersionId, chunk.Text, tags)).ToArray(), job.LeaseOwner, job.Generation,
            job.PreviousActiveCollectionName, job.PreviousActiveEmbeddingDimension,
            job.PreviousActiveDistance is null ? null : ParseDistance(job.PreviousActiveDistance), job.IsCollectionExclusive,
            job.PreviousActiveCollectionExclusive, job.ModelConfigurationId, job.ModelConfigurationVersion,
            job.PrivateKnowledgeIngestBatchId, job.EmbeddingContractKey, job.PreviousActiveEmbeddingContractKey);
    }

    public Task<bool> CompleteIndexAsync(
        KnowledgeIndexWork work,
        CancellationToken token) =>
        work.PrivateKnowledgeIngestBatchId is null
            ? ActivateVersionAsync(work, token)
            : StageAndTryActivatePrivateBatchAsync(work, token);

    private async Task<bool> StageAndTryActivatePrivateBatchAsync(
        KnowledgeIndexWork work,
        CancellationToken token)
    {
        if (work.LeaseOwner is null
            || work.PrivateKnowledgeIngestBatchId is not { } batchId)
        {
            return false;
        }

        await using var transaction = await BeginTransactionAsync(token);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var job = await database.KnowledgeIndexJobs.SingleOrDefaultAsync(
            x => x.Id == work.JobId
                 && x.Status == "leased"
                 && x.LeaseOwner == work.LeaseOwner
                 && x.PrivateKnowledgeIngestBatchId == batchId,
            token);
        if (job is null)
        {
            if (transaction is not null) await transaction.RollbackAsync(token);
            return false;
        }
        var version = await database.KnowledgeDocumentVersions.SingleAsync(
            x => x.Id == work.VersionId,
            token);
        version.Status = "indexed";
        version.IsPublished = false;
        version.IndexCollectionName = work.CollectionName;
        version.IndexEmbeddingContractKey = work.EmbeddingContractKey;
        version.EmbeddingDimension = work.Dimension;
        version.VectorDistance = DistanceValue(work.Distance);
        version.IndexGeneration = work.Generation;
        version.IndexCollectionExclusive = work.IsCollectionExclusive;
        version.UpdatedAtUtc = now;
        await ReplaceChunkTagsAsync(work, token);
        job.Status = "staged";
        job.LeaseOwner = null;
        job.LeaseExpiresAtUtc = null;
        job.FailureReason = null;
        job.Version++;
        job.UpdatedAtUtc = now;
        await database.SaveChangesAsync(token);

        var expectedVersionIds = await database.PrivateKnowledgeIngestItems
            .Where(x => x.BatchId == batchId
                        && x.ChangeKind != "Duplicate"
                        && x.StagedVersionId != null)
            .Select(x => x.StagedVersionId!.Value)
            .ToArrayAsync(token);
        var batchJobs = await database.KnowledgeIndexJobs
            .Where(x => x.PrivateKnowledgeIngestBatchId == batchId
                        && expectedVersionIds.Contains(x.KnowledgeDocumentVersionId))
            .ToArrayAsync(token);
        if (expectedVersionIds.Length == 0
            || batchJobs.Length != expectedVersionIds.Length
            || batchJobs.Any(x => x.Status != "staged"))
        {
            if (transaction is not null) await transaction.CommitAsync(token);
            return true;
        }

        var documentIds = batchJobs.Select(x => x.KnowledgeDocumentId).Distinct().ToArray();
        var documents = await database.KnowledgeDocuments
            .Where(x => documentIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, token);
        var newVersionIds = batchJobs.Select(x => x.KnowledgeDocumentVersionId).ToArray();
        var newVersions = await database.KnowledgeDocumentVersions
            .Where(x => newVersionIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, token);
        foreach (var batchJob in batchJobs.OrderBy(x => x.Id))
        {
            var document = documents[batchJob.KnowledgeDocumentId];
            if (document.IsDeleteRequested
                || document.Status == "disabled"
                || document.ActiveVersionId != batchJob.PreviousActiveVersionId
                || document.ActiveCollectionName != batchJob.PreviousActiveCollectionName)
            {
                if (transaction is not null) await transaction.RollbackAsync(token);
                return false;
            }
            var next = newVersions[batchJob.KnowledgeDocumentVersionId];
            document.ActiveVersionId = next.Id;
            document.ActiveCollectionName = batchJob.CollectionName;
            document.ActiveEmbeddingContractKey = batchJob.EmbeddingContractKey;
            document.ActiveEmbeddingDimension = batchJob.Dimension;
            document.ActiveDistance = batchJob.Distance;
            document.ActiveIndexGeneration = batchJob.Generation;
            document.ActiveCollectionExclusive = batchJob.IsCollectionExclusive;
            document.Status = "active";
            document.StateVersion++;
            document.UpdatedAtUtc = now;
            next.Status = "active";
            next.IsPublished = true;
            next.IndexEmbeddingContractKey = batchJob.EmbeddingContractKey;
            next.UpdatedAtUtc = now;
            if (batchJob.PreviousActiveVersionId is { } previousId
                && previousId != next.Id)
            {
                var previous = await database.KnowledgeDocumentVersions.SingleAsync(
                    x => x.Id == previousId,
                    token);
                previous.Status = "indexed";
                previous.IsPublished = false;
                previous.UpdatedAtUtc = now;
                if (batchJob.PreviousActiveCollectionName is { } oldCollection
                    && KnowledgeIndexTransition.RequiresPreviousVersionCleanup(previousId, next.Id, oldCollection, batchJob.CollectionName))
                {
                    await AddCleanupJobAsync(
                        batchJob.KnowledgeDocumentId,
                        previousId,
                        oldCollection,
                        batchJob.PreviousActiveEmbeddingDimension ?? batchJob.Dimension,
                        ParseDistance(batchJob.PreviousActiveDistance ?? batchJob.Distance),
                        0,
                        now,
                        batchJob.Id,
                        null,
                        batchJob.PreviousActiveCollectionExclusive,
                        token);
                }
            }
            batchJob.Status = "completed";
            batchJob.Version++;
            batchJob.UpdatedAtUtc = now;
        }

        var ingestBatch = await database.PrivateKnowledgeIngestBatches.SingleAsync(
            x => x.Id == batchId,
            token);
        ingestBatch.Status = "Activated";
        ingestBatch.FailureCode = null;
        ingestBatch.FinalNotificationState = "Queued";
        ingestBatch.Version++;
        ingestBatch.UpdatedAtUtc = now;
        var source = await database.ConversationMessages.AsNoTracking()
            .SingleAsync(x => x.Id == ingestBatch.SourceConversationMessageId, token);
        var notificationKey = $"private-ingest-final:{ingestBatch.Id:D}";
        if (!await database.SendCommands.AnyAsync(
                x => x.IdempotencyKey == notificationKey,
                token))
        {
            var sendStatus = await MySqlRobotSendCoordinator.InitialStatusAsync(
                database,
                ingestBatch.RobotConfigId,
                token);
            database.SendCommands.Add(new SendCommandEntity
            {
                RobotConfigId = ingestBatch.RobotConfigId,
                IdempotencyKey = notificationKey,
                PayloadJson = JsonSerializer.Serialize(new
                {
                    GroupName = source.PeerDisplayName ?? source.SenderDisplayName,
                    Text = $"知识整理完成：新增 {ingestBatch.NewCount}，重复 {ingestBatch.DuplicateCount}，补充 {ingestBatch.SupplementCount}，纠正 {ingestBatch.CorrectionCount}。"
                }),
                Status = sendStatus,
                NextAttemptAtUtc = now,
                CreatedAtUtc = now
            });
        }
        await database.SaveChangesAsync(token);
        if (transaction is not null) await transaction.CommitAsync(token);
        return true;
    }

    private async Task ReplaceChunkTagsAsync(
        KnowledgeIndexWork work,
        CancellationToken token)
    {
        var chunkIds = work.Chunks.Select(x => x.Id).ToArray();
        foreach (var chunkBatch in GuidBatchQuery.CreateBatches(chunkIds))
        {
            var predicate = GuidBatchQuery.BuildPredicate<KnowledgeChunkTagEntity>(
                chunkBatch,
                binding => binding.KnowledgeChunkId);
            database.KnowledgeChunkTags.RemoveRange(
                await database.KnowledgeChunkTags.Where(predicate).ToArrayAsync(token));
            await database.SaveChangesAsync(token);
            var ids = chunkBatch.ToHashSet();
            database.KnowledgeChunkTags.AddRange(
                work.Chunks.Where(x => ids.Contains(x.Id))
                    .SelectMany(x => x.TagIds.Select(tagId =>
                        new KnowledgeChunkTagEntity
                        {
                            KnowledgeChunkId = x.Id,
                            KnowledgeTagId = tagId
                        })));
            await database.SaveChangesAsync(token);
        }
    }

    public async Task<ModelProviderConfiguration> LoadEmbeddingConfigurationAsync(
        Guid? modelConfigurationId,
        int? modelConfigurationVersion,
        CancellationToken token)
    {
        var config = await LoadEmbeddingModelAsync(modelConfigurationId, modelConfigurationVersion, token);
        return modelConfigurations.ToProviderConfiguration(new ModelConfigurationRecord(config.Id, config.Name, config.Provider, config.BaseUrl,
            config.Model, config.EncryptedApiKey, config.TimeoutSeconds, config.MaxRetries, config.IsEnabled, config.IsDefault,
            config.EmbeddingDimension, config.WebSearchMode));
    }

    public async Task<EmbeddingSpaceContract> LoadEmbeddingSpaceContractAsync(
        Guid? modelConfigurationId,
        int? modelConfigurationVersion,
        CancellationToken token)
    {
        var config = await LoadEmbeddingModelAsync(modelConfigurationId, modelConfigurationVersion, token);
        var dimension = config.EmbeddingDimension
            ?? throw new InvalidOperationException("The embedding model does not define its vector dimension.");
        return EmbeddingSpaceContract.Create(config.Provider, config.BaseUrl, config.Model, dimension, options.Distance);
    }

    private async Task<ModelConfigEntity> LoadEmbeddingModelAsync(
        Guid? modelConfigurationId,
        int? modelConfigurationVersion,
        CancellationToken token)
    {
        var configurations = database.ModelConfigs.AsNoTracking()
            .Where(item => item.ConfigurationType == "embedding" && item.IsEnabled);
        var config = modelConfigurationId is { } id
            ? await configurations.SingleOrDefaultAsync(item => item.Id == id, token)
            : await configurations.OrderByDescending(item => item.IsDefault).ThenBy(item => item.CreatedAtUtc).FirstOrDefaultAsync(token);
        if (config is null)
            throw new InvalidOperationException("The queued embedding model configuration is unavailable.");
        if (modelConfigurationVersion is { } expectedVersion && config.Version != expectedVersion)
            throw new InvalidOperationException("The queued embedding model configuration changed; submit a new index job.");
        return config;
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
                .SetProperty(document => document.ActiveCollectionName, work.CollectionName)
                .SetProperty(document => document.ActiveEmbeddingContractKey, work.EmbeddingContractKey)
                .SetProperty(document => document.ActiveEmbeddingDimension, work.Dimension)
                .SetProperty(document => document.ActiveDistance, DistanceValue(work.Distance)).SetProperty(document => document.ActiveIndexGeneration, work.Generation)
                .SetProperty(document => document.ActiveCollectionExclusive, work.IsCollectionExclusive)
                .SetProperty(document => document.Status, "active").SetProperty(document => document.StateVersion, document => document.StateVersion + 1)
                .SetProperty(document => document.UpdatedAtUtc, now), token);
        if (documentChanged != 1) { if (transaction is not null) await transaction.RollbackAsync(token); return false; }
        var versionChanged = await database.KnowledgeDocumentVersions.Where(version => version.Id == work.VersionId && version.KnowledgeDocumentId == work.DocumentId && version.Status != "disabled")
            .ExecuteUpdateAsync(setters => setters.SetProperty(version => version.Status, "active").SetProperty(version => version.IsPublished, true)
                .SetProperty(version => version.IndexCollectionName, work.CollectionName)
                .SetProperty(version => version.IndexEmbeddingContractKey, work.EmbeddingContractKey)
                .SetProperty(version => version.EmbeddingDimension, work.Dimension)
                .SetProperty(version => version.VectorDistance, DistanceValue(work.Distance)).SetProperty(version => version.IndexGeneration, work.Generation)
                .SetProperty(version => version.IndexCollectionExclusive, work.IsCollectionExclusive)
                .SetProperty(version => version.UpdatedAtUtc, now), token);
        if (versionChanged != 1) { if (transaction is not null) await transaction.RollbackAsync(token); return false; }
        await database.KnowledgeCandidates.Where(candidate => candidate.KnowledgeDocumentVersionId == work.VersionId &&
                (candidate.Status == "indexing" || candidate.Status == "approved_pending_index"))
            .ExecuteUpdateAsync(setters => setters.SetProperty(candidate => candidate.Status, "published")
                .SetProperty(candidate => candidate.PublishedAtUtc, now).SetProperty(candidate => candidate.Version, candidate => candidate.Version + 1)
                .SetProperty(candidate => candidate.UpdatedAtUtc, now), token);
        var chunkIds = work.Chunks.Select(chunk => chunk.Id).ToArray();
        foreach (var batch in GuidBatchQuery.CreateBatches(chunkIds))
        {
            var predicate = GuidBatchQuery.BuildPredicate<KnowledgeChunkTagEntity>(batch, binding => binding.KnowledgeChunkId);
            var existingBindings = await database.KnowledgeChunkTags.Where(predicate).ToArrayAsync(token);
            database.KnowledgeChunkTags.RemoveRange(existingBindings);
            await database.SaveChangesAsync(token);
            var batchIds = batch.ToHashSet();
            database.KnowledgeChunkTags.AddRange(work.Chunks.Where(chunk => batchIds.Contains(chunk.Id)).SelectMany(chunk => chunk.TagIds.Select(tagId =>
                new KnowledgeChunkTagEntity { KnowledgeChunkId = chunk.Id, KnowledgeTagId = tagId })));
            await database.SaveChangesAsync(token);
        }
        if (work.PreviousActiveVersionId is { } oldVersion
            && work.PreviousActiveCollectionName is { } oldCollection
            && KnowledgeIndexTransition.RequiresPreviousVersionCleanup(oldVersion, work.VersionId, oldCollection, work.CollectionName))
        {
            if (oldVersion != work.VersionId)
                await database.KnowledgeDocumentVersions.Where(version => version.Id == oldVersion).ExecuteUpdateAsync(setters => setters
                    .SetProperty(version => version.Status, "indexed").SetProperty(version => version.IsPublished, false).SetProperty(version => version.UpdatedAtUtc, now), token);
            await AddCleanupJobAsync(work.DocumentId, oldVersion, oldCollection, work.PreviousActiveEmbeddingDimension ?? work.Dimension,
                work.PreviousActiveDistance ?? work.Distance, 0, now, work.JobId, null, work.PreviousActiveCollectionExclusive, token);
        }
        var completed = await database.KnowledgeIndexJobs.Where(job => job.Id == work.JobId && job.Status == "activating" && job.LeaseOwner == work.LeaseOwner)
            .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.Status, "completed").SetProperty(job => job.LeaseOwner, (string?)null)
                .SetProperty(job => job.LeaseExpiresAtUtc, (DateTime?)null).SetProperty(job => job.FailureReason, (string?)null)
                .SetProperty(job => job.Version, job => job.Version + 1).SetProperty(job => job.UpdatedAtUtc, now), token);
        if (completed != 1) { if (transaction is not null) await transaction.RollbackAsync(token); return false; }
        await database.SaveChangesAsync(token);
        if (transaction is not null) await transaction.CommitAsync(token);
        return true;
    }

    public async Task EnqueueCleanupAsync(KnowledgeIndexWork work, CancellationToken token)
    {
        if (work.PreviousActiveVersionId is not { } version
            || work.PreviousActiveCollectionName is not { } collection
            || !KnowledgeIndexTransition.RequiresPreviousVersionCleanup(version, work.VersionId, collection, work.CollectionName)) return;
        await AddCleanupJobAsync(work.DocumentId, version, collection, work.PreviousActiveEmbeddingDimension ?? work.Dimension,
            work.PreviousActiveDistance ?? work.Distance, 0, timeProvider.GetUtcNow().UtcDateTime, work.JobId, null,
            work.PreviousActiveCollectionExclusive, token);
        try { await database.SaveChangesAsync(token); }
        catch (DbUpdateException exception) when (exception.InnerException is MySqlException { Number: 1062 }) { database.ChangeTracker.Clear(); }
    }

    public async Task MarkIndexFailedAsync(Guid jobId, string? leaseOwner, string reason, bool retryable, CancellationToken token)
    {
        database.ChangeTracker.Clear();
        var job = await database.KnowledgeIndexJobs.SingleOrDefaultAsync(item => item.Id == jobId && item.LeaseOwner == leaseOwner &&
            (item.Status == "leased" || item.Status == "activating"), token);
        if (job is null) return;
        job.AttemptCount++;
        job.FailureReason = reason.Length <= 1024 ? reason : reason[..1024];
        job.Status = retryable && job.AttemptCount < 4 ? "retrying" : "failed";
        job.NextAttemptAtUtc = timeProvider.GetUtcNow().UtcDateTime.AddSeconds(job.AttemptCount switch { 1 => 5, 2 => 15, _ => 45 });
        job.LeaseOwner = null; job.LeaseExpiresAtUtc = null; job.Version++; job.UpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        if (job.PrivateKnowledgeIngestBatchId is { } batchId)
        {
            var batch = await database.PrivateKnowledgeIngestBatches.SingleAsync(
                x => x.Id == batchId,
                token);
            batch.Status = job.Status == "retrying" ? "Retryable" : "Failed";
            batch.FailureCode = retryable
                ? "private_knowledge_index_unavailable"
                : "private_knowledge_index_failed";
            batch.Version++;
            batch.UpdatedAtUtc = job.UpdatedAtUtc;
        }
        await database.SaveChangesAsync(token);
    }

    public async Task RetryAsync(Guid jobId, CancellationToken token)
    {
        var job = await database.KnowledgeIndexJobs.SingleOrDefaultAsync(item => item.Id == jobId, token) ?? throw new KeyNotFoundException();
        if (job.Status is not ("failed" or "retrying")) throw new InvalidOperationException("Only failed or retrying index jobs can be retried.");
        if (await database.KnowledgeDocuments.AnyAsync(document => document.Id == job.KnowledgeDocumentId &&
            (document.IsDeleteRequested || document.Status == "disabled"), token))
            throw new InvalidOperationException("A deleted document cannot restart indexing.");
        job.Status = "pending"; job.NextAttemptAtUtc = timeProvider.GetUtcNow().UtcDateTime; job.LeaseOwner = null; job.LeaseExpiresAtUtc = null;
        job.FailureReason = null; job.Version++; job.UpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        await database.SaveChangesAsync(token);
    }

    public async Task DisableAsync(Guid documentId, CancellationToken token)
    {
        database.ChangeTracker.Clear();
        var current = await database.KnowledgeDocuments.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == documentId,
            token) ?? throw new KeyNotFoundException();
        await DisableCoreAsync(documentId, current.StateVersion, null, token);
    }

    public Task DisableAsync(
        Guid documentId,
        int expectedStateVersion,
        string actor,
        CancellationToken token) =>
        DisableCoreAsync(documentId, expectedStateVersion, actor, token);

    private async Task DisableCoreAsync(
        Guid documentId,
        int expectedStateVersion,
        string? actor,
        CancellationToken token)
    {
        database.ChangeTracker.Clear();
        var document = await database.KnowledgeDocuments.AsNoTracking().SingleOrDefaultAsync(item => item.Id == documentId, token) ?? throw new KeyNotFoundException();
        if (document.StateVersion != expectedStateVersion)
            throw Concurrency(document);
        if (document.IsDeleteRequested) throw new DocumentDeleteRequestedException();
        if (document.Status == "disabled") return;
        if (IsInMemory)
        {
            await DisableTrackedAsync(documentId, expectedStateVersion, actor, token);
            return;
        }
        await using var transaction = await BeginTransactionAsync(token);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (document.ActiveVersionId is { } versionId && document.ActiveCollectionName is { } collection)
            await AddCleanupJobAsync(documentId, versionId, collection, document.ActiveEmbeddingDimension ?? options.Dimension,
                document.ActiveDistance is null ? options.Distance : ParseDistance(document.ActiveDistance), document.ActiveIndexGeneration ?? 0,
                now, StableJobId(versionId), null, document.ActiveCollectionExclusive, token);
        var stagedContracts = await database.KnowledgeIndexJobs.AsNoTracking().Where(job => job.KnowledgeDocumentId == documentId &&
                job.Operation != "cleanup" && job.CollectionName != "" && job.CollectionName != document.ActiveCollectionName)
            .Select(job => new { job.Id, job.KnowledgeDocumentVersionId, job.CollectionName, job.Dimension, job.Distance, job.Generation,
                job.LeaseExpiresAtUtc, job.IsCollectionExclusive }).ToArrayAsync(token);
        foreach (var staged in stagedContracts)
            await AddCleanupJobAsync(documentId, staged.KnowledgeDocumentVersionId, staged.CollectionName, staged.Dimension,
                ParseDistance(staged.Distance), staged.Generation, now, staged.Id, staged.LeaseExpiresAtUtc, staged.IsCollectionExclusive, token);
        var documentChanged = await database.KnowledgeDocuments.Where(item => item.Id == documentId && !item.IsDeleteRequested &&
                item.Status == document.Status && item.ActiveVersionId == document.ActiveVersionId && item.StateVersion == expectedStateVersion)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, "disabled").SetProperty(item => item.ActiveVersionId, (Guid?)null)
                .SetProperty(item => item.StateVersion, item => item.StateVersion + 1)
                .SetProperty(item => item.UpdatedAtUtc, now), token);
        if (documentChanged != 1)
        {
            if (transaction is not null) await transaction.RollbackAsync(token);
            await ThrowDisableConflictAsync(documentId, token);
        }
        await database.KnowledgeDocumentVersions.Where(version => version.KnowledgeDocumentId == documentId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(version => version.Status, "disabled").SetProperty(version => version.IsPublished, false)
                .SetProperty(version => version.UpdatedAtUtc, now), token);
        await database.KnowledgeIndexJobs.Where(job => job.KnowledgeDocumentId == documentId && job.Operation != "cleanup" &&
                (job.Status == "pending" || job.Status == "retrying" || job.Status == "leased" || job.Status == "activating"))
            .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.Status, "cancelled").SetProperty(job => job.LeaseOwner, (string?)null)
                .SetProperty(job => job.Version, job => job.Version + 1).SetProperty(job => job.UpdatedAtUtc, now), token);
        AddDocumentAudit(
            actor,
            "knowledge-document.disable",
            documentId,
            new
            {
                before = new { status = document.Status, stateVersion = expectedStateVersion },
                after = new { status = "disabled", stateVersion = expectedStateVersion + 1 }
            });
        await database.SaveChangesAsync(token);
        if (transaction is not null) await transaction.CommitAsync(token);
    }

    private async Task DisableTrackedAsync(
        Guid documentId,
        int expectedStateVersion,
        string? actor,
        CancellationToken token)
    {
        database.ChangeTracker.Clear();
        var document = await database.KnowledgeDocuments.SingleOrDefaultAsync(
            item => item.Id == documentId,
            token) ?? throw new KeyNotFoundException();
        if (document.StateVersion != expectedStateVersion) throw Concurrency(document);
        if (document.IsDeleteRequested) throw new DocumentDeleteRequestedException();
        if (document.Status == "disabled") return;

        var priorStatus = document.Status;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        document.Status = "disabled";
        document.ActiveVersionId = null;
        document.StateVersion++;
        document.UpdatedAtUtc = now;
        foreach (var version in await database.KnowledgeDocumentVersions
                     .Where(item => item.KnowledgeDocumentId == documentId)
                     .ToArrayAsync(token))
        {
            version.Status = "disabled";
            version.IsPublished = false;
            version.UpdatedAtUtc = now;
        }

        foreach (var job in await database.KnowledgeIndexJobs
                     .Where(job => job.KnowledgeDocumentId == documentId &&
                                   job.Operation != "cleanup" &&
                                   (job.Status == "pending" ||
                                    job.Status == "retrying" ||
                                    job.Status == "leased" ||
                                    job.Status == "activating"))
                     .ToArrayAsync(token))
        {
            job.Status = "cancelled";
            job.LeaseOwner = null;
            job.UpdatedAtUtc = now;
            job.Version++;
        }

        AddDocumentAudit(
            actor,
            "knowledge-document.disable",
            documentId,
            new
            {
                before = new { status = priorStatus, stateVersion = expectedStateVersion },
                after = new { status = "disabled", stateVersion = document.StateVersion }
            });
        await database.SaveChangesAsync(token);
    }

    public async Task CompleteCleanupAsync(Guid jobId, string owner, CancellationToken token) => await database.KnowledgeIndexJobs
        .Where(job => job.Id == jobId && job.Status == "leased" && job.LeaseOwner == owner)
        .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.Status, "completed").SetProperty(job => job.LeaseOwner, (string?)null)
            .SetProperty(job => job.LeaseExpiresAtUtc, (DateTime?)null).SetProperty(job => job.Version, job => job.Version + 1)
            .SetProperty(job => job.UpdatedAtUtc, timeProvider.GetUtcNow().UtcDateTime), token);

    public Task<bool> IsIndexLeaseOwnedAsync(Guid jobId, string owner, CancellationToken token) => database.KnowledgeIndexJobs.AsNoTracking()
        .AnyAsync(job => job.Id == jobId && (job.Status == "leased" || job.Status == "activating") && job.LeaseOwner == owner, token);

    public Task<DateTime?> GetCleanupDrainDeadlineAsync(Guid jobId, DateTime nowUtc, CancellationToken token) => database.KnowledgeIndexJobs
        .AsNoTracking().Where(job => job.Id == jobId && job.Operation == "cleanup" && job.DrainUntilUtc > nowUtc)
        .Select(job => job.DrainUntilUtc).SingleOrDefaultAsync(token);

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
        var tags = ids.ToDictionary(id => id, _ => Array.Empty<Guid>());
        foreach (var batch in GuidBatchQuery.CreateBatches(ids))
        {
            var predicate = GuidBatchQuery.BuildPredicate<KnowledgeChunkTagEntity>(batch, binding => binding.KnowledgeChunkId);
            var bindings = await (from binding in database.KnowledgeChunkTags.AsNoTracking().Where(predicate)
                                  join tag in database.KnowledgeTags.AsNoTracking() on binding.KnowledgeTagId equals tag.Id
                                  where tag.IsEnabled
                                  select new { binding.KnowledgeChunkId, binding.KnowledgeTagId }).ToArrayAsync(token);
            foreach (var group in bindings.GroupBy(binding => binding.KnowledgeChunkId))
                tags[group.Key] = group.Select(binding => binding.KnowledgeTagId).ToArray();
        }
        var actual = await vectors.InspectVersionAsync(new VectorCollection(name, dimension, ParseDistance(distance)), active, token);
        var actualById = actual.ToDictionary(point => point.ChunkId);
        var drift = new List<string>();
        foreach (var chunk in expected)
        {
            if (!actualById.TryGetValue(chunk.Id, out var point)) { drift.Add($"missing:{chunk.Id:D}"); continue; }
            if (point.DocumentId != documentId || point.VersionId != active || !point.Active || document.ActiveIndexGeneration is null ||
                point.Generation != document.ActiveIndexGeneration.Value ||
                !point.TagIds.ToHashSet().SetEquals(tags[chunk.Id])) drift.Add($"payload:{chunk.Id:D}");
        }
        if (document.ActiveIndexGeneration is null) drift.Insert(0, "missing-active-generation");
        foreach (var unexpected in actual.Where(point => !ids.Contains(point.ChunkId))) drift.Add($"unexpected:{unexpected.ChunkId:D}");
        return new KnowledgeIndexStatus(document.Id, active, document.Status, name, expected.Length, actual.Count,
            drift.Count == 0 ? "consistent" : "drift", drift, jobs);
    }

    public async Task<IReadOnlyList<VectorSearchHit>> SearchVisibleAsync(IReadOnlyList<float> vector, IReadOnlyList<Guid> requestedTagIds,
        IVectorStore vectors, int limit, CancellationToken token)
    {
        var scope = await new KnowledgeTagScopeResolver(database).ResolveAsync(requestedTagIds, token);
        return await SearchVisibleAsync(vector, scope, vectors, limit, token);
    }

    public async Task<IReadOnlyList<VectorSearchHit>> SearchVisibleAsync(
        IReadOnlyList<float> vector,
        IReadOnlyList<Guid> requestedTagIds,
        EmbeddingSpaceContract queryContract,
        IVectorStore vectors,
        int limit,
        CancellationToken token)
    {
        var scope = await new KnowledgeTagScopeResolver(database).ResolveAsync(requestedTagIds, token);
        return await SearchVisibleAsync(vector, scope, queryContract, vectors, limit, token);
    }

    public async Task<IReadOnlyList<VectorSearchHit>> SearchVisibleAsync(IReadOnlyList<float> vector, KnowledgeTagScope scope,
        IVectorStore vectors, int limit, CancellationToken token) =>
        await SearchVisibleCoreAsync(vector, scope, null, vectors, limit, token);

    public async Task<IReadOnlyList<VectorSearchHit>> SearchVisibleAsync(
        IReadOnlyList<float> vector,
        KnowledgeTagScope scope,
        EmbeddingSpaceContract queryContract,
        IVectorStore vectors,
        int limit,
        CancellationToken token) =>
        await SearchVisibleCoreAsync(vector, scope, queryContract, vectors, limit, token);

    private async Task<IReadOnlyList<VectorSearchHit>> SearchVisibleCoreAsync(
        IReadOnlyList<float> vector,
        KnowledgeTagScope scope,
        EmbeddingSpaceContract? queryContract,
        IVectorStore vectors,
        int limit,
        CancellationToken token)
    {
        const int maximumCandidateCount = 200;
        var searchLimit = Math.Clamp(limit, 1, 50);
        if (!string.Equals(scope.FilterDescriptor, KnowledgeTagScopeResolver.EffectiveVisibleTagsFilter, StringComparison.Ordinal))
            throw new ArgumentException("Knowledge tag scope uses an unsupported filter descriptor.", nameof(scope));
        var visibleTagIds = scope.EffectiveVisibleTagIds.Distinct().Order().ToArray();
        if (visibleTagIds.Length == 0) return [];
        var eligibleVersionIds = new HashSet<Guid>();
        foreach (var batch in GuidBatchQuery.CreateBatches(visibleTagIds))
        {
            var tagPredicate = GuidBatchQuery.BuildPredicate<KnowledgeChunkTagEntity>(batch, binding => binding.KnowledgeTagId);
            var matchedVersions = await (from binding in database.KnowledgeChunkTags.AsNoTracking().Where(tagPredicate)
                                         join tag in database.KnowledgeTags.AsNoTracking() on binding.KnowledgeTagId equals tag.Id
                                         join chunk in database.KnowledgeChunks.AsNoTracking() on binding.KnowledgeChunkId equals chunk.Id
                                         join version in database.KnowledgeDocumentVersions.AsNoTracking() on chunk.KnowledgeDocumentVersionId equals version.Id
                                         join document in database.KnowledgeDocuments.AsNoTracking() on version.KnowledgeDocumentId equals document.Id
                                         where tag.IsEnabled && chunk.Status == "approved" && version.Status == "active" && version.IsPublished &&
                                               document.Status == "active" && !document.IsDeleteRequested && document.ActiveVersionId == version.Id &&
                                               document.ActiveCollectionName != null && document.ActiveEmbeddingDimension == vector.Count && document.ActiveDistance != null
                                         select version.Id).Distinct().ToArrayAsync(token);
            eligibleVersionIds.UnionWith(matchedVersions);
        }
        if (eligibleVersionIds.Count == 0) return [];

        var activeDocuments = await database.KnowledgeDocuments.AsNoTracking().Where(document => !document.IsDeleteRequested && document.Status == "active" &&
            document.ActiveVersionId != null && document.ActiveCollectionName != null && document.ActiveEmbeddingDimension == vector.Count && document.ActiveDistance != null)
            .Select(document => new
            {
                document.ActiveVersionId,
                document.ActiveCollectionName,
                document.ActiveEmbeddingContractKey,
                document.ActiveEmbeddingDimension,
                document.ActiveDistance
            }).ToArrayAsync(token);

        var relevantDocuments = activeDocuments.Where(document => eligibleVersionIds.Contains(document.ActiveVersionId!.Value)).ToArray();
        if (queryContract is not null)
        {
            if (relevantDocuments.Any(document =>
                    document.ActiveEmbeddingContractKey is { } key
                    && !string.Equals(key, queryContract.Key, StringComparison.Ordinal)))
                throw new VectorCollectionConfigurationException(
                    "Active knowledge uses an embedding contract that does not match the query embedding contract.");
            if (relevantDocuments.Any(document =>
                    string.Equals(document.ActiveEmbeddingContractKey, queryContract.Key, StringComparison.Ordinal)
                    && !string.Equals(document.ActiveCollectionName, queryContract.CollectionName, StringComparison.Ordinal)))
                throw new VectorCollectionConfigurationException(
                    "Active knowledge has a shared collection name that does not match its embedding contract.");
            relevantDocuments = relevantDocuments.Where(document =>
                document.ActiveEmbeddingContractKey is null
                || string.Equals(document.ActiveEmbeddingContractKey, queryContract.Key, StringComparison.Ordinal)).ToArray();
        }

        var contracts = relevantDocuments
            .GroupBy(document => new { document.ActiveCollectionName, document.ActiveEmbeddingDimension, document.ActiveDistance })
            .Select(group => new SearchCollectionContract(group.Key.ActiveCollectionName!, group.Key.ActiveEmbeddingDimension!.Value,
                group.Key.ActiveDistance!, group.Select(item => item.ActiveVersionId!.Value).Distinct().Order().ToArray()))
            .OrderBy(contract => contract.CollectionName, StringComparer.Ordinal).ThenBy(contract => contract.Dimension)
            .ThenBy(contract => contract.Distance, StringComparer.Ordinal).ToArray();
        if (contracts.Length > options.MaximumCollectionsPerSearch)
            throw new KnowledgeSearchCapacityException(contracts.Length, options.MaximumCollectionsPerSearch);
        if (contracts.Length == 0) return [];

        const int maximumConcurrentSearches = 4;
        var candidatePages = Enumerable.Repeat<IReadOnlyList<VectorSearchHit>>([], contracts.Length).ToArray();
        var successfulCollectionCount = 0;
        var unavailableCollectionCount = 0;
        VectorStoreUnavailableException? firstUnavailable = null;
        using (var concurrency = new SemaphoreSlim(maximumConcurrentSearches, maximumConcurrentSearches))
        {
            await Task.WhenAll(contracts.Select(async (contract, index) =>
            {
                await concurrency.WaitAsync(token);
                try
                {
                    var requestLimit = Math.Max(1,
                        maximumCandidateCount / contracts.Length + (index < maximumCandidateCount % contracts.Length ? 1 : 0));
                    var request = new VectorSearchRequest(new VectorCollection(contract.CollectionName, contract.Dimension, ParseDistance(contract.Distance)),
                        vector, visibleTagIds, contract.ActiveVersionIds, requestLimit);
                    candidatePages[index] = (await vectors.SearchAsync(request, token)).Take(requestLimit).ToArray();
                    Interlocked.Increment(ref successfulCollectionCount);
                }
                catch (VectorStoreUnavailableException exception)
                {
                    Interlocked.Increment(ref unavailableCollectionCount);
                    Interlocked.CompareExchange(ref firstUnavailable, exception, null);
                }
                finally { concurrency.Release(); }
            }));
        }
        if (successfulCollectionCount == 0 && firstUnavailable is not null)
            throw new VectorStoreUnavailableException(
                "All eligible knowledge vector collections are unavailable.",
                firstUnavailable);
        if (unavailableCollectionCount > 0)
            logger?.LogWarning(
                "Knowledge vector search used partial results because {UnavailableCollectionCount} of {EligibleCollectionCount} eligible collections were unavailable.",
                unavailableCollectionCount,
                contracts.Length);

        var candidates = candidatePages.SelectMany(page => page).ToArray();
        if (candidates.Length == 0) return [];

        var orderedCandidates = candidates.OrderByDescending(hit => hit.Score).ThenBy(hit => hit.ChunkId)
            .DistinctBy(hit => hit.ChunkId).Take(maximumCandidateCount).ToArray();
        var liveChunkIds = new HashSet<Guid>();
        foreach (var batch in GuidBatchQuery.CreateBatches(orderedCandidates.Select(hit => hit.ChunkId)))
        {
            var predicate = GuidBatchQuery.BuildPredicate<KnowledgeChunkEntity>(batch, chunk => chunk.Id);
            var matched = await (from chunk in database.KnowledgeChunks.AsNoTracking().Where(predicate)
                                 join version in database.KnowledgeDocumentVersions.AsNoTracking() on chunk.KnowledgeDocumentVersionId equals version.Id
                                 join document in database.KnowledgeDocuments.AsNoTracking() on version.KnowledgeDocumentId equals document.Id
                                 where chunk.Status == "approved" && version.Status == "active" && version.IsPublished && document.Status == "active" &&
                                       !document.IsDeleteRequested && document.ActiveVersionId == version.Id
                                 select chunk.Id).ToArrayAsync(token);
            liveChunkIds.UnionWith(matched);
        }

        var visibleTagSet = visibleTagIds.ToHashSet();
        var visibleChunkIds = new HashSet<Guid>();
        foreach (var batch in GuidBatchQuery.CreateBatches(liveChunkIds))
        {
            var predicate = GuidBatchQuery.BuildPredicate<KnowledgeChunkTagEntity>(batch, binding => binding.KnowledgeChunkId);
            var bindings = await database.KnowledgeChunkTags.AsNoTracking().Where(predicate)
                .Select(binding => new { binding.KnowledgeChunkId, binding.KnowledgeTagId }).ToArrayAsync(token);
            visibleChunkIds.UnionWith(bindings.Where(binding => visibleTagSet.Contains(binding.KnowledgeTagId))
                .Select(binding => binding.KnowledgeChunkId));
        }
        return orderedCandidates.Where(hit => visibleChunkIds.Contains(hit.ChunkId)).Take(searchLimit).ToArray();
    }

    public async Task<IReadOnlyList<KnowledgeVectorContract>> GetDocumentVectorContractsAsync(Guid documentId, CancellationToken token)
    {
        var versions = await database.KnowledgeDocumentVersions.AsNoTracking().Where(version => version.KnowledgeDocumentId == documentId &&
            version.IndexCollectionName != null && version.EmbeddingDimension != null && version.VectorDistance != null)
            .Select(version => new VectorContractRow(version.Id, version.IndexCollectionName!, version.EmbeddingDimension!.Value,
                version.VectorDistance!, version.IndexCollectionExclusive)).ToArrayAsync(token);
        var active = await database.KnowledgeDocuments.AsNoTracking().Where(document => document.Id == documentId && document.ActiveVersionId != null &&
                document.ActiveCollectionName != null && document.ActiveEmbeddingDimension != null && document.ActiveDistance != null)
            .Select(document => new VectorContractRow(document.ActiveVersionId!.Value, document.ActiveCollectionName!, document.ActiveEmbeddingDimension!.Value,
                document.ActiveDistance!, document.ActiveCollectionExclusive)).ToArrayAsync(token);
        var pending = await database.KnowledgeIndexJobs.AsNoTracking().Where(job => job.KnowledgeDocumentId == documentId && job.CollectionName != "")
            .Select(job => new VectorContractRow(job.KnowledgeDocumentVersionId, job.CollectionName, job.Dimension, job.Distance, job.IsCollectionExclusive)).ToArrayAsync(token);
        return versions.Concat(active).Concat(pending).GroupBy(item => new { item.VersionId, item.CollectionName, item.Dimension, item.Distance })
            .Select(group => new KnowledgeVectorContract(new VectorCollection(group.Key.CollectionName, group.Key.Dimension, ParseDistance(group.Key.Distance)),
                group.Key.VersionId, group.Any(item => item.IsCollectionExclusive))).ToArray();
    }

    public Task<DateTime?> GetDocumentIndexDrainDeadlineAsync(Guid documentId, DateTime nowUtc, CancellationToken token) => database.KnowledgeIndexJobs
        .AsNoTracking().Where(job => job.KnowledgeDocumentId == documentId && job.Operation != "cleanup" && job.LeaseExpiresAtUtc > nowUtc &&
            (job.Status == "cancelled" || job.Status == "leased" || job.Status == "activating"))
        .MaxAsync(job => (DateTime?)job.LeaseExpiresAtUtc, token);

    private async Task AddCleanupJobAsync(Guid documentId, Guid versionId, string collection, int dimension, VectorDistance distance,
        int generation, DateTime now, Guid? sourceIndexJobId, DateTime? drainUntilUtc, bool? collectionExclusive, CancellationToken token)
    {
        var id = CleanupJobId(versionId, collection);
        var exclusive = collectionExclusive ?? await database.KnowledgeIndexJobs.AnyAsync(job => job.Operation != "cleanup" &&
            job.KnowledgeDocumentVersionId == versionId && job.CollectionName == collection && job.IsCollectionExclusive, token);
        var existing = await database.KnowledgeIndexJobs.SingleOrDefaultAsync(job => job.Id == id, token);
        if (existing is not null)
        {
            if (exclusive) existing.IsCollectionExclusive = true;
            if (drainUntilUtc is { } drain && (existing.DrainUntilUtc is null || drain > existing.DrainUntilUtc)) existing.DrainUntilUtc = drain;
            if (existing.Status == "completed" && drainUntilUtc is { } futureDrain && futureDrain > now)
            {
                existing.Status = "pending";
                existing.AttemptCount = 0;
                existing.NextAttemptAtUtc = now;
                existing.LeaseOwner = null;
                existing.LeaseExpiresAtUtc = null;
                existing.FailureReason = null;
                existing.Version++;
                existing.UpdatedAtUtc = now;
            }
            return;
        }
        database.KnowledgeIndexJobs.Add(new KnowledgeIndexJobEntity
        {
            Id = id, KnowledgeDocumentId = documentId, KnowledgeDocumentVersionId = versionId, Operation = "cleanup", CollectionName = collection,
            Dimension = dimension, Distance = DistanceValue(distance), Generation = generation, SourceIndexJobId = sourceIndexJobId,
            IsCollectionExclusive = exclusive, DrainUntilUtc = drainUntilUtc, NextAttemptAtUtc = now
        });
    }

    private void AddDocumentAudit(string? actor, string action, Guid documentId, object detail)
    {
        if (actor is null) return;
        database.AdministrationAudits.Add(new AdministrationAuditEntity
        {
            Actor = actor,
            Action = action,
            TargetType = "knowledge-document",
            TargetId = documentId.ToString("D"),
            SanitizedDetailJson = JsonSerializer.Serialize(detail)
        });
    }

    private async Task ThrowDisableConflictAsync(Guid documentId, CancellationToken token)
    {
        database.ChangeTracker.Clear();
        var current = await database.KnowledgeDocuments.AsNoTracking().SingleOrDefaultAsync(
            document => document.Id == documentId,
            token);
        if (current is null) throw new KeyNotFoundException();
        if (current.IsDeleteRequested) throw new DocumentDeleteRequestedException();
        throw Concurrency(current);
    }

    private static DocumentConcurrencyException Concurrency(KnowledgeDocumentEntity document) =>
        new(new KnowledgeDocumentCurrentState(document.Id, document.Status, document.StateVersion));

    private bool IsInMemory =>
        database.Database.ProviderName?.Contains("InMemory", StringComparison.OrdinalIgnoreCase) == true;

    private async Task<IDbContextTransaction?> BeginTransactionAsync(CancellationToken token) =>
        IsInMemory || !database.Database.IsRelational()
            ? null
            : await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
    private static Guid StableJobId(Guid versionId) => HashGuid($"index:{versionId:N}");
    private static Guid CleanupJobId(Guid versionId, string collection) => HashGuid($"cleanup-index:{versionId:N}:{collection}");
    private static Guid HashGuid(string input) => new(SHA256.HashData(Encoding.UTF8.GetBytes(input)).AsSpan(0, 16));
    private static string StagingCollection(string baseName, Guid jobId, int generation) => $"{baseName}_g{generation}_{jobId:N}";
    private static string DistanceValue(VectorDistance distance) => distance.ToString().ToLowerInvariant();
    private static VectorDistance ParseDistance(string value) => Enum.Parse<VectorDistance>(value, true);
    private static string SerializeTagIds(IEnumerable<Guid> tagIds) => JsonSerializer.Serialize(tagIds.Order().Select(id => id.ToString("D")));
    private static Guid[] ParseTagIds(string json) => JsonSerializer.Deserialize<string[]>(json)?.Select(Guid.Parse).Order().ToArray() ?? [];
    private sealed record VectorContractRow(Guid VersionId, string CollectionName, int Dimension, string Distance, bool IsCollectionExclusive = false);
    private sealed record SearchCollectionContract(string CollectionName, int Dimension, string Distance, Guid[] ActiveVersionIds);
}
