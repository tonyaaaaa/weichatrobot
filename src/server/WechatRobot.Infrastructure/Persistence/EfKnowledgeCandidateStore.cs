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
        var prior = await db.KnowledgeReviews.AsNoTracking().SingleOrDefaultAsync(x => x.IdempotencyKey == command.IdempotencyKey, token);
        if (prior is not null)
        {
            if (prior.KnowledgeCandidateId != command.CandidateId) throw new InvalidOperationException("Review idempotency key belongs to another candidate.");
            if (prior.Decision == "approve") await EnsurePublishOutboxAsync(command.CandidateId, prior.TagIdsJson, nowUtc, token);
            return await ResultAsync(command.CandidateId, token);
        }

        var candidate = await db.KnowledgeCandidates.SingleOrDefaultAsync(x => x.Id == command.CandidateId, token) ?? throw new KeyNotFoundException();
        if (candidate.Version != command.ExpectedVersion) throw new HandoffConcurrencyException("The candidate was modified by another reviewer.");
        if (candidate.Status is not ("pending" or "revision")) throw new HandoffStateException("The candidate is not awaiting review.");
        var answer = string.IsNullOrWhiteSpace(command.RevisedAnswer) ? candidate.Answer : command.RevisedAnswer.Trim();
        db.KnowledgeReviews.Add(new KnowledgeReviewEntity { KnowledgeCandidateId = candidate.Id, ReviewerUserId = command.ReviewerUserId,
            Decision = command.Decision, TagIdsJson = JsonSerializer.Serialize(command.TagIds.Distinct().Order()), RevisedAnswer = command.RevisedAnswer,
            IdempotencyKey = command.IdempotencyKey, CreatedAtUtc = nowUtc });

        if (command.Decision is "reject" or "revision")
        {
            candidate.Answer = answer; candidate.Status = command.Decision == "reject" ? "rejected" : "revision";
            candidate.Version++; candidate.UpdatedAtUtc = nowUtc;
            try { await db.SaveChangesAsync(token); }
            catch (DbUpdateConcurrencyException) { throw new HandoffConcurrencyException("The candidate was modified by another reviewer."); }
            catch (DbUpdateException exception) when (exception.InnerException is MySql.Data.MySqlClient.MySqlException { Number: 1062 })
            { db.ChangeTracker.Clear(); return await ResultAsync(command.CandidateId, token); }
            return await ResultAsync(candidate.Id, token);
        }

        var document = new KnowledgeDocumentEntity { Title = Limit(candidate.Question, 256), Status = "draft", CreatedAtUtc = nowUtc, UpdatedAtUtc = nowUtc };
        var version = new KnowledgeDocumentVersionEntity { KnowledgeDocumentId = document.Id, Version = 1,
            OriginalFileName = $"reviewed-{candidate.Id:N}.md", SafeFileName = $"reviewed-{candidate.Id:N}.md", ContentType = "text/markdown",
            Sha256 = Hash($"{candidate.Id:N}|{candidate.Question}|{answer}"), SizeBytes = Encoding.UTF8.GetByteCount(answer),
            ObjectKey = $"reviewed/{candidate.Id:N}.md", Status = "approved", IsPublished = false, CreatedAtUtc = nowUtc, UpdatedAtUtc = nowUtc };
        var chunk = new KnowledgeChunkEntity { KnowledgeDocumentVersionId = version.Id, Sequence = 0,
            Text = $"问题：{candidate.Question}\n答案：{answer}", Question = candidate.Question, Answer = answer, Status = "approved", CreatedAtUtc = nowUtc, UpdatedAtUtc = nowUtc };
        db.AddRange(document, version, chunk);
        candidate.Answer = answer; candidate.Status = "approved_pending_index"; candidate.KnowledgeDocumentVersionId = version.Id;
        candidate.Version++; candidate.UpdatedAtUtc = nowUtc;
        db.DurableJobs.Add(PublishJob(candidate.Id, document.Id, version.Id, command.TagIds, nowUtc));
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateConcurrencyException) { throw new HandoffConcurrencyException("The candidate was modified by another reviewer."); }
        catch (DbUpdateException exception) when (exception.InnerException is MySql.Data.MySqlClient.MySqlException { Number: 1062 })
        { db.ChangeTracker.Clear(); return await ResultAsync(command.CandidateId, token); }

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

    private async Task EnsurePublishOutboxAsync(Guid candidateId, string tagIdsJson, DateTime nowUtc, CancellationToken token)
    {
        var candidate = await db.KnowledgeCandidates.SingleAsync(x => x.Id == candidateId, token);
        if (candidate.KnowledgeDocumentVersionId is not { } versionId) throw new HandoffStateException("Approved candidate has no knowledge version.");
        var version = await db.KnowledgeDocumentVersions.AsNoTracking().SingleAsync(x => x.Id == versionId, token);
        var job = await db.DurableJobs.SingleOrDefaultAsync(x => x.Id == candidateId, token);
        var hasIndexJob = await db.KnowledgeIndexJobs.AnyAsync(x => x.KnowledgeDocumentVersionId == versionId && x.Operation != "cleanup", token);
        if (job is null) db.DurableJobs.Add(PublishJob(candidateId, version.KnowledgeDocumentId, versionId,
            JsonSerializer.Deserialize<Guid[]>(tagIdsJson) ?? [], nowUtc));
        else if (candidate.Status != "published" && (job.Status == "deadLetter" || job.Status == "completed" && !hasIndexJob))
        {
            job.Status = "retrying"; job.AttemptCount = 0; job.NextAttemptAtUtc = nowUtc; job.LeaseOwner = null; job.LeaseExpiresAtUtc = null;
            job.CompletedAtUtc = null; job.Version++; job.UpdatedAtUtc = nowUtc;
        }
        if (candidate.Status == "indexing" && !hasIndexJob)
        { candidate.Status = "approved_pending_index"; candidate.Version++; candidate.UpdatedAtUtc = nowUtc; }
        await db.SaveChangesAsync(token);
    }

    private static DurableJobEntity PublishJob(Guid candidateId, Guid documentId, Guid versionId, IReadOnlyList<Guid> tags, DateTime now) => new()
    {
        Id = candidateId, JobType = "PublishKnowledgeCandidate", PayloadJson = JsonSerializer.Serialize(new
        { CandidateId = candidateId, DocumentId = documentId, VersionId = versionId, TagIds = tags.Distinct().Order().ToArray() }),
        Status = "pending", AvailableAtUtc = now, NextAttemptAtUtc = now, CreatedAtUtc = now, UpdatedAtUtc = now
    };

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Limit(string value, int max) => value.Length <= max ? value : value[..max];
}
