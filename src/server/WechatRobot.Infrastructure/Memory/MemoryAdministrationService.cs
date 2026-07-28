using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using WechatRobot.Domain.Memory;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.Infrastructure.Memory;

public sealed partial class MemoryAdministrationService(
    WechatRobotDbContext database,
    TimeProvider timeProvider)
{
    public async Task<MemoryCandidateEntity> UpdateCandidateAsync(
        Guid id,
        string content,
        double confidence,
        int expectedVersion,
        string actor,
        CancellationToken cancellationToken)
    {
        var candidate = await LoadCandidateAsync(id, cancellationToken);
        EnsureVersion(candidate.Version, expectedVersion);
        var normalized = NormalizeAndValidate(content);
        candidate.Content = content.Trim();
        candidate.NormalizedKey = normalized;
        candidate.Fingerprint = Hash($"{candidate.ScopeType}|{candidate.RobotConfigId}|{candidate.GroupProfileId}|{candidate.SubjectKey}|{candidate.MemoryType}|{normalized}");
        candidate.Confidence = Math.Clamp(confidence, 0, 1);
        candidate.Version++;
        candidate.UpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        AddAudits(actor, "edit", "candidate", candidate.Id, candidate.Status, candidate.Status, candidate.Version, "administrator_edit");
        await database.SaveChangesAsync(cancellationToken);
        return candidate;
    }

    public async Task<MemoryCandidateEntity> RejectCandidateAsync(
        Guid id,
        int expectedVersion,
        string actor,
        CancellationToken cancellationToken)
    {
        var candidate = await LoadCandidateAsync(id, cancellationToken);
        EnsureVersion(candidate.Version, expectedVersion);
        if (candidate.Status is "promoted" or "routed_to_knowledge")
            throw new InvalidOperationException("The candidate has already left the pending workflow.");
        var old = candidate.Status;
        candidate.Status = "rejected";
        candidate.Version++;
        candidate.UpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        AddAudits(actor, "reject", "candidate", id, old, candidate.Status, candidate.Version, "administrator_rejected");
        await database.SaveChangesAsync(cancellationToken);
        return candidate;
    }

    public async Task<MemoryEntryEntity> PromoteCandidateAsync(
        Guid id,
        int expectedVersion,
        string actor,
        CancellationToken cancellationToken)
    {
        var candidate = await LoadCandidateAsync(id, cancellationToken);
        EnsureVersion(candidate.Version, expectedVersion);
        if (candidate.MemoryType == nameof(MemoryType.BusinessFact))
            throw new InvalidOperationException("BusinessFact must be approved through knowledge learning review.");
        if (candidate.PromotedMemoryEntryId is not null)
            return await database.MemoryEntries.SingleAsync(x => x.Id == candidate.PromotedMemoryEntryId, cancellationToken);
        if (candidate.Status is "rejected" or "expired")
            throw new InvalidOperationException("The candidate cannot be promoted from its current status.");

        var now = timeProvider.GetUtcNow().UtcDateTime;
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
            SourceCandidateId = candidate.Id,
            ValidFromUtc = now,
            StatusVersion = 1,
            IndexGeneration = 1,
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        database.MemoryEntries.Add(entry);
        candidate.PromotedMemoryEntryId = entry.Id;
        var old = candidate.Status;
        candidate.Status = "promoted";
        candidate.Version++;
        candidate.UpdatedAtUtc = now;
        EnqueueIndex(entry, now);
        AddAudits(actor, "promote", "candidate", candidate.Id, old, candidate.Status, candidate.Version, "administrator_promoted");
        await database.SaveChangesAsync(cancellationToken);
        return entry;
    }

    public async Task<MemoryEntryEntity> ChangeEntryStatusAsync(
        Guid id,
        string targetStatus,
        int expectedVersion,
        string actor,
        CancellationToken cancellationToken)
    {
        var entry = await database.MemoryEntries.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException();
        EnsureVersion(entry.Version, expectedVersion);
        var allowed = targetStatus switch
        {
            "forgotten" => entry.Status == "active",
            "active" => entry.Status is "forgotten" or "expired",
            _ => false
        };
        if (!allowed) throw new InvalidOperationException("The requested memory status transition is invalid.");

        var old = entry.Status;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        entry.Status = targetStatus;
        entry.StatusVersion++;
        entry.Version++;
        entry.UpdatedAtUtc = now;
        if (targetStatus == "active")
            EnqueueIndex(entry, now);
        else
            EnqueueRemove(entry, now);
        AddAudits(actor, targetStatus == "active" ? "restore" : "forget", "entry", id, old, targetStatus, entry.Version,
            targetStatus == "active" ? "administrator_restored" : "administrator_forgot");
        await database.SaveChangesAsync(cancellationToken);
        return entry;
    }

    public async Task RetryJobAsync(
        Guid id,
        int expectedVersion,
        string actor,
        CancellationToken cancellationToken)
    {
        var job = await database.DurableJobs.SingleOrDefaultAsync(
            x => x.Id == id &&
                 (x.JobType == "ExtractConversationMemory" ||
                  x.JobType == "IndexMemoryEntry" ||
                  x.JobType == "RemoveMemoryEntryFromIndex" ||
                  x.JobType == "MaintainLongTermMemory"),
            cancellationToken) ?? throw new KeyNotFoundException();
        EnsureVersion(job.Version, expectedVersion);
        if (job.Status is not ("retrying" or "deadLetter"))
            throw new InvalidOperationException("Only retrying or dead-letter memory jobs can be retried.");
        var now = timeProvider.GetUtcNow().UtcDateTime;
        job.Status = "pending";
        job.AttemptCount = 0;
        job.NextAttemptAtUtc = now;
        job.AvailableAtUtc = now;
        job.LeaseOwner = null;
        job.LeaseExpiresAtUtc = null;
        job.CompletedAtUtc = null;
        job.Version++;
        job.UpdatedAtUtc = now;
        database.AdministrationAudits.Add(new AdministrationAuditEntity
        {
            Actor = actor,
            Action = "memory.job.retry",
            TargetType = "DurableJob",
            TargetId = id.ToString("D"),
            SanitizedDetailJson = """{"reason":"administrator_retry"}""",
            CreatedAtUtc = now
        });
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task ReorganizeCandidateAsync(
        Guid id,
        int expectedVersion,
        string actor,
        CancellationToken cancellationToken)
    {
        var candidate = await LoadCandidateAsync(id, cancellationToken);
        EnsureVersion(candidate.Version, expectedVersion);
        var observation = await database.MemoryObservations.AsNoTracking()
            .Where(x => x.MemoryCandidateId == id)
            .OrderByDescending(x => x.ObservedAtUtc)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("The candidate has no source observation.");
        var message = await database.ConversationMessages.AsNoTracking()
            .SingleAsync(x => x.Id == observation.ConversationMessageId, cancellationToken);
        var model = await database.ModelConfigs.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == observation.ModelConfigurationId, cancellationToken)
            ?? throw new InvalidOperationException("The original model configuration is unavailable.");
        if (message.GroupProfileId is null)
            throw new InvalidOperationException("The source conversation is not attached to a group.");
        var now = timeProvider.GetUtcNow().UtcDateTime;
        database.DurableJobs.Add(new DurableJobEntity
        {
            JobType = "ExtractConversationMemory",
            GroupProfileId = message.GroupProfileId,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                messageId = message.Id,
                conversationSessionId = observation.ConversationSessionId,
                groupProfileId = message.GroupProfileId,
                modelConfigurationId = model.Id,
                modelConfigurationVersion = model.Version,
                explicitRequest = true
            }),
            Status = "pending",
            AvailableAtUtc = now,
            NextAttemptAtUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        candidate.Version++;
        candidate.UpdatedAtUtc = now;
        AddAudits(actor, "reorganize", "candidate", candidate.Id, candidate.Status, candidate.Status,
            candidate.Version, "administrator_reorganized");
        await database.SaveChangesAsync(cancellationToken);
    }

    private async Task<MemoryCandidateEntity> LoadCandidateAsync(Guid id, CancellationToken cancellationToken) =>
        await database.MemoryCandidates.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
        ?? throw new KeyNotFoundException();

    private void AddAudits(
        string actor,
        string action,
        string targetType,
        Guid targetId,
        string? oldStatus,
        string? newStatus,
        int version,
        string reason)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        database.MemoryAudits.Add(new MemoryAuditEntity
        {
            Action = action,
            ActorType = "administrator",
            TargetType = targetType,
            TargetId = targetId,
            OldStatus = oldStatus,
            NewStatus = newStatus,
            Version = version,
            ReasonCode = reason,
            CreatedAtUtc = now
        });
        database.AdministrationAudits.Add(new AdministrationAuditEntity
        {
            Actor = actor,
            Action = $"memory.{targetType}.{action}",
            TargetType = targetType,
            TargetId = targetId.ToString("D"),
            SanitizedDetailJson = System.Text.Json.JsonSerializer.Serialize(new { oldStatus, newStatus, version, reason }),
            CreatedAtUtc = now
        });
    }

    private void EnqueueIndex(MemoryEntryEntity entry, DateTime now) =>
        database.DurableJobs.Add(new DurableJobEntity
        {
            Id = Guid.NewGuid(),
            JobType = "IndexMemoryEntry",
            GroupProfileId = entry.GroupProfileId,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { memoryEntryId = entry.Id }),
            Status = "pending",
            AvailableAtUtc = now,
            NextAttemptAtUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });

    private void EnqueueRemove(MemoryEntryEntity entry, DateTime now) =>
        database.DurableJobs.Add(new DurableJobEntity
        {
            Id = Guid.NewGuid(),
            JobType = "RemoveMemoryEntryFromIndex",
            GroupProfileId = entry.GroupProfileId,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { memoryEntryId = entry.Id }),
            Status = "pending",
            AvailableAtUtc = now,
            NextAttemptAtUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });

    private static void EnsureVersion(int actual, int expected)
    {
        if (actual != expected) throw new MemoryConcurrencyException();
    }

    private static string NormalizeAndValidate(string content)
    {
        if (string.IsNullOrWhiteSpace(content) || content.Trim().Length > 1000 || SecretPattern().IsMatch(content))
            throw new ArgumentException("Memory content is invalid or contains secret-like material.");
        return Whitespace().Replace(content.Trim().Normalize(NormalizationForm.FormKC), " ").ToLowerInvariant();
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"(?ix)(password\s*[:=]|api[_ -]?key\s*[:=]|access[_ -]?key|bearer\s+|验证码|connection\s*string|server\s*=.+password\s*=)")]
    private static partial Regex SecretPattern();
}

public sealed class MemoryConcurrencyException : Exception;
