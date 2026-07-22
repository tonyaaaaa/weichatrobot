using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;
using WechatRobot.Application.Knowledge;
using WechatRobot.Application.Storage;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.Infrastructure.Persistence;

public sealed class KnowledgeDocumentStore(WechatRobotDbContext database) : IKnowledgeDocumentStore
{
    public async Task<PendingDocumentUpload?> StageAsync(DocumentStageRequest request, CancellationToken cancellationToken)
    {
        if (await database.KnowledgeDocumentVersions.AnyAsync(item => item.Sha256 == request.Document.Sha256, cancellationToken)) return null;
        var documentId = request.DocumentId ?? Guid.NewGuid();
        var document = await database.KnowledgeDocuments.SingleOrDefaultAsync(item => item.Id == documentId, cancellationToken);
        if (document is null)
        {
            document = new KnowledgeDocumentEntity { Id = documentId, Title = request.DisplayName };
            database.KnowledgeDocuments.Add(document);
        }
        else if (document.IsDeleteRequested) throw new InvalidOperationException("A deleted document cannot receive a new version.");

        var versionNumber = await database.KnowledgeDocumentVersions.Where(item => item.KnowledgeDocumentId == documentId)
            .Select(item => (int?)item.Version).MaxAsync(cancellationToken) is { } current ? current + 1 : 1;
        var version = new KnowledgeDocumentVersionEntity
        {
            KnowledgeDocumentId = documentId, Version = versionNumber, OriginalFileName = request.DisplayName,
            SafeFileName = request.Document.SafeFileName, ContentType = request.Document.ContentType, Sha256 = request.Document.Sha256,
            SizeBytes = request.Document.Content.LongLength,
            ObjectKey = $"wechatrobot/knowledge/{documentId:N}/{versionNumber}/source/{request.Document.SafeFileName}",
            StagedContent = request.Document.Content
        };
        database.KnowledgeDocumentVersions.Add(version);
        database.DurableJobs.AddRange(NewJob("UploadKnowledgeDocument", documentId, version.Id), NewJob("ParseKnowledgeDocument", documentId, version.Id, "blocked"));

        await using var transaction = await BeginTransactionIfRelationalAsync(cancellationToken);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is MySqlException { Number: 1062 })
        {
            if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None);
            database.ChangeTracker.Clear();
            return null;
        }
        return ToPending(version);
    }

    public async Task<PendingDocumentUpload?> GetRetryableAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var document = await database.KnowledgeDocuments.AsNoTracking().SingleOrDefaultAsync(item => item.Id == documentId, cancellationToken);
        if (document is null) return null;
        if (document.IsDeleteRequested || document.Status == "disabled") throw new DocumentDeletedException();
        var version = await database.KnowledgeDocumentVersions.AsNoTracking()
            .Where(item => item.KnowledgeDocumentId == documentId && item.Status == "failed")
            .OrderByDescending(item => item.Version).FirstOrDefaultAsync(cancellationToken);
        return version is null || version.StagedContent.Length == 0 ? null : ToPending(version);
    }

    public async Task<PendingDocumentUpload?> GetRecoverableAsync(Guid versionId, CancellationToken cancellationToken)
    {
        var version = await database.KnowledgeDocumentVersions.AsNoTracking().SingleOrDefaultAsync(item => item.Id == versionId, cancellationToken);
        if (version is null || version.Status == "uploaded" || version.StagedContent.Length == 0) return null;
        var document = await database.KnowledgeDocuments.AsNoTracking().SingleAsync(item => item.Id == version.KnowledgeDocumentId, cancellationToken);
        return document.IsDeleteRequested || document.Status == "disabled" || version.Status == "disabled" ? null : ToPending(version);
    }

    public async Task<bool> MarkUploadedAsync(PendingDocumentUpload upload, StoredObject stored, CancellationToken cancellationToken)
    {
        database.ChangeTracker.Clear();
        await using var transaction = await BeginTransactionIfRelationalAsync(cancellationToken);
        var document = await database.KnowledgeDocuments.SingleAsync(item => item.Id == upload.DocumentId, cancellationToken);
        var version = await database.KnowledgeDocumentVersions.SingleAsync(item => item.Id == upload.VersionId, cancellationToken);
        var job = await UploadJobAsync(upload.VersionId, cancellationToken);
        if (document.IsDeleteRequested || document.Status == "disabled" || version.Status == "disabled" || job.Status == "cancelled")
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            return false;
        }
        if (version.Status == "uploaded")
        {
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return true;
        }
        version.PublicUrl = stored.PublicUrl.AbsoluteUri;
        version.Status = "uploaded";
        version.FailureReason = null;
        version.StagedContent = [];
        version.UpdatedAtUtc = DateTime.UtcNow;
        job.Status = "completed";
        job.CompletedAtUtc = DateTime.UtcNow;
        job.UpdatedAtUtc = DateTime.UtcNow;
        document.Status = "uploaded";
        document.UpdatedAtUtc = DateTime.UtcNow;
        var parseJob = await database.DurableJobs.SingleAsync(item => item.JobType == "ParseKnowledgeDocument" && item.PayloadJson.Contains(upload.VersionId.ToString()), cancellationToken);
        if (parseJob.Status == "blocked")
        {
            parseJob.Status = "pending";
            parseJob.NextAttemptAtUtc = DateTime.UtcNow;
            parseJob.UpdatedAtUtc = DateTime.UtcNow;
        }
        await database.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task MarkFailedAsync(PendingDocumentUpload upload, CancellationToken cancellationToken)
    {
        database.ChangeTracker.Clear();
        var document = await database.KnowledgeDocuments.SingleAsync(item => item.Id == upload.DocumentId, cancellationToken);
        var version = await database.KnowledgeDocumentVersions.SingleAsync(item => item.Id == upload.VersionId, cancellationToken);
        var job = await UploadJobAsync(upload.VersionId, cancellationToken);
        if (document.IsDeleteRequested || document.Status == "disabled" || version.Status is "disabled" or "uploaded" || job.Status is "cancelled" or "completed") return;
        version.Status = "failed";
        version.FailureReason = "Object storage upload failed; retry is available.";
        version.UpdatedAtUtc = DateTime.UtcNow;
        job.Status = "retrying";
        job.AttemptCount++;
        job.NextAttemptAtUtc = DateTime.UtcNow.AddSeconds(15);
        job.UpdatedAtUtc = DateTime.UtcNow;
        document.Status = "failed";
        document.UpdatedAtUtc = DateTime.UtcNow;
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> RequestPhysicalDeleteAsync(Guid documentId, CancellationToken cancellationToken)
    {
        await using var transaction = await BeginTransactionIfRelationalAsync(cancellationToken);
        var document = await database.KnowledgeDocuments.SingleOrDefaultAsync(item => item.Id == documentId, cancellationToken);
        if (document is null) return false;
        document.IsDeleteRequested = true;
        document.Status = "disabled";
        document.ActiveVersionId = null;
        document.UpdatedAtUtc = DateTime.UtcNow;
        var versions = await database.KnowledgeDocumentVersions.Where(item => item.KnowledgeDocumentId == documentId).ToArrayAsync(cancellationToken);
        foreach (var version in versions)
        {
            version.Status = "disabled";
            version.IsPublished = false;
            version.UpdatedAtUtc = DateTime.UtcNow;
        }
        var relatedJobs = await database.DurableJobs.Where(job =>
            (job.JobType == "UploadKnowledgeDocument" || job.JobType == "ParseKnowledgeDocument") &&
            job.PayloadJson.Contains(documentId.ToString()) && job.Status != "completed" && job.Status != "cancelled").ToArrayAsync(cancellationToken);
        foreach (var job in relatedJobs)
        {
            job.Status = "cancelled";
            job.LeaseOwner = null;
            job.LeaseExpiresAtUtc = null;
            job.UpdatedAtUtc = DateTime.UtcNow;
            job.Version++;
        }
        var cleanupId = CleanupJobId(documentId);
        if (!await database.DurableJobs.AnyAsync(job => job.Id == cleanupId, cancellationToken))
            database.DurableJobs.Add(NewJob("CleanupKnowledgeDocument", documentId, null, "pending", cleanupId));
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (exception.InnerException is MySqlException { Number: 1062 })
        {
            if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None);
            database.ChangeTracker.Clear();
            return true;
        }
    }

    private Task<DurableJobEntity> UploadJobAsync(Guid versionId, CancellationToken cancellationToken) => database.DurableJobs
        .Where(job => job.JobType == "UploadKnowledgeDocument" && job.PayloadJson.Contains(versionId.ToString()))
        .OrderByDescending(job => job.CreatedAtUtc).FirstAsync(cancellationToken);

    private static PendingDocumentUpload ToPending(KnowledgeDocumentVersionEntity version) => new(version.KnowledgeDocumentId,
        version.Id, version.Version, version.ObjectKey, version.SafeFileName, version.ContentType, version.Sha256, version.StagedContent,
        version.Status, version.PublicUrl);

    private static DurableJobEntity NewJob(string type, Guid documentId, Guid? versionId, string status = "pending", Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(), JobType = type, Status = status, PayloadJson = JsonSerializer.Serialize(new { documentId, versionId }), NextAttemptAtUtc = DateTime.UtcNow
    };

    private static Guid CleanupJobId(Guid documentId)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"CleanupKnowledgeDocument:{documentId:N}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?> BeginTransactionIfRelationalAsync(CancellationToken cancellationToken) =>
        database.Database.ProviderName?.Contains("InMemory", StringComparison.OrdinalIgnoreCase) == true
            ? null : await database.Database.BeginTransactionAsync(cancellationToken);
}
