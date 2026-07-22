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
        database.DurableJobs.AddRange(NewJob("UploadKnowledgeDocument", documentId, version.Id), NewJob("ParseKnowledgeDocument", documentId, version.Id));

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
        var version = await database.KnowledgeDocumentVersions.AsNoTracking()
            .Where(item => item.KnowledgeDocumentId == documentId && item.Status == "failed")
            .OrderByDescending(item => item.Version).FirstOrDefaultAsync(cancellationToken);
        return version is null || version.StagedContent.Length == 0 ? null : ToPending(version);
    }

    public async Task MarkUploadedAsync(PendingDocumentUpload upload, StoredObject stored, CancellationToken cancellationToken)
    {
        database.ChangeTracker.Clear();
        var document = await database.KnowledgeDocuments.SingleAsync(item => item.Id == upload.DocumentId, cancellationToken);
        var version = await database.KnowledgeDocumentVersions.SingleAsync(item => item.Id == upload.VersionId, cancellationToken);
        var job = await UploadJobAsync(upload.VersionId, cancellationToken);
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
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkFailedAsync(PendingDocumentUpload upload, CancellationToken cancellationToken)
    {
        database.ChangeTracker.Clear();
        var document = await database.KnowledgeDocuments.SingleAsync(item => item.Id == upload.DocumentId, cancellationToken);
        var version = await database.KnowledgeDocumentVersions.SingleAsync(item => item.Id == upload.VersionId, cancellationToken);
        var job = await UploadJobAsync(upload.VersionId, cancellationToken);
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
        var document = await database.KnowledgeDocuments.SingleOrDefaultAsync(item => item.Id == documentId, cancellationToken);
        if (document is null) return false;
        document.IsDeleteRequested = true;
        document.Status = "disabled";
        document.UpdatedAtUtc = DateTime.UtcNow;
        database.DurableJobs.Add(NewJob("CleanupKnowledgeDocument", documentId, null));
        await database.SaveChangesAsync(cancellationToken);
        return true;
    }

    private Task<DurableJobEntity> UploadJobAsync(Guid versionId, CancellationToken cancellationToken) => database.DurableJobs
        .Where(job => job.JobType == "UploadKnowledgeDocument" && job.PayloadJson.Contains(versionId.ToString()))
        .OrderByDescending(job => job.CreatedAtUtc).FirstAsync(cancellationToken);

    private static PendingDocumentUpload ToPending(KnowledgeDocumentVersionEntity version) => new(version.KnowledgeDocumentId,
        version.Id, version.Version, version.ObjectKey, version.SafeFileName, version.ContentType, version.Sha256, version.StagedContent,
        version.Status, version.PublicUrl);

    private static DurableJobEntity NewJob(string type, Guid documentId, Guid? versionId) => new()
    {
        JobType = type, PayloadJson = JsonSerializer.Serialize(new { documentId, versionId }), NextAttemptAtUtc = DateTime.UtcNow
    };

    private async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?> BeginTransactionIfRelationalAsync(CancellationToken cancellationToken) =>
        database.Database.ProviderName?.Contains("InMemory", StringComparison.OrdinalIgnoreCase) == true
            ? null : await database.Database.BeginTransactionAsync(cancellationToken);
}
