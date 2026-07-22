using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Handoffs;
using WechatRobot.Application.Jobs;
using WechatRobot.Infrastructure.Persistence;

namespace WechatRobot.Infrastructure.Knowledge;

public sealed class KnowledgeCandidatePublishProcessor(WechatRobotDbContext db, QdrantKnowledgeService knowledge, TimeProvider timeProvider)
{
    public async Task ProcessAsync(LeasedDurableJob job, CancellationToken token)
    {
        if (job.JobType != "PublishKnowledgeCandidate") throw new InvalidOperationException("Unsupported candidate publish job.");
        var payload = JsonSerializer.Deserialize<Payload>(job.PayloadJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Candidate publish payload is invalid.");
        var candidate = await db.KnowledgeCandidates.AsNoTracking().SingleAsync(x => x.Id == payload.CandidateId, token);
        if (candidate.Status == "published") return;
        var active = await db.KnowledgeDocumentVersions.AsNoTracking()
            .AnyAsync(x => x.Id == payload.VersionId && x.IsPublished && x.Status == "active", token);
        if (active)
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            await db.KnowledgeCandidates.Where(x => x.Id == payload.CandidateId && x.Status != "published" &&
                    db.DurableJobs.Any(jobRow => jobRow.Id == job.Id && jobRow.Status == "leased" && jobRow.LeaseOwner == job.LeaseOwner))
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, "published").SetProperty(x => x.PublishedAtUtc, now)
                    .SetProperty(x => x.Version, x => x.Version + 1).SetProperty(x => x.UpdatedAtUtc, now), token);
            return;
        }
        await knowledge.QueueCandidateIndexAsync(payload.CandidateId, payload.DocumentId, payload.VersionId, payload.TagIds, job.LeaseOwner, token);
    }

    private sealed record Payload(Guid CandidateId, Guid DocumentId, Guid VersionId, Guid[] TagIds);
}
