using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Handoffs;
using WechatRobot.Application.Jobs;
using WechatRobot.Infrastructure.Persistence;

namespace WechatRobot.Infrastructure.Knowledge;

public sealed class KnowledgeCandidatePublishProcessor(WechatRobotDbContext db, QdrantKnowledgeService knowledge)
{
    public async Task ProcessAsync(LeasedDurableJob job, CancellationToken token)
    {
        if (job.JobType != "PublishKnowledgeCandidate") throw new InvalidOperationException("Unsupported candidate publish job.");
        var payload = JsonSerializer.Deserialize<Payload>(job.PayloadJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Candidate publish payload is invalid.");
        var candidate = await db.KnowledgeCandidates.AsNoTracking().SingleAsync(x => x.Id == payload.CandidateId, token);
        if (candidate.Status == "published") return;
        var indexJobId = await knowledge.QueueIndexAsync(payload.DocumentId, payload.VersionId, payload.TagIds, false, token);
        var now = DateTime.UtcNow;
        var changed = await db.KnowledgeCandidates.Where(x => x.Id == payload.CandidateId &&
                (x.Status == "approved_pending_index" || x.Status == "indexing") &&
                db.DurableJobs.Any(jobRow => jobRow.Id == job.Id && jobRow.Status == "leased" && jobRow.LeaseOwner == job.LeaseOwner))
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, "indexing").SetProperty(x => x.Version, x => x.Version + 1)
                .SetProperty(x => x.UpdatedAtUtc, now), token);
        if (changed != 1) throw new HandoffConcurrencyException($"Candidate publish ownership was lost after queuing index job {indexJobId}.");
    }

    private sealed record Payload(Guid CandidateId, Guid DocumentId, Guid VersionId, Guid[] TagIds);
}
