using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Knowledge;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.Infrastructure.Knowledge;

public sealed class KnowledgeDocumentAdministrationQuery(WechatRobotDbContext database)
{
    public Task<KnowledgeDocumentPage> ListAsync(
        string? query,
        string? status,
        int page,
        int pageSize,
        CancellationToken cancellationToken) =>
        ListAsync(
            query,
            status,
            sourceKind: null,
            tagId: null,
            page,
            pageSize,
            cancellationToken);

    public async Task<KnowledgeDocumentPage> ListAsync(
        string? query,
        string? status,
        string? sourceKind,
        Guid? tagId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var documents = database.KnowledgeDocuments.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var normalized = query.Trim().ToUpperInvariant();
            documents = documents.Where(document =>
                document.Title.ToUpper().Contains(normalized));
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            var exactStatus = status.Trim();
            documents = documents.Where(document => document.Status == exactStatus);
        }

        var effectiveVersions =
            from document in database.KnowledgeDocuments.AsNoTracking()
            from version in database.KnowledgeDocumentVersions.AsNoTracking()
            where version.KnowledgeDocumentId == document.Id &&
                  (document.ActiveVersionId == version.Id ||
                   (document.ActiveVersionId == null &&
                    !database.KnowledgeDocumentVersions.Any(candidate =>
                        candidate.KnowledgeDocumentId == document.Id &&
                        candidate.Version > version.Version)))
            select new
            {
                DocumentId = document.Id,
                VersionId = version.Id,
                version.SourceKind
            };

        if (!string.IsNullOrWhiteSpace(sourceKind))
        {
            var exactSourceKind = sourceKind.Trim();
            documents = documents.Where(document =>
                effectiveVersions.Any(version =>
                    version.DocumentId == document.Id &&
                    version.SourceKind == exactSourceKind));
        }

        if (tagId.HasValue)
        {
            var exactTagId = tagId.Value;
            var taggedVersionIds =
                from chunk in database.KnowledgeChunks.AsNoTracking()
                join binding in database.KnowledgeChunkTags.AsNoTracking()
                    on chunk.Id equals binding.KnowledgeChunkId
                where binding.KnowledgeTagId == exactTagId
                select chunk.KnowledgeDocumentVersionId;
            documents = documents.Where(document =>
                effectiveVersions.Any(version =>
                    version.DocumentId == document.Id &&
                    taggedVersionIds.Contains(version.VersionId)));
        }

        var total = await documents.CountAsync(cancellationToken);
        var rows = await documents
            .OrderByDescending(document => document.UpdatedAtUtc)
            .ThenBy(document => document.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(document => new DocumentRow(
                document.Id,
                document.Title,
                document.Status,
                document.StateVersion,
                document.ActiveVersionId,
                document.IsDeleteRequested,
                document.CreatedAtUtc,
                document.UpdatedAtUtc))
            .ToArrayAsync(cancellationToken);
        var summaries = await BuildSummariesAsync(rows, cancellationToken);
        return new(summaries, total, page, pageSize);
    }

    public async Task<KnowledgeDocumentDetail?> GetAsync(
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var document = await database.KnowledgeDocuments.AsNoTracking()
            .Where(item => item.Id == documentId)
            .Select(item => new DocumentRow(
                item.Id,
                item.Title,
                item.Status,
                item.StateVersion,
                item.ActiveVersionId,
                item.IsDeleteRequested,
                item.CreatedAtUtc,
                item.UpdatedAtUtc))
            .SingleOrDefaultAsync(cancellationToken);
        if (document is null)
        {
            return null;
        }

        var versions = await database.KnowledgeDocumentVersions.AsNoTracking()
            .Where(version => version.KnowledgeDocumentId == documentId)
            .OrderByDescending(version => version.Version)
            .ThenBy(version => version.Id)
            .Select(version => new VersionDetailRow(
                version.Id,
                version.Version,
                version.OriginalFileName,
                version.SafeFileName,
                version.ContentType,
                version.SizeBytes,
                version.Status,
                version.FailureReason,
                version.IsPublished,
                version.PublicUrl != null && version.PublicUrl != "",
                version.PreviewRevision,
                version.SourceKind,
                version.SourceActorDisplayName,
                version.SourceBatchId,
                version.ChangeKind,
                version.SupersedesVersionId,
                version.CreatedAtUtc,
                version.UpdatedAtUtc))
            .ToArrayAsync(cancellationToken);
        var summary = AssertSingle(await BuildSummariesAsync([document], cancellationToken));
        var versionIds = versions.Select(version => version.Id).ToArray();
        if (versionIds.Length == 0)
        {
            return new(summary, []);
        }

        var tagsByVersion = await LoadVersionTagsAsync(versionIds, cancellationToken);
        var previewCounts = await CountByVersionAsync(
            versionIds,
            batch => database.KnowledgeChunkPreviews.AsNoTracking()
                .Where(GuidBatchQuery.BuildPredicate<KnowledgeChunkPreviewEntity>(
                    batch,
                    item => item.KnowledgeDocumentVersionId)),
            cancellationToken);
        var approvedChunkCounts = await CountByVersionAsync(
            versionIds,
            batch => database.KnowledgeChunks.AsNoTracking()
                .Where(GuidBatchQuery.BuildPredicate<KnowledgeChunkEntity>(
                    batch,
                    item => item.KnowledgeDocumentVersionId))
                .Where(item => item.Status == "approved"),
            cancellationToken);
        var ocrRows = await LoadBatchedAsync(
            versionIds,
            batch => database.KnowledgeOcrPages.AsNoTracking()
                .Where(GuidBatchQuery.BuildPredicate<KnowledgeOcrPageEntity>(
                    batch,
                    item => item.KnowledgeDocumentVersionId))
                .Select(item => new OcrRow(item.KnowledgeDocumentVersionId, item.Status))
                .ToArrayAsync(cancellationToken));
        var ocrCounts = ocrRows
            .GroupBy(item => item.VersionId)
            .ToDictionary(group => group.Key, group => group.Count());
        var ocrFailedCounts = ocrRows
            .Where(item => item.Status == "failed")
            .GroupBy(item => item.VersionId)
            .ToDictionary(group => group.Key, group => group.Count());

        var durableRows = await database.DurableJobs.AsNoTracking()
            .Where(job =>
                (job.JobType == "UploadKnowledgeDocument" ||
                 job.JobType == "ParseKnowledgeDocument") &&
                job.PayloadJson.Contains(documentId.ToString()))
            .Select(job => new DurableJobRow(
                job.Id,
                job.JobType,
                job.Status,
                job.AttemptCount,
                job.PayloadJson,
                job.CreatedAtUtc,
                job.UpdatedAtUtc))
            .ToArrayAsync(cancellationToken);
        var durableByVersion = durableRows
            .Select(row => (Row: row, Payload: TryReadPayload(row.PayloadJson)))
            .Where(item =>
                item.Payload is not null &&
                item.Payload.DocumentId == documentId &&
                versionIds.Contains(item.Payload.VersionId))
            .GroupBy(item => item.Payload!.VersionId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<KnowledgeDocumentJobSummary>)group
                    .OrderBy(item => item.Row.CreatedAtUtc)
                    .ThenBy(item => item.Row.Id)
                    .Select(item => new KnowledgeDocumentJobSummary(
                        item.Row.Id,
                        item.Row.JobType,
                        item.Row.Status,
                        item.Row.AttemptCount,
                        item.Row.CreatedAtUtc,
                        item.Row.UpdatedAtUtc))
                    .ToArray());

        var indexRows = await LoadBatchedAsync(
            versionIds,
            batch => database.KnowledgeIndexJobs.AsNoTracking()
                .Where(GuidBatchQuery.BuildPredicate<KnowledgeIndexJobEntity>(
                    batch,
                    item => item.KnowledgeDocumentVersionId))
                .Select(item => new IndexJobRow(
                    item.Id,
                    item.KnowledgeDocumentVersionId,
                    item.Operation,
                    item.Status,
                    item.AttemptCount,
                    item.FailureReason != null && item.FailureReason != "",
                    item.CreatedAtUtc,
                    item.UpdatedAtUtc))
                .ToArrayAsync(cancellationToken));
        var indexByVersion = indexRows
            .GroupBy(item => item.VersionId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<KnowledgeDocumentIndexJobSummary>)group
                    .OrderByDescending(item => item.UpdatedAtUtc)
                    .ThenBy(item => item.Id)
                    .Select(item => new KnowledgeDocumentIndexJobSummary(
                        item.Id,
                        item.Operation,
                        item.Status,
                        item.AttemptCount,
                        item.HasFailure,
                        item.CreatedAtUtc,
                        item.UpdatedAtUtc))
                    .ToArray());

        var details = versions.Select(version => new KnowledgeDocumentVersionSummary(
            version.Id,
            version.Version,
            version.OriginalFileName,
            version.SafeFileName,
            version.ContentType,
            version.SizeBytes,
            version.Status,
            SanitizeFailure(version.FailureReason),
            version.IsPublished,
            version.HasPublicObject,
            version.PreviewRevision,
            previewCounts.GetValueOrDefault(version.Id),
            approvedChunkCounts.GetValueOrDefault(version.Id),
            ocrCounts.GetValueOrDefault(version.Id),
            ocrFailedCounts.GetValueOrDefault(version.Id),
            version.SourceKind,
            version.SourceActorDisplayName,
            version.SourceBatchId,
            version.ChangeKind,
            version.SupersedesVersionId,
            tagsByVersion.GetValueOrDefault(version.Id) ?? [],
            durableByVersion.GetValueOrDefault(version.Id) ?? [],
            indexByVersion.GetValueOrDefault(version.Id) ?? [],
            version.CreatedAtUtc,
            version.UpdatedAtUtc)).ToArray();
        return new(summary, details);
    }

    private async Task<KnowledgeDocumentSummary[]> BuildSummariesAsync(
        IReadOnlyList<DocumentRow> documents,
        CancellationToken cancellationToken)
    {
        if (documents.Count == 0)
        {
            return [];
        }

        var documentIds = documents.Select(document => document.Id).ToArray();
        var versions = await LoadBatchedAsync(
            documentIds,
            batch => database.KnowledgeDocumentVersions.AsNoTracking()
                .Where(GuidBatchQuery.BuildPredicate<KnowledgeDocumentVersionEntity>(
                    batch,
                    version => version.KnowledgeDocumentId))
                .Select(version => new VersionRow(
                    version.Id,
                    version.KnowledgeDocumentId,
                    version.Version,
                    version.Status,
                    version.FailureReason,
                    version.StagedContent.Length > 0,
                    version.SourceKind,
                    version.SourceActorDisplayName))
                .ToArrayAsync(cancellationToken));
        var grouped = versions
            .GroupBy(version => version.DocumentId)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(version => version.Version)
                    .ThenBy(version => version.Id)
                    .ToArray());
        var effectiveVersionByDocument = documents.ToDictionary(
            document => document.Id,
            document =>
            {
                var documentVersions = grouped.GetValueOrDefault(document.Id) ?? [];
                return document.ActiveVersionId.HasValue
                    ? documentVersions.FirstOrDefault(version =>
                        version.Id == document.ActiveVersionId.Value)
                    : documentVersions.FirstOrDefault();
            });
        var effectiveVersionIds = effectiveVersionByDocument.Values
            .Where(version => version is not null)
            .Select(version => version!.Id)
            .Distinct()
            .ToArray();
        var tagsByVersion = await LoadVersionTagsAsync(
            effectiveVersionIds,
            cancellationToken);
        var cleanupJobIds = documents
            .Where(document => document.IsDeleteRequested)
            .ToDictionary(
                document => document.Id,
                document => KnowledgeDocumentCleanupJobIdentity.Create(
                    document.Id));
        var cleanupJobIdValues = cleanupJobIds.Values.ToArray();
        var cleanupStatuses = cleanupJobIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await database.DurableJobs
                .AsNoTracking()
                .Where(job =>
                    cleanupJobIdValues.Contains(job.Id) &&
                    job.JobType == "CleanupKnowledgeDocument")
                .ToDictionaryAsync(
                    job => job.Id,
                    job => job.Status,
                    cancellationToken);
        return documents.Select(document =>
        {
            var documentVersions = grouped.GetValueOrDefault(document.Id) ?? [];
            var latest = documentVersions.FirstOrDefault();
            var effective = effectiveVersionByDocument.GetValueOrDefault(document.Id);
            return new KnowledgeDocumentSummary(
                document.Id,
                document.Title,
                document.Status,
                document.StateVersion,
                document.ActiveVersionId,
                documentVersions.Length,
                latest?.Id,
                latest?.Version,
                latest?.Status,
                SanitizeFailure(latest?.FailureReason),
                !document.IsDeleteRequested &&
                document.Status != "disabled" &&
                latest is { Status: "failed", HasStagedContent: true },
                document.IsDeleteRequested,
                cleanupJobIds.TryGetValue(document.Id, out var cleanupJobId) &&
                cleanupStatuses.TryGetValue(cleanupJobId, out var cleanupStatus) &&
                cleanupStatus is "deadLetter" or "cancelled",
                effective?.SourceKind ?? "LegacyUnknown",
                effective?.SourceActorDisplayName,
                effective is null
                    ? []
                    : tagsByVersion.GetValueOrDefault(effective.Id) ?? [],
                document.CreatedAtUtc,
                document.UpdatedAtUtc);
        }).ToArray();
    }

    private async Task<Dictionary<Guid, IReadOnlyList<KnowledgeDocumentTagSummary>>>
        LoadVersionTagsAsync(
            IReadOnlyCollection<Guid> versionIds,
            CancellationToken cancellationToken)
    {
        if (versionIds.Count == 0)
        {
            return [];
        }

        var rows = await LoadBatchedAsync(
            versionIds,
            batch => database.KnowledgeChunks.AsNoTracking()
                .Where(GuidBatchQuery.BuildPredicate<KnowledgeChunkEntity>(
                    batch,
                    chunk => chunk.KnowledgeDocumentVersionId))
                .Join(
                    database.KnowledgeChunkTags.AsNoTracking(),
                    chunk => chunk.Id,
                    binding => binding.KnowledgeChunkId,
                    (chunk, binding) => new
                    {
                        chunk.KnowledgeDocumentVersionId,
                        binding.KnowledgeTagId
                    })
                .Join(
                    database.KnowledgeTags.AsNoTracking(),
                    binding => binding.KnowledgeTagId,
                    tag => tag.Id,
                    (binding, tag) => new VersionTagRow(
                        binding.KnowledgeDocumentVersionId,
                        tag.Id,
                        tag.Name))
                .Distinct()
                .ToArrayAsync(cancellationToken));
        return rows
            .GroupBy(row => row.VersionId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<KnowledgeDocumentTagSummary>)group
                    .OrderBy(row => row.Name, StringComparer.Ordinal)
                    .ThenBy(row => row.TagId)
                    .Select(row => new KnowledgeDocumentTagSummary(row.TagId, row.Name))
                    .ToArray());
    }

    private async Task<Dictionary<Guid, int>> CountByVersionAsync<TEntity>(
        IReadOnlyCollection<Guid> versionIds,
        Func<IReadOnlyCollection<Guid>, IQueryable<TEntity>> query,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        var result = new Dictionary<Guid, int>();
        foreach (var batch in GuidBatchQuery.CreateBatches(versionIds))
        {
            var keyProperty = typeof(TEntity).GetProperty("KnowledgeDocumentVersionId")
                ?? throw new InvalidOperationException($"{typeof(TEntity).Name} has no version key.");
            var rows = await query(batch).ToArrayAsync(cancellationToken);
            foreach (var group in rows.GroupBy(row => (Guid)keyProperty.GetValue(row)!))
            {
                result[group.Key] = group.Count();
            }
        }
        return result;
    }

    private static async Task<T[]> LoadBatchedAsync<T>(
        IReadOnlyCollection<Guid> ids,
        Func<IReadOnlyCollection<Guid>, Task<T[]>> load)
    {
        var result = new List<T>();
        foreach (var batch in GuidBatchQuery.CreateBatches(ids))
        {
            result.AddRange(await load(batch));
        }
        return result.ToArray();
    }

    private static DocumentJobPayload? TryReadPayload(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !TryGetGuid(document.RootElement, "documentId", out var documentId) ||
                !TryGetGuid(document.RootElement, "versionId", out var versionId))
            {
                return null;
            }
            return new(documentId, versionId);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGetGuid(JsonElement element, string name, out Guid value)
    {
        value = Guid.Empty;
        return element.TryGetProperty(name, out var property) &&
               property.ValueKind == JsonValueKind.String &&
               Guid.TryParse(property.GetString(), out value);
    }

    private static string? SanitizeFailure(string? failure)
    {
        if (string.IsNullOrWhiteSpace(failure))
        {
            return null;
        }
        return failure.StartsWith(
            "Object storage upload failed",
            StringComparison.OrdinalIgnoreCase)
            ? "Object storage upload failed; retry is available."
            : "Document processing failed.";
    }

    private static T AssertSingle<T>(IReadOnlyList<T> values) =>
        values.Count == 1
            ? values[0]
            : throw new InvalidOperationException("Expected one document summary.");

    private sealed record DocumentRow(
        Guid Id,
        string Title,
        string Status,
        int StateVersion,
        Guid? ActiveVersionId,
        bool IsDeleteRequested,
        DateTime CreatedAtUtc,
        DateTime UpdatedAtUtc);

    private sealed record VersionRow(
        Guid Id,
        Guid DocumentId,
        int Version,
        string Status,
        string? FailureReason,
        bool HasStagedContent,
        string SourceKind,
        string? SourceActorDisplayName);

    private sealed record VersionDetailRow(
        Guid Id,
        int Version,
        string OriginalFileName,
        string SafeFileName,
        string ContentType,
        long SizeBytes,
        string Status,
        string? FailureReason,
        bool IsPublished,
        bool HasPublicObject,
        int PreviewRevision,
        string SourceKind,
        string? SourceActorDisplayName,
        Guid? SourceBatchId,
        string ChangeKind,
        Guid? SupersedesVersionId,
        DateTime CreatedAtUtc,
        DateTime UpdatedAtUtc);

    private sealed record DurableJobRow(
        Guid Id,
        string JobType,
        string Status,
        int AttemptCount,
        string PayloadJson,
        DateTime CreatedAtUtc,
        DateTime UpdatedAtUtc);

    private sealed record IndexJobRow(
        Guid Id,
        Guid VersionId,
        string Operation,
        string Status,
        int AttemptCount,
        bool HasFailure,
        DateTime CreatedAtUtc,
        DateTime UpdatedAtUtc);

    private sealed record OcrRow(Guid VersionId, string Status);

    private sealed record VersionTagRow(
        Guid VersionId,
        Guid TagId,
        string Name);
    private sealed record DocumentJobPayload(Guid DocumentId, Guid VersionId);
}
