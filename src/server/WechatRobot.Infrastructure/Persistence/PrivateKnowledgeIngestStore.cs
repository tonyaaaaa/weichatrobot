using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;
using WechatRobot.Application.PrivateChat;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.Infrastructure.Persistence;

public sealed class PrivateKnowledgeIngestStore(WechatRobotDbContext database) : IPrivateKnowledgeIngestStore
{
    public async Task<PrivateKnowledgeIngestBatch> GetOrCreateAsync(
        Guid robotConfigId, Guid sourceConversationMessageId, int roomType,
        string sourceActorDisplayName, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var existing = await database.PrivateKnowledgeIngestBatches.SingleOrDefaultAsync(
            x => x.SourceConversationMessageId == sourceConversationMessageId, cancellationToken);
        if (existing is not null) return Map(existing);
        var entity = new PrivateKnowledgeIngestBatchEntity
        {
            RobotConfigId = robotConfigId,
            SourceConversationMessageId = sourceConversationMessageId,
            RoomType = roomType,
            SourceActorDisplayName = sourceActorDisplayName,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc
        };
        database.Add(entity);
        try { await database.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) when (exception.InnerException is MySqlException { Number: 1062 })
        {
            database.Entry(entity).State = EntityState.Detached;
            entity = await database.PrivateKnowledgeIngestBatches.SingleAsync(
                x => x.SourceConversationMessageId == sourceConversationMessageId, cancellationToken);
        }
        return Map(entity);
    }

    public async Task<PrivateKnowledgeIngestBatch?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        (await database.PrivateKnowledgeIngestBatches.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken)) is { } entity ? Map(entity) : null;

    public async Task<IReadOnlyList<PrivateKnowledgeIngestBatch>> ListAsync(
        string? status, int skip, int take, CancellationToken cancellationToken)
    {
        var query = database.PrivateKnowledgeIngestBatches.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);
        return (await query.OrderByDescending(x => x.CreatedAtUtc).Skip(Math.Max(0, skip))
            .Take(Math.Clamp(take, 1, 200)).ToListAsync(cancellationToken)).Select(Map).ToArray();
    }

    public async Task SaveProposalsAsync(
        Guid batchId, int expectedVersion, IReadOnlyList<ProposedKnowledgeItem> proposals,
        DateTime nowUtc, CancellationToken cancellationToken)
    {
        if (proposals.Count is < 1 or > 20) throw new InvalidOperationException("Proposal count is invalid.");
        var batch = await database.PrivateKnowledgeIngestBatches.SingleAsync(x => x.Id == batchId, cancellationToken);
        if (batch.Version != expectedVersion) throw new DbUpdateConcurrencyException();
        var existing = await database.PrivateKnowledgeIngestItems.Where(x => x.BatchId == batchId).ToListAsync(cancellationToken);
        database.RemoveRange(existing);
        database.AddRange(proposals.Select((item, index) => new PrivateKnowledgeIngestItemEntity
        {
            BatchId = batchId, Sequence = index + 1, Question = item.Question.Trim(), Answer = item.Answer.Trim(),
            ChangeKind = item.ChangeKind.ToString(), MatchedVersionId = item.SimilarVersionId,
            QuestionFingerprint = Hash(item.Question), AnswerFingerprint = Hash(item.Answer),
            ProposedTagsJson = JsonSerializer.Serialize(item.ExplicitTags),
            ResolvedTagIdsJson = item.SuggestedTagId is { } id ? JsonSerializer.Serialize(new[] { id }) : "[]",
            CreatedAtUtc = nowUtc
        }));
        batch.Status = "Staged";
        batch.TotalCount = proposals.Count;
        batch.NewCount = proposals.Count(x => x.ChangeKind == KnowledgeChangeKind.New);
        batch.DuplicateCount = proposals.Count(x => x.ChangeKind == KnowledgeChangeKind.Duplicate);
        batch.SupplementCount = proposals.Count(x => x.ChangeKind == KnowledgeChangeKind.Supplement);
        batch.CorrectionCount = proposals.Count(x => x.ChangeKind == KnowledgeChangeKind.Correction);
        batch.Version++;
        batch.UpdatedAtUtc = nowUtc;
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkFailedAsync(Guid batchId, string failureCode, bool retryable, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var batch = await database.PrivateKnowledgeIngestBatches.SingleAsync(x => x.Id == batchId, cancellationToken);
        batch.Status = retryable ? "Retryable" : "Failed";
        batch.FailureCode = failureCode[..Math.Min(failureCode.Length, 128)];
        batch.Version++;
        batch.UpdatedAtUtc = nowUtc;
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task<PrivateKnowledgeIngestBatch> RetryAsync(
        Guid batchId,
        int expectedVersion,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var batch = await database.PrivateKnowledgeIngestBatches
            .SingleOrDefaultAsync(x => x.Id == batchId, cancellationToken)
            ?? throw new KeyNotFoundException();
        if (batch.Version != expectedVersion)
        {
            throw new PrivateKnowledgeIngestConcurrencyException();
        }
        if (batch.Status is not ("Failed" or "Retryable"))
        {
            throw new PrivateKnowledgeIngestRetryException("private_knowledge_ingest_retry_not_allowed");
        }
        if (!await database.ConversationMessages.AnyAsync(
                x => x.Id == batch.SourceConversationMessageId,
                cancellationToken))
        {
            throw new PrivateKnowledgeIngestRetryException("private_knowledge_ingest_source_missing");
        }

        var job = await database.DurableJobs.SingleOrDefaultAsync(
            x => x.Id == batch.Id && x.JobType == "ProcessPrivateKnowledgeIngest",
            cancellationToken);
        if (job is null)
        {
            job = new DurableJobEntity
            {
                Id = batch.Id,
                JobType = "ProcessPrivateKnowledgeIngest",
                RelatedConversationMessageId = batch.SourceConversationMessageId
            };
            database.DurableJobs.Add(job);
        }
        job.PayloadJson = JsonSerializer.Serialize(new { BatchId = batch.Id });
        job.Status = "pending";
        job.AttemptCount = 0;
        job.AvailableAtUtc = nowUtc;
        job.NextAttemptAtUtc = nowUtc;
        job.LeaseOwner = null;
        job.LeaseExpiresAtUtc = null;
        job.CompletedAtUtc = null;
        job.Version++;
        job.UpdatedAtUtc = nowUtc;

        batch.Status = "Received";
        batch.FailureCode = null;
        batch.Version++;
        batch.UpdatedAtUtc = nowUtc;
        await database.SaveChangesAsync(cancellationToken);
        return Map(batch);
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim())));

    private static PrivateKnowledgeIngestBatch Map(PrivateKnowledgeIngestBatchEntity x) =>
        new(x.Id, x.RobotConfigId, x.SourceConversationMessageId, x.RoomType, x.SourceActorDisplayName,
            Enum.Parse<PrivateKnowledgeIngestStatus>(x.Status), x.TotalCount, x.NewCount, x.DuplicateCount,
            x.SupplementCount, x.CorrectionCount, x.FailureCode, x.Version, x.CreatedAtUtc, x.UpdatedAtUtc);
}
