using System.Data;
using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Memory;
using WechatRobot.Domain.Memory;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.Infrastructure.Memory;

public sealed class EfMemoryStore(WechatRobotDbContext database) : IMemoryStore
{
    public async Task<IReadOnlyList<ActiveMemorySummary>> FindActiveAsync(
        MemoryScope scope,
        MemoryType type,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var scopeType = scope.Type.ToString();
        var memoryType = type.ToString();
        return await database.MemoryEntries.AsNoTracking()
            .Where(x => x.Status == "active" &&
                        x.ScopeType == scopeType &&
                        x.RobotConfigId == scope.RobotConfigId &&
                        x.GroupProfileId == scope.GroupProfileId &&
                        x.SubjectKey == scope.SubjectKey &&
                        x.MemoryType == memoryType)
            .OrderByDescending(x => x.UpdatedAtUtc)
            .Take(Math.Clamp(limit, 1, 20))
            .Select(x => new ActiveMemorySummary(x.Id, x.Content))
            .ToArrayAsync(cancellationToken);
    }

    public async Task<MemoryOrganizationResult> ObserveAsync(
        MemoryCandidateDraft draft,
        MemoryObservationDraft observation,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var scopeType = draft.Scope.Type.ToString();
        var scopeHash = ScopeHash(draft);
        var memoryType = draft.Type.ToString();
        var candidate = await database.MemoryCandidates.SingleOrDefaultAsync(
            x => x.ScopeHash == scopeHash &&
                 x.MemoryType == memoryType &&
                 x.Fingerprint == draft.Fingerprint,
            cancellationToken);

        if (candidate is null)
        {
            candidate = new MemoryCandidateEntity
            {
                ScopeType = scopeType,
                ScopeHash = scopeHash,
                RobotConfigId = draft.Scope.RobotConfigId,
                GroupProfileId = draft.Scope.GroupProfileId,
                SubjectKey = draft.Scope.SubjectKey,
                SubjectDisplayName = draft.Scope.SubjectDisplayName,
                MemoryType = memoryType,
                Content = draft.Content,
                NormalizedKey = draft.NormalizedKey,
                Fingerprint = draft.Fingerprint,
                Confidence = draft.Confidence,
                IsExplicit = draft.IsExplicit,
                HasUnresolvedConflict = draft.HasUnresolvedConflict,
                Status = "pending",
                CreatedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc
            };
            database.MemoryCandidates.Add(candidate);
            await database.SaveChangesAsync(cancellationToken);
        }
        else
        {
            candidate.Confidence = Math.Max(candidate.Confidence, draft.Confidence);
            candidate.IsExplicit |= draft.IsExplicit;
            candidate.HasUnresolvedConflict |= draft.HasUnresolvedConflict;
            candidate.SubjectDisplayName = draft.Scope.SubjectDisplayName ?? candidate.SubjectDisplayName;
            candidate.UpdatedAtUtc = nowUtc;
        }

        var observationExists = await database.MemoryObservations.AnyAsync(
            x => x.MemoryCandidateId == candidate.Id &&
                 x.ConversationMessageId == observation.ConversationMessageId,
            cancellationToken);

        if (!observationExists)
        {
            database.MemoryObservations.Add(new MemoryObservationEntity
            {
                MemoryCandidateId = candidate.Id,
                ConversationSessionId = observation.ConversationSessionId,
                ConversationMessageId = observation.ConversationMessageId,
                SourceContentHash = observation.SourceContentHash,
                EvidenceSummary = observation.EvidenceSummary,
                ObservedAtUtc = observation.ObservedAtUtc,
                ModelConfigurationId = observation.ModelConfigurationId,
                CreatedAtUtc = nowUtc
            });
            await database.SaveChangesAsync(cancellationToken);
        }

        var observations = database.MemoryObservations.Where(x => x.MemoryCandidateId == candidate.Id);
        candidate.ObservationCount = await observations.CountAsync(cancellationToken);
        candidate.DistinctSessionCount = await observations
            .Select(x => x.ConversationSessionId)
            .Distinct()
            .CountAsync(cancellationToken);
        candidate.DistinctDayCount = await observations
            .Select(x => x.ObservedAtUtc.Date)
            .Distinct()
            .CountAsync(cancellationToken);
        candidate.Status = candidate.ObservationCount == 0 ? "pending" : "accumulating";
        candidate.Version++;

        Guid? entryId = candidate.PromotedMemoryEntryId;
        Guid? knowledgeCandidateId = candidate.KnowledgeCandidateId;
        if (draft.Type is MemoryType.BusinessFact)
        {
            knowledgeCandidateId ??= await RouteToKnowledgeAsync(candidate, observation, nowUtc, cancellationToken);
            candidate.KnowledgeCandidateId = knowledgeCandidateId;
            candidate.Status = "routed_to_knowledge";
        }
        else if (entryId is null && MemoryPromotionPolicy.CanPromote(
                     draft.Type,
                     candidate.IsExplicit,
                     candidate.Confidence,
                     candidate.DistinctSessionCount,
                     candidate.DistinctDayCount,
                     candidate.HasUnresolvedConflict))
        {
            var entry = new MemoryEntryEntity
            {
                ScopeType = candidate.ScopeType,
                RobotConfigId = candidate.RobotConfigId,
                GroupProfileId = candidate.GroupProfileId,
                SubjectKey = candidate.SubjectKey,
                SubjectDisplayName = candidate.SubjectDisplayName,
                MemoryType = candidate.MemoryType,
                Content = candidate.Content,
                NormalizedKey = candidate.NormalizedKey,
                Confidence = candidate.Confidence,
                Status = "active",
                SupersedesMemoryEntryId = draft.SupersedesMemoryEntryId,
                SourceCandidateId = candidate.Id,
                ValidFromUtc = nowUtc,
                StatusVersion = 1,
                Version = 1,
                CreatedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc
            };
            database.MemoryEntries.Add(entry);
            if (draft.SupersedesMemoryEntryId is { } supersededId)
            {
                var superseded = await database.MemoryEntries.SingleOrDefaultAsync(
                    x => x.Id == supersededId && x.Status == "active",
                    cancellationToken);
                if (superseded is not null)
                {
                    superseded.Status = "superseded";
                    superseded.StatusVersion++;
                    superseded.Version++;
                    superseded.UpdatedAtUtc = nowUtc;
                    database.MemoryAudits.Add(NewAudit(
                        "supersede",
                        "entry",
                        superseded.Id,
                        "active",
                        "superseded",
                        superseded.Version,
                        "explicit_memory_conflict",
                        nowUtc));
                }
            }
            database.DurableJobs.Add(new DurableJobEntity
            {
                Id = DeterministicGuid($"index-memory:{entry.Id:D}"),
                JobType = "IndexMemoryEntry",
                GroupProfileId = entry.GroupProfileId,
                PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { memoryEntryId = entry.Id }),
                Status = "pending",
                AvailableAtUtc = nowUtc,
                NextAttemptAtUtc = nowUtc,
                CreatedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc
            });
            entryId = entry.Id;
            candidate.PromotedMemoryEntryId = entry.Id;
            candidate.Status = "promoted";
            database.MemoryAudits.Add(NewAudit(
                "promote",
                "candidate",
                candidate.Id,
                "accumulating",
                "promoted",
                candidate.Version,
                "promotion_policy_satisfied",
                nowUtc));
        }

        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new MemoryOrganizationResult(candidate.Id, candidate.Status, entryId, knowledgeCandidateId);
    }

    private async Task<Guid> RouteToKnowledgeAsync(
        MemoryCandidateEntity candidate,
        MemoryObservationDraft observation,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var existing = await database.KnowledgeCandidates.SingleOrDefaultAsync(
            x => x.SourceMemoryCandidateId == candidate.Id,
            cancellationToken);
        if (existing is not null)
        {
            return existing.Id;
        }

        var knowledge = new KnowledgeCandidateEntity
        {
            HandoffCaseId = null,
            QuestionMessageId = observation.ConversationMessageId,
            SourceConversationMessageId = observation.ConversationMessageId,
            SourceMemoryCandidateId = candidate.Id,
            SourceType = "MemoryExtraction",
            Question = $"请核实并整理此业务事实：{candidate.Content}",
            Answer = candidate.Content,
            EvidenceJson = "{}",
            Status = "pending",
            Version = 1,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc
        };
        database.KnowledgeCandidates.Add(knowledge);
        database.MemoryAudits.Add(NewAudit(
            "route_to_knowledge",
            "candidate",
            candidate.Id,
            "accumulating",
            "routed_to_knowledge",
            candidate.Version,
            "business_fact_requires_review",
            nowUtc));
        return knowledge.Id;
    }

    private static MemoryAuditEntity NewAudit(
        string action,
        string targetType,
        Guid targetId,
        string? oldStatus,
        string? newStatus,
        int version,
        string reasonCode,
        DateTime nowUtc) => new()
        {
            Action = action,
            ActorType = "system",
            TargetType = targetType,
            TargetId = targetId,
            OldStatus = oldStatus,
            NewStatus = newStatus,
            Version = version,
            ReasonCode = reasonCode,
            CreatedAtUtc = nowUtc
        };

    private static Guid DeterministicGuid(string value)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static string ScopeHash(MemoryCandidateDraft draft)
    {
        var value = $"{draft.Scope.Type}|{draft.Scope.RobotConfigId}|{draft.Scope.GroupProfileId}|{draft.Scope.SubjectKey}";
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value)));
    }
}
