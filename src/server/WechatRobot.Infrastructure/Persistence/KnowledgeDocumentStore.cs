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
            SourceKind = "DocumentUpload",
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
        return ToPending(version, document.StateVersion);
    }

    public Task<PendingDocumentUpload?> GetRetryableAsync(
        Guid documentId,
        CancellationToken cancellationToken) =>
        GetRetryableCoreAsync(documentId, null, null, cancellationToken);

    public Task<PendingDocumentUpload?> GetRetryableAsync(
        Guid documentId,
        int expectedStateVersion,
        string actor,
        CancellationToken cancellationToken) =>
        GetRetryableCoreAsync(documentId, expectedStateVersion, actor, cancellationToken);

    private async Task<PendingDocumentUpload?> GetRetryableCoreAsync(
        Guid documentId,
        int? expectedStateVersion,
        string? actor,
        CancellationToken cancellationToken)
    {
        var document = await database.KnowledgeDocuments.AsNoTracking().SingleOrDefaultAsync(item => item.Id == documentId, cancellationToken);
        if (document is null) throw new DocumentNotFoundException();
        if (expectedStateVersion is not null && document.StateVersion != expectedStateVersion)
            throw Concurrency(document);
        if (document.IsDeleteRequested) throw new DocumentDeleteRequestedException();
        if (document.Status == "disabled") throw new DocumentDeletedException();
        var version = await database.KnowledgeDocumentVersions.AsNoTracking()
            .Where(item => item.KnowledgeDocumentId == documentId)
            .OrderByDescending(item => item.Version).FirstOrDefaultAsync(cancellationToken);
        return version is null || version.Status != "failed" || version.StagedContent.Length == 0
            ? null
            : ToPending(version, document.StateVersion, actor);
    }

    public async Task<PendingDocumentUpload?> GetRecoverableAsync(Guid versionId, CancellationToken cancellationToken)
    {
        var version = await database.KnowledgeDocumentVersions.AsNoTracking().SingleOrDefaultAsync(item => item.Id == versionId, cancellationToken);
        if (version is null || version.Status == "uploaded" || version.StagedContent.Length == 0) return null;
        var document = await database.KnowledgeDocuments.AsNoTracking().SingleAsync(item => item.Id == version.KnowledgeDocumentId, cancellationToken);
        return document.IsDeleteRequested || document.Status == "disabled" || version.Status == "disabled"
            ? null
            : ToPending(version, document.StateVersion);
    }

    public async Task<bool> MarkUploadedAsync(PendingDocumentUpload upload, StoredObject stored, CancellationToken cancellationToken)
    {
        try
        {
            return await MarkUploadedTrackedAsync(upload, stored, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            if (upload.AuditActor is not null)
                await ThrowManagementConflictAsync(upload.DocumentId, cancellationToken);
            return false;
        }
    }

    public async Task MarkFailedAsync(PendingDocumentUpload upload, CancellationToken cancellationToken)
    {
        try
        {
            await MarkFailedTrackedAsync(upload, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            if (upload.AuditActor is not null)
                await ThrowManagementConflictAsync(upload.DocumentId, cancellationToken);
        }
    }

    public async Task<bool> RequestPhysicalDeleteAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var current = await database.KnowledgeDocuments.AsNoTracking().SingleOrDefaultAsync(
            document => document.Id == documentId,
            cancellationToken);
        if (current is null) return false;
        if (current.IsDeleteRequested) return true;
        return await RequestPhysicalDeleteCoreAsync(documentId, current.StateVersion, null, false, cancellationToken);
    }

    public Task<bool> RequestPhysicalDeleteAsync(
        Guid documentId,
        int expectedStateVersion,
        string actor,
        CancellationToken cancellationToken) =>
        RequestPhysicalDeleteCoreAsync(
            documentId,
            expectedStateVersion,
            actor,
            true,
            cancellationToken);

    private async Task<bool> RequestPhysicalDeleteCoreAsync(
        Guid documentId,
        int expectedStateVersion,
        string? actor,
        bool rejectExistingRequest,
        CancellationToken cancellationToken)
    {
        try
        {
            return await RequestPhysicalDeleteTrackedAsync(
                documentId,
                expectedStateVersion,
                actor,
                rejectExistingRequest,
                cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await ThrowManagementConflictAsync(documentId, cancellationToken);
            return false;
        }
        catch (DbUpdateException exception) when (exception.InnerException is MySqlException { Number: 1062 })
        {
            database.ChangeTracker.Clear();
            return true;
        }
    }

    private async Task<bool> TryRequeuePhysicalCleanupAsync(
        Guid documentId,
        string? actor,
        CancellationToken cancellationToken)
    {
        var cleanupId = KnowledgeDocumentCleanupJobIdentity.Create(documentId);
        var terminalStatuses = new[] { "deadLetter", "cancelled", "completed" };
        var cleanup = await database.DurableJobs.SingleOrDefaultAsync(
            job =>
                job.Id == cleanupId &&
                job.JobType == "CleanupKnowledgeDocument",
            cancellationToken);
        if (cleanup is null || !terminalStatuses.Contains(cleanup.Status))
            return false;

        var now = DateTime.UtcNow;
        var priorStatus = cleanup.Status;
        var priorAttemptCount = cleanup.AttemptCount;
        cleanup.Status = "pending";
        cleanup.AttemptCount = 0;
        cleanup.NextAttemptAtUtc = now;
        cleanup.CompletedAtUtc = null;
        cleanup.LeaseOwner = null;
        cleanup.LeaseExpiresAtUtc = null;
        cleanup.UpdatedAtUtc = now;
        cleanup.Version++;
        database.DeadLetters.RemoveRange(
            await database.DeadLetters
                .Where(letter => letter.DurableJobId == cleanupId)
                .ToArrayAsync(cancellationToken));
        AddPhysicalCleanupRetryAudit(
            actor,
            documentId,
            priorStatus,
            priorAttemptCount);
        await database.SaveChangesAsync(cancellationToken);
        return true;
    }

    private void AddPhysicalCleanupRetryAudit(
        string? actor,
        Guid documentId,
        string priorStatus,
        int priorAttemptCount)
    {
        AddAudit(
            actor,
            "knowledge-document.retry-physical-delete",
            documentId,
            new
            {
                before = new
                {
                    status = priorStatus,
                    attemptCount = priorAttemptCount
                },
                after = new { status = "pending", attemptCount = 0 }
            });
    }

    private Task<Guid> UploadJobIdAsync(Guid versionId, CancellationToken cancellationToken) => database.DurableJobs
        .Where(job => job.JobType == "UploadKnowledgeDocument" && job.PayloadJson.Contains(versionId.ToString()))
        .OrderByDescending(job => job.CreatedAtUtc).Select(job => job.Id).FirstAsync(cancellationToken);

    private static PendingDocumentUpload ToPending(
        KnowledgeDocumentVersionEntity version,
        int documentStateVersion,
        string? auditActor = null) => new(version.KnowledgeDocumentId,
        version.Id, version.Version, version.ObjectKey, version.SafeFileName, version.ContentType, version.Sha256, version.StagedContent,
        version.Status, version.PublicUrl, documentStateVersion, auditActor);

    private static DurableJobEntity NewJob(string type, Guid documentId, Guid? versionId, string status = "pending", Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(), JobType = type, Status = status, PayloadJson = JsonSerializer.Serialize(new { documentId, versionId }), NextAttemptAtUtc = DateTime.UtcNow
    };

    private async Task<bool> MarkUploadedTrackedAsync(PendingDocumentUpload upload, StoredObject stored, CancellationToken cancellationToken)
    {
        database.ChangeTracker.Clear();
        var document = await database.KnowledgeDocuments.SingleAsync(item => item.Id == upload.DocumentId, cancellationToken);
        var version = await database.KnowledgeDocumentVersions.SingleAsync(item => item.Id == upload.VersionId, cancellationToken);
        var uploadJobId = await UploadJobIdAsync(upload.VersionId, cancellationToken);
        var uploadJob = await database.DurableJobs.SingleAsync(job => job.Id == uploadJobId, cancellationToken);
        if (document.IsDeleteRequested || document.Status == "disabled" || version.Status == "disabled" ||
            uploadJob.Status == "cancelled")
        {
            if (upload.AuditActor is not null) throw Concurrency(document);
            return false;
        }
        if (IsUploadedOrLater(version.Status))
        {
            if (upload.AuditActor is not null && document.StateVersion != upload.DocumentStateVersion)
                throw Concurrency(document);
            return true;
        }
        if (document.StateVersion != upload.DocumentStateVersion)
        {
            if (upload.AuditActor is not null) throw Concurrency(document);
            return false;
        }
        var now = DateTime.UtcNow;
        uploadJob.Status = "completed"; uploadJob.CompletedAtUtc = now; uploadJob.LeaseOwner = null; uploadJob.LeaseExpiresAtUtc = null; uploadJob.UpdatedAtUtc = now; uploadJob.Version++;
        document.Status = "uploaded"; document.StateVersion++; document.UpdatedAtUtc = now;
        version.PublicUrl = stored.PublicUrl.AbsoluteUri; version.Status = "uploaded"; version.FailureReason = null; version.StagedContent = []; version.UpdatedAtUtc = now;
        var parseJob = await database.DurableJobs.SingleAsync(job => job.JobType == "ParseKnowledgeDocument" && job.PayloadJson.Contains(upload.VersionId.ToString()), cancellationToken);
        if (parseJob.Status == "blocked") { parseJob.Status = "pending"; parseJob.NextAttemptAtUtc = now; parseJob.UpdatedAtUtc = now; parseJob.Version++; }
        AddRetryAudit(upload, "uploaded", document.StateVersion, null);
        await database.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static bool IsUploadedOrLater(string status) => status is
        "uploaded" or "preview" or "approved" or "indexing" or "indexed" or "active";

    private async Task MarkFailedTrackedAsync(PendingDocumentUpload upload, CancellationToken cancellationToken)
    {
        database.ChangeTracker.Clear();
        var document = await database.KnowledgeDocuments.SingleAsync(item => item.Id == upload.DocumentId, cancellationToken);
        var version = await database.KnowledgeDocumentVersions.SingleAsync(item => item.Id == upload.VersionId, cancellationToken);
        var uploadJobId = await UploadJobIdAsync(upload.VersionId, cancellationToken);
        var uploadJob = await database.DurableJobs.SingleAsync(job => job.Id == uploadJobId, cancellationToken);
        if (document.IsDeleteRequested || document.Status == "disabled" || version.Status is "disabled" or "uploaded" ||
            uploadJob.Status is "cancelled" or "completed" || document.StateVersion != upload.DocumentStateVersion)
        {
            if (upload.AuditActor is not null) throw Concurrency(document);
            return;
        }
        var now = DateTime.UtcNow;
        uploadJob.Status = "retrying"; uploadJob.AttemptCount++; uploadJob.NextAttemptAtUtc = now.AddSeconds(15); uploadJob.LeaseOwner = null; uploadJob.LeaseExpiresAtUtc = null; uploadJob.UpdatedAtUtc = now; uploadJob.Version++;
        document.Status = "failed"; document.StateVersion++; document.UpdatedAtUtc = now;
        version.Status = "failed"; version.FailureReason = "Object storage upload failed; retry is available."; version.UpdatedAtUtc = now;
        AddRetryAudit(upload, "failed", document.StateVersion, "object-storage-upload-failed");
        await database.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> RequestPhysicalDeleteTrackedAsync(
        Guid documentId,
        int expectedStateVersion,
        string? actor,
        bool rejectExistingRequest,
        CancellationToken cancellationToken)
    {
        database.ChangeTracker.Clear();
        var document = await database.KnowledgeDocuments.SingleOrDefaultAsync(item => item.Id == documentId, cancellationToken);
        if (document is null) return false;
        if (document.StateVersion != expectedStateVersion) throw Concurrency(document);
        if (document.IsDeleteRequested)
        {
            if (rejectExistingRequest)
            {
                if (await TryRequeuePhysicalCleanupAsync(
                        documentId,
                        actor,
                        cancellationToken))
                    return true;
                throw new DocumentDeleteRequestedException();
            }
            return true;
        }
        var now = DateTime.UtcNow;
        var priorStatus = document.Status;
        document.IsDeleteRequested = true; document.Status = "disabled"; document.ActiveVersionId = null; document.StateVersion++; document.UpdatedAtUtc = now;
        foreach (var version in await database.KnowledgeDocumentVersions.Where(item => item.KnowledgeDocumentId == documentId).ToArrayAsync(cancellationToken))
        { version.Status = "disabled"; version.IsPublished = false; version.UpdatedAtUtc = now; }
        foreach (var job in await database.DurableJobs.Where(job => (job.JobType == "UploadKnowledgeDocument" || job.JobType == "ParseKnowledgeDocument") &&
                     job.PayloadJson.Contains(documentId.ToString()) && job.Status != "completed" && job.Status != "cancelled").ToArrayAsync(cancellationToken))
        { job.Status = "cancelled"; job.LeaseOwner = null; job.LeaseExpiresAtUtc = null; job.UpdatedAtUtc = now; job.Version++; }
        foreach (var job in await database.KnowledgeIndexJobs.Where(job => job.KnowledgeDocumentId == documentId && job.Operation != "cleanup" &&
                     (job.Status == "pending" || job.Status == "retrying" || job.Status == "leased" || job.Status == "activating")).ToArrayAsync(cancellationToken))
        { job.Status = "cancelled"; job.LeaseOwner = null; job.UpdatedAtUtc = now; job.Version++; }
        var cleanupId = KnowledgeDocumentCleanupJobIdentity.Create(documentId);
        if (!await database.DurableJobs.AnyAsync(job => job.Id == cleanupId, cancellationToken)) database.DurableJobs.Add(NewJob("CleanupKnowledgeDocument", documentId, null, "pending", cleanupId));
        AddAudit(
            actor,
            "knowledge-document.request-physical-delete",
            documentId,
            new
            {
                before = new { status = priorStatus, stateVersion = expectedStateVersion },
                after = new { status = "disabled", stateVersion = document.StateVersion, isDeleteRequested = true }
            });
        await database.SaveChangesAsync(cancellationToken);
        return true;
    }

    private void AddRetryAudit(
        PendingDocumentUpload upload,
        string newStatus,
        int newStateVersion,
        string? failureCategory)
    {
        if (upload.AuditActor is null) return;
        AddAudit(
            upload.AuditActor,
            "knowledge-document.retry-upload",
            upload.DocumentId,
            new
            {
                versionId = upload.VersionId,
                version = upload.Version,
                before = new { status = upload.State, stateVersion = upload.DocumentStateVersion },
                after = new { status = newStatus, stateVersion = newStateVersion },
                failureCategory
            });
    }

    private void AddAudit(string? actor, string action, Guid documentId, object detail)
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

    private async Task ThrowManagementConflictAsync(Guid documentId, CancellationToken cancellationToken)
    {
        database.ChangeTracker.Clear();
        var current = await database.KnowledgeDocuments.AsNoTracking().SingleOrDefaultAsync(
            document => document.Id == documentId,
            cancellationToken);
        if (current is null) throw new DocumentNotFoundException();
        if (current.IsDeleteRequested) throw new DocumentDeleteRequestedException();
        throw Concurrency(current);
    }

    private static DocumentConcurrencyException Concurrency(KnowledgeDocumentEntity document) =>
        new(new KnowledgeDocumentCurrentState(document.Id, document.Status, document.StateVersion));

    private async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?> BeginTransactionIfRelationalAsync(CancellationToken cancellationToken) =>
        database.Database.ProviderName?.Contains("InMemory", StringComparison.OrdinalIgnoreCase) == true
            ? null : await database.Database.BeginTransactionAsync(cancellationToken);
}
