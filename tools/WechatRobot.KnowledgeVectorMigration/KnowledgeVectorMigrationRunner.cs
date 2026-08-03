using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Knowledge;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.KnowledgeVectorMigration;

public sealed record MigrationSummary(
    int VersionCount,
    int SourceCollectionCount,
    int DestinationCollectionCount,
    int PointCount,
    int MismatchCount,
    string State);

public sealed class KnowledgeVectorMigrationRunner(
    WechatRobotDbContext database,
    IVectorStore vectors,
    KnowledgeVectorMigrationPlanner planner,
    string checkpointPath)
{
    public async Task<MigrationSummary> DryRunAsync(CancellationToken token)
    {
        var documents = await database.KnowledgeDocuments.AsNoTracking()
            .Where(item => item.Status == "active"
                && !item.IsDeleteRequested
                && item.ActiveVersionId != null
                && item.ActiveCollectionName != null
                && item.ActiveEmbeddingDimension != null
                && item.ActiveDistance != null)
            .ToArrayAsync(token);
        var legacyDocuments = documents
            .Where(item => !EmbeddingSpaceContract.IsSharedCollectionName(item.ActiveCollectionName))
            .ToArray();
        var versionIds = legacyDocuments.Select(item => item.ActiveVersionId!.Value).ToArray();
        var versions = new Dictionary<Guid, KnowledgeDocumentVersionEntity>();
        var jobs = new List<KnowledgeIndexJobEntity>();
        foreach (var batch in GuidBatchQuery.CreateBatches(versionIds))
        {
            var versionPredicate = GuidBatchQuery.BuildPredicate<KnowledgeDocumentVersionEntity>(batch, item => item.Id);
            foreach (var version in await database.KnowledgeDocumentVersions.AsNoTracking()
                         .Where(versionPredicate)
                         .ToArrayAsync(token))
                versions.TryAdd(version.Id, version);

            var jobPredicate = GuidBatchQuery.BuildPredicate<KnowledgeIndexJobEntity>(batch, item => item.KnowledgeDocumentVersionId);
            jobs.AddRange(await database.KnowledgeIndexJobs.AsNoTracking()
                .Where(jobPredicate)
                .Where(item => item.ModelConfigurationId != null && item.Status == "completed")
                .ToArrayAsync(token));
        }
        jobs.Sort((left, right) => right.UpdatedAtUtc.CompareTo(left.UpdatedAtUtc));
        var modelIds = jobs.Select(item => item.ModelConfigurationId!.Value).Distinct().ToArray();
        var models = new Dictionary<Guid, ModelConfigEntity>();
        foreach (var batch in GuidBatchQuery.CreateBatches(modelIds))
        {
            var modelPredicate = GuidBatchQuery.BuildPredicate<ModelConfigEntity>(batch, item => item.Id);
            foreach (var model in await database.ModelConfigs.AsNoTracking()
                         .Where(modelPredicate)
                         .ToArrayAsync(token))
                models.TryAdd(model.Id, model);
        }
        var configuredDefault = await database.ModelConfigs.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ConfigurationType == "embedding" && item.IsDefault, token);
        if (configuredDefault is not null) models.TryAdd(configuredDefault.Id, configuredDefault);
        var defaultModel = models.Values.SingleOrDefault(item =>
            item.ConfigurationType == "embedding" && item.IsDefault);

        var mappings = new List<ActiveVectorMapping>(legacyDocuments.Length);
        foreach (var document in legacyDocuments)
        {
            var versionId = document.ActiveVersionId!.Value;
            if (!versions.TryGetValue(versionId, out var version))
                throw new InvalidOperationException($"Active version is missing for document {document.Id:D}.");
            if (string.IsNullOrWhiteSpace(version.IndexCollectionName))
                throw new InvalidOperationException($"Active version has no indexed collection mapping: {versionId:D}.");
            var model = jobs.FirstOrDefault(item =>
                    item.KnowledgeDocumentVersionId == versionId
                    && item.ModelConfigurationId is { } id
                    && models.ContainsKey(id)) is { ModelConfigurationId: { } jobModelId }
                ? models[jobModelId]
                : defaultModel ?? throw new InvalidOperationException(
                    "An active legacy version has no completed model provenance and no default embedding model.");
            var dimension = document.ActiveEmbeddingDimension!.Value;
            if (model.EmbeddingDimension != dimension)
                throw new InvalidOperationException(
                    $"Embedding model dimension does not match active version {versionId:D}.");
            mappings.Add(new(
                document.Id,
                versionId,
                document.ActiveCollectionName!,
                document.ActiveCollectionExclusive,
                dimension,
                ParseDistance(document.ActiveDistance!),
                document.ActiveIndexGeneration ?? version.IndexGeneration ?? 1,
                model.Provider,
                model.BaseUrl,
                model.Model));
        }

        var plan = planner.Build(mappings);
        var checkpoint = new MigrationCheckpoint();
        var mismatches = 0;
        var documentMappings = legacyDocuments.ToDictionary(item => item.Id);
        var verifiedVersions = new VersionMigrationCheckpoint?[plan.Versions.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, plan.Versions.Count),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token },
            async (index, cancellationToken) =>
        {
            var planned = plan.Versions[index];
            var source = new VectorCollection(
                planned.Source.SourceCollection,
                planned.Source.Dimension,
                planned.Source.Distance);
            var inspectedCollection = await WithTransientRetryAsync(
                () => vectors.InspectCollectionAsync(source.Name, cancellationToken),
                cancellationToken);
            if (inspectedCollection != source)
            {
                Interlocked.Increment(ref mismatches);
                return;
            }
            var metadata = await WithTransientRetryAsync(
                () => vectors.InspectVersionAsync(source, planned.Source.VersionId, cancellationToken),
                cancellationToken);
            var metadataMatches = metadata.Count > 0 && metadata.All(item =>
                item.DocumentId == planned.Source.DocumentId
                && item.VersionId == planned.Source.VersionId
                && item.Active
                && item.Generation == planned.Source.Generation);
            if (!metadataMatches) Interlocked.Increment(ref mismatches);
            var version = versions[planned.Source.VersionId];
            verifiedVersions[index] = new()
            {
                DocumentId = planned.Source.DocumentId,
                VersionId = planned.Source.VersionId,
                SourceCollection = planned.Source.SourceCollection,
                SourceDocumentCollectionExclusive = planned.Source.SourceCollectionExclusive,
                SourceDocumentEmbeddingContractKey = documentMappings[planned.Source.DocumentId].ActiveEmbeddingContractKey,
                SourceVersionCollection = version.IndexCollectionName!,
                SourceVersionCollectionExclusive = version.IndexCollectionExclusive,
                SourceVersionEmbeddingContractKey = version.IndexEmbeddingContractKey,
                DestinationCollection = planned.Contract.CollectionName,
                DestinationContractKey = planned.Contract.Key,
                Dimension = planned.Contract.Dimension,
                Distance = DistanceValue(planned.Contract.Distance),
                Generation = planned.Source.Generation,
                ExpectedPointCount = metadata.Count,
                ExpectedMetadataHash = MetadataHash(metadata)
            };
        });
        checkpoint.Versions.AddRange(verifiedVersions.OfType<VersionMigrationCheckpoint>());
        checkpoint.State = mismatches == 0 ? "Planned" : "Blocked";
        await MigrationCheckpointStore.SaveAsync(checkpointPath, checkpoint, token);
        return Summarize(checkpoint, mismatches);
    }

    public async Task<MigrationSummary> ApplyAsync(MigrationCheckpoint checkpoint, CancellationToken token)
    {
        if (checkpoint.State == "Blocked")
            throw new InvalidOperationException("The dry run is blocked by source mismatches.");
        await EnsureNoActiveIndexWorkAsync(token);
        foreach (var item in checkpoint.Versions)
        {
            if (item.Stage == MigrationStage.Planned)
                await CopyAsync(checkpoint, item, token);
            if (item.Stage == MigrationStage.Copied)
                await VerifyCopiedAsync(checkpoint, item, token);
        }
        if (!planner.CanSwitch(checkpoint.Versions.Select(item => new VersionVerification(
                item.ExpectedPointCount,
                item.ExpectedPointCount,
                item.Stage >= MigrationStage.Verified)).ToArray()))
            throw new InvalidOperationException("Not every version passed destination verification.");

        await using var transaction = await database.Database.BeginTransactionAsync(token);
        foreach (var item in checkpoint.Versions)
        {
            if (item.Stage >= MigrationStage.Switched) continue;
            var document = await database.KnowledgeDocuments.SingleAsync(value => value.Id == item.DocumentId, token);
            var version = await database.KnowledgeDocumentVersions.SingleAsync(value => value.Id == item.VersionId, token);
            if (document.ActiveVersionId != item.VersionId)
                throw new InvalidOperationException("An active document version changed after the dry run.");
            if (document.ActiveCollectionName == item.DestinationCollection
                && version.IndexCollectionName == item.DestinationCollection)
            {
                item.Stage = MigrationStage.Switched;
                continue;
            }
            if (document.ActiveCollectionName != item.SourceCollection
                || version.IndexCollectionName != item.SourceVersionCollection)
                throw new InvalidOperationException("An active collection mapping changed after the dry run.");
            document.ActiveCollectionName = item.DestinationCollection;
            document.ActiveEmbeddingContractKey = item.DestinationContractKey;
            document.ActiveCollectionExclusive = false;
            document.StateVersion++;
            document.UpdatedAtUtc = DateTime.UtcNow;
            version.IndexCollectionName = item.DestinationCollection;
            version.IndexEmbeddingContractKey = item.DestinationContractKey;
            version.IndexCollectionExclusive = false;
            version.UpdatedAtUtc = DateTime.UtcNow;
            item.Stage = MigrationStage.Switched;
        }
        await database.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        checkpoint.State = "Switched";
        await MigrationCheckpointStore.SaveAsync(checkpointPath, checkpoint, token);
        return Summarize(checkpoint, 0);
    }

    public async Task<MigrationSummary> VerifyAsync(MigrationCheckpoint checkpoint, CancellationToken token)
    {
        var mismatches = 0;
        foreach (var item in checkpoint.Versions)
        {
            var destination = Collection(item.DestinationCollection, item.Dimension, item.Distance);
            var metadata = await vectors.InspectVersionAsync(destination, item.VersionId, token);
            var document = await database.KnowledgeDocuments.AsNoTracking()
                .SingleAsync(value => value.Id == item.DocumentId, token);
            var version = await database.KnowledgeDocumentVersions.AsNoTracking()
                .SingleAsync(value => value.Id == item.VersionId, token);
            var matches = metadata.Count == item.ExpectedPointCount
                && MetadataHash(metadata) == item.ExpectedMetadataHash
                && document.ActiveVersionId == item.VersionId
                && document.ActiveCollectionName == item.DestinationCollection
                && document.ActiveEmbeddingContractKey == item.DestinationContractKey
                && !document.ActiveCollectionExclusive
                && version.IndexCollectionName == item.DestinationCollection
                && version.IndexEmbeddingContractKey == item.DestinationContractKey
                && !version.IndexCollectionExclusive;
            if (!matches) mismatches++;
            else item.Stage = MigrationStage.Accepted;
        }
        checkpoint.State = mismatches == 0 ? "Accepted" : "VerificationFailed";
        await MigrationCheckpointStore.SaveAsync(checkpointPath, checkpoint, token);
        return Summarize(checkpoint, mismatches);
    }

    public async Task<MigrationSummary> RollbackAsync(MigrationCheckpoint checkpoint, CancellationToken token)
    {
        await EnsureNoActiveIndexWorkAsync(token);
        foreach (var item in checkpoint.Versions)
        {
            if (item.Stage is not (MigrationStage.Switched or MigrationStage.Accepted)) continue;
            var source = Collection(item.SourceCollection, item.Dimension, item.Distance);
            var metadata = await vectors.InspectVersionAsync(source, item.VersionId, token);
            if (metadata.Count != item.ExpectedPointCount || MetadataHash(metadata) != item.ExpectedMetadataHash)
                throw new InvalidOperationException("Rollback source verification failed; database mappings were not changed.");
        }
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        foreach (var item in checkpoint.Versions)
        {
            if (item.Stage is not (MigrationStage.Switched or MigrationStage.Accepted)) continue;
            var document = await database.KnowledgeDocuments.SingleAsync(value => value.Id == item.DocumentId, token);
            var version = await database.KnowledgeDocumentVersions.SingleAsync(value => value.Id == item.VersionId, token);
            if (document.ActiveVersionId != item.VersionId
                || document.ActiveCollectionName != item.DestinationCollection
                || version.IndexCollectionName != item.DestinationCollection)
                throw new InvalidOperationException("Rollback mapping guard failed; database mappings were not changed.");
            document.ActiveCollectionName = item.SourceCollection;
            document.ActiveEmbeddingContractKey = item.SourceDocumentEmbeddingContractKey;
            document.ActiveCollectionExclusive = item.SourceDocumentCollectionExclusive;
            document.StateVersion++;
            document.UpdatedAtUtc = DateTime.UtcNow;
            version.IndexCollectionName = item.SourceVersionCollection;
            version.IndexEmbeddingContractKey = item.SourceVersionEmbeddingContractKey;
            version.IndexCollectionExclusive = item.SourceVersionCollectionExclusive;
            version.UpdatedAtUtc = DateTime.UtcNow;
            item.Stage = MigrationStage.RolledBack;
        }
        await database.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        checkpoint.State = "RolledBack";
        await MigrationCheckpointStore.SaveAsync(checkpointPath, checkpoint, token);
        return Summarize(checkpoint, 0);
    }

    private async Task CopyAsync(MigrationCheckpoint checkpoint, VersionMigrationCheckpoint item, CancellationToken token)
    {
        var source = Collection(item.SourceCollection, item.Dimension, item.Distance);
        var destination = Collection(item.DestinationCollection, item.Dimension, item.Distance);
        await vectors.EnsureCollectionAsync(destination, token);
        await vectors.EnsurePayloadIndexesAsync(destination, token);
        var copiedMetadata = new List<VectorPointMetadata>(item.ExpectedPointCount);
        string? offset = null;
        do
        {
            var page = await vectors.ReadVersionPointsAsync(source, item.VersionId, offset, 256, token);
            if (page.Points.Any(point => point.DocumentId != item.DocumentId || point.VersionId != item.VersionId))
                throw new InvalidOperationException("Source vector metadata changed during copy.");
            await vectors.UpsertAsync(destination, page.Points, token);
            copiedMetadata.AddRange(page.Points.Select(ToMetadata));
            offset = page.NextOffset;
        } while (offset is not null);
        if (copiedMetadata.Count != item.ExpectedPointCount
            || MetadataHash(copiedMetadata) != item.ExpectedMetadataHash)
            throw new InvalidOperationException("Source vectors changed after the dry run.");
        item.Stage = MigrationStage.Copied;
        checkpoint.State = "Copied";
        await MigrationCheckpointStore.SaveAsync(checkpointPath, checkpoint, token);
    }

    private async Task VerifyCopiedAsync(MigrationCheckpoint checkpoint, VersionMigrationCheckpoint item, CancellationToken token)
    {
        var destination = Collection(item.DestinationCollection, item.Dimension, item.Distance);
        var metadata = await vectors.InspectVersionAsync(destination, item.VersionId, token);
        if (metadata.Count != item.ExpectedPointCount || MetadataHash(metadata) != item.ExpectedMetadataHash)
            throw new InvalidOperationException("Destination vector verification failed.");
        item.Stage = MigrationStage.Verified;
        checkpoint.State = "Verified";
        await MigrationCheckpointStore.SaveAsync(checkpointPath, checkpoint, token);
    }

    private async Task EnsureNoActiveIndexWorkAsync(CancellationToken token)
    {
        var active = await database.KnowledgeIndexJobs.AsNoTracking().AnyAsync(item =>
            (item.Operation == "index" || item.Operation == "reindex")
            && (item.Status == "pending" || item.Status == "leased" || item.Status == "staged" || item.Status == "activating"), token);
        if (active) throw new InvalidOperationException("Active knowledge index work exists; keep the Worker stopped and drain jobs before mutation.");
    }

    private static MigrationSummary Summarize(MigrationCheckpoint checkpoint, int mismatches) => new(
        checkpoint.Versions.Count,
        checkpoint.Versions.Select(item => item.SourceCollection).Distinct(StringComparer.Ordinal).Count(),
        checkpoint.Versions.Select(item => item.DestinationCollection).Distinct(StringComparer.Ordinal).Count(),
        checkpoint.Versions.Sum(item => item.ExpectedPointCount),
        mismatches,
        checkpoint.State);

    private static async Task<T> WithTransientRetryAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (VectorStoreUnavailableException) when (attempt < 4)
            {
                await Task.Delay(TimeSpan.FromSeconds(1 << (attempt - 1)), cancellationToken);
            }
        }
    }

    private static VectorCollection Collection(string name, int dimension, string distance) =>
        new(name, dimension, ParseDistance(distance));

    private static VectorPointMetadata ToMetadata(VectorPoint point) =>
        new(point.Id, point.DocumentId, point.VersionId, point.TagIds, point.Active, point.Generation);

    private static string MetadataHash(IEnumerable<VectorPointMetadata> metadata)
    {
        var canonical = string.Join('\n', metadata.OrderBy(item => item.ChunkId).Select(item =>
            $"{item.ChunkId:D}|{item.DocumentId:D}|{item.VersionId:D}|{string.Join(',', item.TagIds.Order().Select(tag => tag.ToString("D")))}|{item.Active}|{item.Generation}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static VectorDistance ParseDistance(string value) => value.ToLowerInvariant() switch
    {
        "cosine" => VectorDistance.Cosine,
        "dot" => VectorDistance.Dot,
        "euclid" => VectorDistance.Euclid,
        _ => throw new InvalidOperationException("Unsupported vector distance in active knowledge mapping.")
    };

    private static string DistanceValue(VectorDistance value) => value.ToString().ToLowerInvariant();
}
