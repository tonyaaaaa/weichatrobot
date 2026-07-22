using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;
using WechatRobot.Application.Conversations;
using WechatRobot.Application.Groups;
using WechatRobot.Application.Models;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.Infrastructure.Conversations;

public sealed class GroundedConversationRepository(
    WechatRobotDbContext database,
    ModelConfigurationService modelConfigurations,
    TimeProvider timeProvider) : IGroundedConversationRepository
{
    private const int MaximumSummaryCandidateRows = 32;
    public async Task<ConversationProcessingRequest> LeaseForProcessingAsync(Guid messageId, string leaseOwner, DateTime nowUtc, TimeSpan leaseDuration, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(leaseOwner) || leaseOwner.Length > 128 || leaseDuration <= TimeSpan.Zero)
            throw new ArgumentException("Conversation session lease is invalid.");
        var initial = await LoadForProcessingAsync(messageId, token);
        var session = await database.ConversationSessions.SingleOrDefaultAsync(item => item.GroupProfileId == initial.GroupProfileId && item.SenderScopeKey == initial.Scope.ScopeKey, token);
        if (session is null)
        {
            session = new ConversationSessionEntity
            {
                GroupProfileId = initial.GroupProfileId, SenderScopeKey = initial.Scope.ScopeKey,
                LastActivityAtUtc = initial.ReceivedAtUtc, CreatedAtUtc = nowUtc, UpdatedAtUtc = nowUtc
            };
            database.ConversationSessions.Add(session);
            try { await database.SaveChangesAsync(token); }
            catch (DbUpdateException exception) when (exception.InnerException is MySqlException { Number: 1062 })
            {
                database.ChangeTracker.Clear();
                session = await database.ConversationSessions.SingleAsync(item => item.GroupProfileId == initial.GroupProfileId && item.SenderScopeKey == initial.Scope.ScopeKey, token);
            }
        }

        await using (var canonicalTransaction = await database.Database.BeginTransactionAsync(token))
        {
            var assigned = await database.ConversationMessages.Where(item => item.Id == messageId &&
                    (item.GroupProfileId == null || item.GroupProfileId == initial.GroupProfileId))
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.GroupProfileId, initial.GroupProfileId)
                    .SetProperty(item => item.ConversationSessionId, session.Id), token);
            if (assigned != 1) throw new InvalidOperationException("Inbound message canonical group identity conflicts with the resolved group profile.");
            var aliases = await database.GroupProfiles.AsNoTracking().Where(item => item.Id == initial.GroupProfileId)
                .Select(item => new { item.Name, item.ExternalGroupId }).SingleAsync(token);
            await database.ConversationMessages.Where(item => item.GroupProfileId == null && item.RobotConfigId == initial.RobotConfigId &&
                    (item.GroupName == aliases.Name || item.GroupName == aliases.ExternalGroupId))
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.GroupProfileId, initial.GroupProfileId), token);
            await canonicalTransaction.CommitAsync(token);
        }

        if (!initial.Scope.IsStatelessDegradation)
        {
            var current = await database.ConversationMessages.AsNoTracking().SingleAsync(item => item.Id == messageId, token);
            var earlier = await database.ConversationMessages.AsNoTracking().Where(item => item.Direction == "inbound" &&
                    item.GroupProfileId == initial.GroupProfileId && item.Id != current.Id &&
                    (item.ReceivedAtUtc < current.ReceivedAtUtc || item.ReceivedAtUtc == current.ReceivedAtUtc && item.Id.CompareTo(current.Id) < 0) &&
                    (item.ProcessingState == "pending" || item.ProcessingState == "retrying" || item.ProcessingState == "leased"))
                .Select(item => new { item.Id, item.StableSenderId }).ToArrayAsync(token);
            if (earlier.Any(item => ConversationScopeResolver.Resolve(initial.ContextPolicy.SenderIsolated, item.StableSenderId, item.Id).ScopeKey == initial.Scope.ScopeKey))
                throw new ConversationSessionBusyException("An earlier message in this canonical conversation session is still active.");
        }

        var changed = await database.ConversationSessions.Where(item => item.Id == session.Id &&
                (item.LeaseOwner == null || item.LeaseExpiresAtUtc <= nowUtc || item.LeaseOwner == leaseOwner))
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.LeaseOwner, leaseOwner)
                .SetProperty(item => item.LeaseExpiresAtUtc, nowUtc.Add(leaseDuration)).SetProperty(item => item.Version, item => item.Version + 1)
                .SetProperty(item => item.UpdatedAtUtc, nowUtc), token);
        if (changed != 1) throw new ConversationSessionBusyException("The conversation session is leased by another worker.");
        var leased = await LoadForProcessingAsync(messageId, token);
        var state = await database.ConversationSessions.AsNoTracking().SingleAsync(item => item.Id == session.Id, token);
        return leased with { ConversationSessionId = session.Id, SessionLeaseOwner = leaseOwner, SessionVersion = state.Version };
    }

    public async Task<bool> RenewLeaseAsync(Guid sessionId, string leaseOwner, DateTime nowUtc, TimeSpan leaseDuration, CancellationToken token)
    {
        var changed = await database.ConversationSessions.Where(item => item.Id == sessionId && item.LeaseOwner == leaseOwner && item.LeaseExpiresAtUtc > nowUtc)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.LeaseExpiresAtUtc, nowUtc.Add(leaseDuration))
                .SetProperty(item => item.Version, item => item.Version + 1).SetProperty(item => item.UpdatedAtUtc, nowUtc), token);
        return changed == 1;
    }

    public async Task ReleaseLeaseAsync(Guid sessionId, string leaseOwner, CancellationToken token) =>
        _ = await database.ConversationSessions.Where(item => item.Id == sessionId && item.LeaseOwner == leaseOwner)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.LeaseOwner, (string?)null).SetProperty(item => item.LeaseExpiresAtUtc, (DateTime?)null)
                .SetProperty(item => item.Version, item => item.Version + 1), token);

    public async Task<ConversationProcessingRequest> LoadForProcessingAsync(Guid messageId, CancellationToken token)
    {
        var message = await database.ConversationMessages.AsNoTracking().SingleOrDefaultAsync(item => item.Id == messageId, token)
            ?? throw new KeyNotFoundException("Inbound conversation message was not found.");
        if (message.Direction != "inbound") throw new InvalidOperationException("Only inbound messages can be processed.");
        var robot = await database.RobotConfigs.AsNoTracking().SingleAsync(item => item.Id == message.RobotConfigId, token);
        var groupName = message.GroupName;
        if (string.IsNullOrWhiteSpace(groupName))
        {
            var payload = await database.DurableJobs.AsNoTracking().Where(job => job.JobType == "ProcessInboundMessage" && job.PayloadJson.Contains(messageId.ToString()))
                .Select(job => job.PayloadJson).FirstOrDefaultAsync(token);
            if (payload is not null) groupName = JsonDocument.Parse(payload).RootElement.GetProperty("GroupName").GetString() ?? string.Empty;
        }
        var group = message.GroupProfileId is { } groupId
            ? await database.GroupProfiles.AsNoTracking().SingleOrDefaultAsync(item => item.Id == groupId && item.IsEnabled, token)
            : await database.GroupProfiles.AsNoTracking().Where(item => item.RobotConfigId == message.RobotConfigId && item.IsEnabled &&
                    (item.ExternalGroupId == groupName || item.Name == groupName))
                .FirstOrDefaultAsync(token);
        if (group is null) throw new InvalidOperationException("No enabled group profile matches the inbound message.");

        var policy = new GroupConfigurationService().GetEffectiveContext(new(group.ContextSenderIsolated, group.ContextHistoryTurns,
            group.ContextIdleTimeoutMinutes, group.ContextTokenCap, group.ContextSummaryEnabled, group.ContextIncludeBotHistory));
        var scope = ConversationScopeResolver.Resolve(policy.SenderIsolated, message.StableSenderId, message.Id);
        var session = await database.ConversationSessions.AsNoTracking().SingleOrDefaultAsync(item => item.GroupProfileId == group.Id && item.SenderScopeKey == scope.ScopeKey, token);
        var policyRows = policy.IncludeBotHistory ? Math.Max(0, policy.HistoryTurns * 2) : Math.Max(0, policy.HistoryTurns);
        var maximumRows = Math.Min(232, policyRows + (policy.SummaryEnabled ? MaximumSummaryCandidateRows : 0));
        var history = session is null || maximumRows == 0 ? [] : (await database.ConversationMessages.AsNoTracking()
            .Where(item => item.ConversationSessionId == session.Id && item.Id != message.Id &&
                (item.SessionSequence != null
                    ? item.SessionSequence > session.ClearedThroughSequence
                    : session.ClearedAtUtc == null || item.CreatedAtUtc > session.ClearedAtUtc))
            .Where(item => item.Direction != "inbound" || item.ProcessingState == "completed")
            .Where(item => policy.IncludeBotHistory || item.Role == "user")
            .OrderByDescending(item => item.SessionSequence ?? 0).ThenByDescending(item => item.Id).Take(maximumRows)
            .Select(item => new ConversationHistoryMessage(item.Role, scope.ScopeKey, item.Text, item.CreatedAtUtc, item.Id, item.SessionSequence)).ToArrayAsync(token))
            .Reverse().ToArray();
        var summary = session?.Summary;
        var allowedTags = await database.GroupProfileTags.AsNoTracking().Where(item => item.GroupProfileId == group.Id)
            .Select(item => item.KnowledgeTagId).ToArrayAsync(token);
        var config = await database.ModelConfigs.AsNoTracking().Where(item => item.ConfigurationType == "chat" && item.IsEnabled)
            .OrderByDescending(item => item.IsDefault).ThenBy(item => item.CreatedAtUtc).FirstOrDefaultAsync(token)
            ?? throw new InvalidOperationException("No enabled chat model configuration exists.");
        var provider = modelConfigurations.ToProviderConfiguration(new(config.Id, config.Name, config.Provider, config.BaseUrl, config.Model,
            config.EncryptedApiKey, config.TimeoutSeconds, config.MaxRetries, config.IsEnabled, config.IsDefault));
        return new(message.Id, message.RobotConfigId, robot.WorkToolRobotId, group.Id, group.Name, message.SenderDisplayName, message.StableSenderId, scope,
            message.Text, message.ReceivedAtUtc, allowedTags, history, summary, policy, provider, config.Id);
    }

    public async Task PersistAnswerAndEnqueueAsync(ConversationProcessingRequest request, GroundedAnswerResult result, CancellationToken token)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        if (await database.RetrievalAudits.AnyAsync(item => item.ConversationMessageId == request.MessageId, token))
        {
            await database.ConversationMessages.Where(item => item.Id == request.MessageId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ProcessingState, "completed"), token);
            if (request.ConversationSessionId != Guid.Empty && request.SessionLeaseOwner is not null)
                await database.ConversationSessions.Where(item => item.Id == request.ConversationSessionId && item.LeaseOwner == request.SessionLeaseOwner)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.LeaseOwner, (string?)null)
                        .SetProperty(item => item.LeaseExpiresAtUtc, (DateTime?)null).SetProperty(item => item.Version, item => item.Version + 1), token);
            await transaction.CommitAsync(token);
            return;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var inbound = await database.ConversationMessages.SingleAsync(item => item.Id == request.MessageId, token);
        inbound.GroupProfileId = request.GroupProfileId;
        inbound.ProcessingState = "completed";
        if (request.ConversationSessionId == Guid.Empty || string.IsNullOrWhiteSpace(request.SessionLeaseOwner))
            throw new ConversationSessionOwnershipLostException("No owned conversation session lease is attached to the request.");
        var sequenceState = await database.ConversationSessions.AsNoTracking()
            .Where(item => item.Id == request.ConversationSessionId && item.LeaseOwner == request.SessionLeaseOwner)
            .Select(item => new { item.NextSequence }).SingleOrDefaultAsync(token)
            ?? throw new ConversationSessionOwnershipLostException("Conversation session lease ownership was lost before sequence allocation.");
        inbound.ConversationSessionId = request.ConversationSessionId;
        inbound.SessionSequence = sequenceState.NextSequence + 1;
        var outbound = new ConversationMessageEntity
        {
            RobotConfigId = request.RobotConfigId,
            GroupProfileId = request.GroupProfileId,
            ConversationSessionId = request.ConversationSessionId,
            SessionSequence = sequenceState.NextSequence + 2,
            GroupName = request.GroupName,
            Direction = "outbound",
            Role = "assistant",
            InReplyToMessageId = request.MessageId,
            FallbackHash = $"outbound:{request.MessageId:D}",
            FallbackWindowStartUtc = DateTime.UnixEpoch,
            SenderDisplayName = request.SenderDisplayName,
            StableSenderId = request.StableSenderId,
            Text = result.Decision.GroupText,
            ReceivedAtUtc = now,
            CreatedAtUtc = now
        };
        database.ConversationMessages.Add(outbound);
        database.RetrievalAudits.Add(new RetrievalAuditEntity
        {
            ConversationMessageId = request.MessageId,
            GroupProfileId = request.GroupProfileId,
            Decision = result.Audit.Decision,
            ConfidenceThreshold = result.Audit.ConfidenceThreshold,
            ConfidenceValue = result.Audit.ConfidenceValue,
            ContextPolicy = result.Audit.ContextPolicy,
            FailureCode = result.Audit.FailureCode,
            EvidenceJson = JsonSerializer.Serialize(result.Audit.Evidence.Select(item => new
            {
                item.DocumentId, item.VersionId, item.ChunkId, item.PageNumber, item.Similarity, item.TagIds, item.DocumentTitle,
                item.SourceFileName, item.SourceUri
            })),
            InputSummaryJson = result.Audit.InputSummaryJson,
            CreatedAtUtc = now
        });
        database.SendCommands.Add(new SendCommandEntity
        {
            RobotConfigId = request.RobotConfigId,
            GroupProfileId = request.GroupProfileId,
            IdempotencyKey = $"grounded-reply:{request.MessageId:D}",
            PayloadJson = JsonSerializer.Serialize(new { request.WorkToolRobotId, request.GroupName, Text = result.Decision.GroupText }),
            NextAttemptAtUtc = now,
            CreatedAtUtc = now
        });

        var guarded = await database.ConversationSessions.Where(item => item.Id == request.ConversationSessionId && item.LeaseOwner == request.SessionLeaseOwner &&
                item.LeaseExpiresAtUtc > now && item.NextSequence == sequenceState.NextSequence)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.LeaseOwner, (string?)null).SetProperty(item => item.LeaseExpiresAtUtc, (DateTime?)null)
                .SetProperty(item => item.LastActivityAtUtc, now).SetProperty(item => item.UpdatedAtUtc, now)
                .SetProperty(item => item.NextSequence, sequenceState.NextSequence + 2)
                .SetProperty(item => item.Summary, item => result.ResetContextBeforeCurrent ? result.UpdatedSummary : result.UpdatedSummary ?? item.Summary)
                .SetProperty(item => item.ClearedThroughSequence, item => result.ResetContextBeforeCurrent ? sequenceState.NextSequence : item.ClearedThroughSequence)
                .SetProperty(item => item.ClearedAtUtc, item => result.ResetContextBeforeCurrent ? request.ReceivedAtUtc : item.ClearedAtUtc)
                .SetProperty(item => item.Version, item => item.Version + 1), token);
        if (guarded != 1)
        {
            await transaction.RollbackAsync(token);
            throw new ConversationSessionOwnershipLostException("Conversation session lease ownership was lost before commit.");
        }
        await database.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }

    public async Task<int> ClearGroupContextAsync(Guid groupProfileId, DateTime clearedAtUtc, CancellationToken token)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        var cleared = await database.ConversationSessions.Where(item => item.GroupProfileId == groupProfileId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ClearedAtUtc, clearedAtUtc)
                .SetProperty(item => item.ClearedThroughSequence, item => item.NextSequence)
                .SetProperty(item => item.Summary, (string?)null)
                .SetProperty(item => item.LeaseOwner, (string?)null)
                .SetProperty(item => item.LeaseExpiresAtUtc, (DateTime?)null)
                .SetProperty(item => item.UpdatedAtUtc, clearedAtUtc)
                .SetProperty(item => item.Version, item => item.Version + 1), token);
        if (!await database.ConversationSessions.AnyAsync(item => item.GroupProfileId == groupProfileId && item.SenderScopeKey == "group", token))
        {
            database.ConversationSessions.Add(new ConversationSessionEntity
            {
                GroupProfileId = groupProfileId, SenderScopeKey = "group", LastActivityAtUtc = clearedAtUtc,
                ClearedAtUtc = clearedAtUtc, ClearedThroughSequence = 0, CreatedAtUtc = clearedAtUtc, UpdatedAtUtc = clearedAtUtc
            });
            await database.SaveChangesAsync(token);
            cleared++;
        }
        await transaction.CommitAsync(token);
        return cleared;
    }

    public async Task<PageResult<ConversationPageItem>> GetHistoryAsync(Guid groupProfileId, int page, int pageSize, CancellationToken token)
    {
        (page, pageSize) = NormalizePage(page, pageSize);
        var query = database.ConversationMessages.AsNoTracking().Where(item => item.GroupProfileId == groupProfileId);
        var total = await query.CountAsync(token);
        var items = await query.OrderByDescending(item => item.CreatedAtUtc).ThenByDescending(item => item.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(item => new ConversationPageItem(item.Id, groupProfileId, item.ConversationSessionId, item.Direction, item.Role, item.SenderDisplayName, item.StableSenderId, item.Text, item.CreatedAtUtc)).ToArrayAsync(token);
        return new(items, total, page, pageSize);
    }

    public async Task<PageResult<RetrievalAuditPageItem>> GetAuditsAsync(Guid groupProfileId, int page, int pageSize, CancellationToken token)
    {
        (page, pageSize) = NormalizePage(page, pageSize);
        var query = database.RetrievalAudits.AsNoTracking().Where(item => item.GroupProfileId == groupProfileId);
        var total = await query.CountAsync(token);
        var items = await query.OrderByDescending(item => item.CreatedAtUtc).ThenByDescending(item => item.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(item => new RetrievalAuditPageItem(item.Id, item.ConversationMessageId, groupProfileId, item.Decision, item.ConfidenceThreshold,
                item.ConfidenceValue, item.FailureCode, item.EvidenceJson, item.CreatedAtUtc)).ToArrayAsync(token);
        return new(items, total, page, pageSize);
    }

    private static (int Page, int Size) NormalizePage(int page, int size) => (Math.Max(1, page), Math.Clamp(size, 1, 100));
}
