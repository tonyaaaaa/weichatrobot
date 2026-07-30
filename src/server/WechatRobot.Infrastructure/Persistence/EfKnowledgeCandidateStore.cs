using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Handoffs;
using WechatRobot.Infrastructure.Knowledge;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.Infrastructure.Persistence;

public sealed class EfKnowledgeCandidateStore(WechatRobotDbContext db) : IKnowledgeCandidateStore
{
    public async Task<KnowledgeCandidateReviewResult> ReviewAsync(ReviewKnowledgeCandidateCommand command, DateTime nowUtc, CancellationToken token)
    {
        var tags = command.TagIds ?? throw new ArgumentException("TagIds is required.");
        var fingerprint = Fingerprint(command.CandidateId, command.ReviewerUserId, command.Decision, tags, command.RevisedAnswer);
        var prior = await db.KnowledgeReviews.AsNoTracking().SingleOrDefaultAsync(x => x.IdempotencyKey == command.IdempotencyKey, token);
        if (prior is not null)
        {
            if (prior.KnowledgeCandidateId != command.CandidateId) throw new InvalidOperationException("Review idempotency key belongs to another candidate.");
            if (prior.RequestFingerprint is not null && !string.Equals(prior.RequestFingerprint, fingerprint, StringComparison.Ordinal))
                throw new HandoffStateException("Review idempotency key was already used with a different payload.");
            if (prior.Decision == "approve") await EnsurePublishOutboxAsync(command.CandidateId, prior.TagIdsJson, nowUtc, token);
            return await ResultAsync(command.CandidateId, token);
        }

        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(token) : null;
        var candidate = await db.KnowledgeCandidates.SingleOrDefaultAsync(x => x.Id == command.CandidateId, token) ?? throw new KeyNotFoundException();
        if (candidate.Version != command.ExpectedVersion) throw new HandoffConcurrencyException("The candidate was modified by another reviewer.");
        if (candidate.Status is not ("pending" or "revision")) throw new HandoffStateException("The candidate is not awaiting review.");
        if (command.Decision == "approve")
        {
            var distinct = tags.Distinct().ToArray();
            var predicate = GuidBatchQuery.BuildPredicate<KnowledgeTagEntity>(distinct, tag => tag.Id);
            if (await db.KnowledgeTags.Where(x => x.IsEnabled).Where(predicate).CountAsync(token) != distinct.Length)
                throw new ArgumentException("Approval tags must exist and be enabled.");
        }
        var answer = string.IsNullOrWhiteSpace(command.RevisedAnswer) ? candidate.Answer : command.RevisedAnswer.Trim();
        db.KnowledgeReviews.Add(new KnowledgeReviewEntity { KnowledgeCandidateId = candidate.Id, ReviewerUserId = command.ReviewerUserId,
            Decision = command.Decision, TagIdsJson = JsonSerializer.Serialize(tags.Distinct().Order()), RevisedAnswer = command.RevisedAnswer,
            IdempotencyKey = command.IdempotencyKey, RequestFingerprint = fingerprint, CreatedAtUtc = nowUtc });

        if (command.Decision is "reject" or "revision")
        {
            candidate.Answer = answer; candidate.Status = command.Decision == "reject" ? "rejected" : "revision";
            candidate.Version++; candidate.UpdatedAtUtc = nowUtc;
            try { await db.SaveChangesAsync(token); if (transaction is not null) await transaction.CommitAsync(token); }
            catch (DbUpdateConcurrencyException) { if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None); throw new HandoffConcurrencyException("The candidate was modified by another reviewer."); }
            catch (DbUpdateException exception) when (exception.InnerException is MySql.Data.MySqlClient.MySqlException { Number: 1062 })
            {
                if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None);
                return await ReplayUniqueConflictAsync(command, fingerprint, token);
            }
            return await ResultAsync(candidate.Id, token);
        }

        var document = new KnowledgeDocumentEntity { Title = Limit(candidate.Question, 256), Status = "draft", CreatedAtUtc = nowUtc, UpdatedAtUtc = nowUtc };
        var version = new KnowledgeDocumentVersionEntity { KnowledgeDocumentId = document.Id, Version = 1, SourceKind = "ConversationReview",
            OriginalFileName = $"reviewed-{candidate.Id:N}.md", SafeFileName = $"reviewed-{candidate.Id:N}.md", ContentType = "text/markdown",
            Sha256 = Hash($"{candidate.Id:N}|{candidate.Question}|{answer}"), SizeBytes = Encoding.UTF8.GetByteCount(answer),
            ObjectKey = $"reviewed/{candidate.Id:N}.md", Status = "approved", IsPublished = false, CreatedAtUtc = nowUtc, UpdatedAtUtc = nowUtc };
        var chunk = new KnowledgeChunkEntity { KnowledgeDocumentVersionId = version.Id, Sequence = 0,
            Text = $"问题：{candidate.Question}\n答案：{answer}", Question = candidate.Question, Answer = answer, Status = "approved", CreatedAtUtc = nowUtc, UpdatedAtUtc = nowUtc };
        db.AddRange(document, version, chunk);
        candidate.Answer = answer; candidate.Status = "approved_pending_index"; candidate.KnowledgeDocumentVersionId = version.Id;
        candidate.Version++; candidate.UpdatedAtUtc = nowUtc;
        db.DurableJobs.Add(PublishJob(candidate.Id, document.Id, version.Id, tags, nowUtc));
        try { await db.SaveChangesAsync(token); if (transaction is not null) await transaction.CommitAsync(token); }
        catch (DbUpdateConcurrencyException) { if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None); throw new HandoffConcurrencyException("The candidate was modified by another reviewer."); }
        catch (DbUpdateException exception) when (exception.InnerException is MySql.Data.MySqlClient.MySqlException { Number: 1062 })
        {
            if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None);
            return await ReplayUniqueConflictAsync(command, fingerprint, token);
        }

        return new(candidate.Id, candidate.Status, version.Id, null, candidate.Version, candidate.Id);
    }

    private async Task<KnowledgeCandidateReviewResult> ResultAsync(Guid candidateId, CancellationToken token)
    {
        var item = await db.KnowledgeCandidates.AsNoTracking().SingleAsync(x => x.Id == candidateId, token);
        var jobId = item.KnowledgeDocumentVersionId is { } versionId
            ? await db.KnowledgeIndexJobs.AsNoTracking().Where(x => x.KnowledgeDocumentVersionId == versionId && x.Operation != "cleanup").Select(x => (Guid?)x.Id).FirstOrDefaultAsync(token)
            : null;
        var publishJobId = await db.DurableJobs.AsNoTracking().Where(x => x.Id == item.Id && x.JobType == "PublishKnowledgeCandidate")
            .Select(x => (Guid?)x.Id).SingleOrDefaultAsync(token);
        return new(item.Id, item.Status, item.KnowledgeDocumentVersionId, jobId, item.Version, publishJobId);
    }

    private async Task<KnowledgeCandidateReviewResult> ReplayUniqueConflictAsync(ReviewKnowledgeCandidateCommand command, string fingerprint,
        CancellationToken token)
    {
        db.ChangeTracker.Clear();
        var committed = await db.KnowledgeReviews.AsNoTracking().SingleOrDefaultAsync(x => x.IdempotencyKey == command.IdempotencyKey, token);
        if (committed is not null && committed.KnowledgeCandidateId == command.CandidateId &&
            (committed.RequestFingerprint is null || string.Equals(committed.RequestFingerprint, fingerprint, StringComparison.Ordinal)))
            return await ResultAsync(command.CandidateId, token);
        throw new HandoffConcurrencyException("The candidate was modified by another reviewer.");
    }

    private async Task EnsurePublishOutboxAsync(Guid candidateId, string tagIdsJson, DateTime nowUtc, CancellationToken token)
    {
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(token) : null;
        var candidate = transaction is null
            ? await db.KnowledgeCandidates.SingleAsync(x => x.Id == candidateId, token)
            : await db.KnowledgeCandidates.FromSqlInterpolated($"SELECT * FROM knowledge_candidate WHERE Id = {candidateId} FOR UPDATE")
                .SingleAsync(token);
        if (candidate.KnowledgeDocumentVersionId is not { } versionId) throw new HandoffStateException("Approved candidate has no knowledge version.");
        var version = await db.KnowledgeDocumentVersions.AsNoTracking().SingleAsync(x => x.Id == versionId, token);
        var job = await db.DurableJobs.SingleOrDefaultAsync(x => x.Id == candidateId, token);
        var hasUsableIndexJob = await db.KnowledgeIndexJobs.AnyAsync(x => x.KnowledgeDocumentVersionId == versionId && x.Operation != "cleanup" &&
            (x.Status == "pending" || x.Status == "retrying" || x.Status == "leased" || x.Status == "activating" || x.Status == "completed"), token);
        if (job is null) db.DurableJobs.Add(PublishJob(candidateId, version.KnowledgeDocumentId, versionId,
            JsonSerializer.Deserialize<Guid[]>(tagIdsJson) ?? [], nowUtc));
        else if (candidate.Status != "published" && (job.Status == "deadLetter" || job.Status == "completed" && !hasUsableIndexJob))
        {
            job.Status = "retrying"; job.AttemptCount = 0; job.NextAttemptAtUtc = nowUtc; job.LeaseOwner = null; job.LeaseExpiresAtUtc = null;
            job.CompletedAtUtc = null; job.Version++; job.UpdatedAtUtc = nowUtc;
        }
        if (candidate.Status == "indexing" && !hasUsableIndexJob)
        { candidate.Status = "approved_pending_index"; candidate.Version++; candidate.UpdatedAtUtc = nowUtc; }
        await db.SaveChangesAsync(token);
        if (transaction is not null) await transaction.CommitAsync(token);
    }

    private static DurableJobEntity PublishJob(Guid candidateId, Guid documentId, Guid versionId, IReadOnlyList<Guid> tags, DateTime now) => new()
    {
        Id = candidateId, JobType = "PublishKnowledgeCandidate", PayloadJson = JsonSerializer.Serialize(new
        { CandidateId = candidateId, DocumentId = documentId, VersionId = versionId, TagIds = tags.Distinct().Order().ToArray() }),
        Status = "pending", AvailableAtUtc = now, NextAttemptAtUtc = now, CreatedAtUtc = now, UpdatedAtUtc = now
    };

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Fingerprint(Guid candidateId, Guid reviewerUserId, string decision, IReadOnlyList<Guid> tags, string? revisedAnswer) =>
        Hash(JsonSerializer.Serialize(new { candidateId, reviewerUserId, decision, tags = tags.Distinct().Order().ToArray(), revisedAnswer = revisedAnswer?.Trim() }));
    private static string Limit(string value, int max) => value.Length <= max ? value : value[..max];
}
