using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;
using WechatRobot.Application.Conversations;
using WechatRobot.Application.Groups;
using WechatRobot.Application.Models;
using WechatRobot.Domain.Groups;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.Infrastructure.Conversations;

public sealed class GroundedConversationRepository(
    WechatRobotDbContext database,
    ModelConfigurationService modelConfigurations,
    TimeProvider timeProvider) : IGroundedConversationRepository
{
    private const int MaximumSummaryCandidateRows = 32;
    private const int MaximumPolicyCandidateGroups = 100;
    private const int MaximumPolicyRules = 10_000;

    public async Task<InboundPolicyDecision> EvaluateInboundPolicyAsync(
        Guid messageId,
        string groupName,
        string? groupRemark,
        bool wasMentioned,
        CancellationToken token)
    {
        var message = await database.ConversationMessages.AsNoTracking().SingleOrDefaultAsync(item => item.Id == messageId, token)
            ?? throw new KeyNotFoundException("Inbound conversation message was not found.");
        var exactNames = await database.GroupProfiles.AsNoTracking()
            .Where(item => item.RobotConfigId == message.RobotConfigId &&
                           item.IsEnabled &&
                           item.Name == groupName &&
                           (item.WorkToolGroupRemark == null || item.WorkToolGroupRemark == groupRemark))
            .OrderBy(item => item.Id).Take(2).ToArrayAsync(token);
        if (exactNames.Length > 1)
            return NoReply(messageId, null, "group_identity_ambiguous", "multiple_name_and_remark_candidates");

        GroupProfileEntity[] candidates;
        var authoritativeIdentity = exactNames.Length == 1;
        if (authoritativeIdentity)
        {
            candidates = exactNames;
        }
        else
        {
            candidates = await database.GroupProfiles.AsNoTracking()
                .Where(item => item.RobotConfigId == message.RobotConfigId &&
                               item.IsEnabled &&
                               (item.WorkToolGroupRemark == null || item.WorkToolGroupRemark == groupRemark))
                .OrderBy(item => item.Id).Take(MaximumPolicyCandidateGroups + 1).ToArrayAsync(token);
            if (candidates.Length > MaximumPolicyCandidateGroups)
                return NoReply(messageId, null, "group_rule_candidate_limit", "candidate_limit_exceeded");
        }

        if (candidates.Length == 0)
            return NoReply(messageId, null, "group_rule_unmatched", "no_enabled_group");

        var candidateIds = candidates.Select(item => item.Id).ToArray();
        var candidatePredicate = GuidBatchQuery.BuildPredicate<GroupRuleEntity>(candidateIds, item => item.GroupProfileId);
        var rules = await database.GroupRules.AsNoTracking().Where(item => item.IsEnabled).Where(candidatePredicate)
            .OrderBy(item => item.GroupProfileId).ThenBy(item => item.CreatedAtUtc).ThenBy(item => item.Id)
            .Take(MaximumPolicyRules + 1).ToArrayAsync(token);
        if (rules.Length > MaximumPolicyRules)
            return NoReply(messageId, null, "group_rule_limit", "rule_limit_exceeded");

        var matches = new List<(GroupProfileEntity Group, string MatchKind)>();
        var sawExclusion = false;
        foreach (var candidate in candidates)
        {
            var candidateRules = rules.Where(item => item.GroupProfileId == candidate.Id).ToArray();
            if (candidateRules.Any(item => item.RuleKind is not 0 and not 1))
                return NoReply(messageId, candidate.Id, "group_rule_invalid", "invalid_enabled_rule_direction");
            var names = new[] { groupName };
            var includes = candidateRules.Where(item => item.RuleKind == 0).ToArray();
            var excludes = candidateRules.Where(item => item.RuleKind == 1).ToArray();
            var included = includes.Length == 0 && authoritativeIdentity;
            var embeddedExcluded = false;
            var matchKind = includes.Length == 0 ? "identity" : "rule";

            foreach (var rule in includes)
            {
                foreach (var name in names)
                {
                    var result = GroupRuleMatcher.Match(ToDomainRule(rule), name);
                    if (!result.IsValid) return NoReply(messageId, candidate.Id, "group_rule_invalid", "invalid_enabled_rule");
                    if (result.IsExcluded)
                    {
                        sawExclusion = true;
                        embeddedExcluded = true;
                    }
                    if (result.IsMatch) included = true;
                }
            }
            if (!included || embeddedExcluded) continue;

            foreach (var rule in excludes)
            {
                foreach (var name in names)
                {
                    var result = GroupRuleMatcher.Match(new GroupRule(rule.Id, rule.IncludePattern,
                        ParsePatternKind(rule.IncludePatternKind), ignoreCase: rule.IgnoreCase, isEnabled: rule.IsEnabled), name);
                    if (!result.IsValid) return NoReply(messageId, candidate.Id, "group_rule_invalid", "invalid_enabled_rule");
                    if (result.IsMatch)
                    {
                        included = false;
                        sawExclusion = true;
                        break;
                    }
                }
                if (!included) break;
            }
            if (included) matches.Add((candidate, matchKind));
        }

        if (matches.Count != 1)
            return NoReply(messageId, null, matches.Count > 1 ? "group_rule_ambiguous" :
                sawExclusion ? "group_rule_excluded" : "group_rule_unmatched", matches.Count > 1 ? "multiple_groups_matched" : "no_group_matched");

        var selected = matches[0];
        var assigned = await database.ConversationMessages.Where(item => item.Id == messageId &&
                (item.GroupProfileId == null || item.GroupProfileId == selected.Group.Id))
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.GroupProfileId, selected.Group.Id), token);
        if (assigned != 1) throw new InvalidOperationException("Inbound message group identity conflicts with the policy decision.");
        return new(messageId, InboundPolicyDecisionKind.Proceed, selected.Group.Id, null,
            JsonSerializer.Serialize(new { decision = "proceed", matchedBy = selected.MatchKind }));
    }

    public async Task PersistNoReplyTerminalAsync(InboundPolicyDecision decision, CancellationToken token)
    {
        if (decision.Kind != InboundPolicyDecisionKind.NoReply || string.IsNullOrWhiteSpace(decision.Reason))
            throw new ArgumentException("A typed no-reply decision is required.", nameof(decision));
        var updated = await database.ConversationMessages.Where(item => item.Id == decision.MessageId && item.Direction == "inbound")
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.GroupProfileId, decision.GroupProfileId)
                .SetProperty(item => item.ProcessingState, "completed").SetProperty(item => item.TerminalDecision, "no_reply")
                .SetProperty(item => item.TerminalReason, decision.Reason).SetProperty(item => item.TerminalEvidenceJson, decision.EvidenceJson), token);
        if (updated != 1) throw new InvalidOperationException("Inbound no-reply terminal could not be persisted.");
    }

    private static InboundPolicyDecision NoReply(Guid messageId, Guid? groupId, string reason, string evidence) =>
        new(messageId, InboundPolicyDecisionKind.NoReply, groupId, reason,
            JsonSerializer.Serialize(new { decision = "no_reply", reason, evidence }));

    private static GroupRule ToDomainRule(GroupRuleEntity rule) => new(rule.Id, rule.IncludePattern,
        ParsePatternKind(rule.IncludePatternKind), rule.ExcludePattern,
        ParsePatternKind(rule.ExcludePatternKind), rule.IgnoreCase, rule.IsEnabled);

    private static GroupRulePatternKind ParsePatternKind(int value) => Enum.IsDefined(typeof(GroupRulePatternKind), value)
        ? (GroupRulePatternKind)value
        : (GroupRulePatternKind)(-1);
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
        var handoffPausePolicy = Enum.TryParse<HandoffPausePolicy>(group.HandoffPausePolicy, true, out var parsedPausePolicy)
            ? parsedPausePolicy
            : HandoffPausePolicy.Group;
        return new(message.Id, message.RobotConfigId, robot.WorkToolRobotId, group.Id, group.Name, message.SenderDisplayName, message.StableSenderId, scope,
            message.Text, message.ReceivedAtUtc, allowedTags, history, summary, policy, provider, config.Id,
            HandoffPausePolicy: handoffPausePolicy);
    }

    public async Task PersistAnswerAndEnqueueAsync(ConversationProcessingRequest request, GroundedAnswerResult result, CancellationToken token)
    {
        await using var sendGate = await MySqlRobotSendCoordinator.AcquireAsync(database, request.RobotConfigId, token);
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        _ = await database.GroupProfiles.FromSqlInterpolated($"SELECT * FROM group_profile WHERE Id = {request.GroupProfileId} FOR UPDATE")
            .AsNoTracking().SingleAsync(token);
        var handoffActive = await database.HandoffCases.AsNoTracking().AnyAsync(item => item.GroupProfileId == request.GroupProfileId &&
            (item.State == "WaitingHuman" || item.State == "HumanHandling") &&
            (item.PauseScope == "Group" || item.PauseScope == "Sender" && request.StableSenderId != null && item.StableSenderId == request.StableSenderId), token);
        if (handoffActive)
        {
            await transaction.RollbackAsync(token);
            throw new ConversationHandoffRaceException("A handoff became active before the answer transaction committed.");
        }
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
            ModelConfigurationId = request.ModelConfigurationId == Guid.Empty ? null : request.ModelConfigurationId,
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
        var sendStatus = await MySqlRobotSendCoordinator.InitialStatusAsync(database, request.RobotConfigId, token);
        database.SendCommands.Add(new SendCommandEntity
        {
            RobotConfigId = request.RobotConfigId,
            GroupProfileId = request.GroupProfileId,
            IdempotencyKey = $"grounded-reply:{request.MessageId:D}",
            PayloadJson = JsonSerializer.Serialize(new { request.GroupName, Text = result.Decision.GroupText }),
            Status = sendStatus,
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

    public async Task PersistHandoffTerminalAsync(ConversationProcessingRequest request, GroundedAnswerResult result, CancellationToken token)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        if (await database.RetrievalAudits.AnyAsync(x => x.ConversationMessageId == request.MessageId, token))
        {
            await database.ConversationMessages.Where(x => x.Id == request.MessageId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ProcessingState, "completed"), token);
            await database.ConversationSessions.Where(x => x.Id == request.ConversationSessionId && x.LeaseOwner == request.SessionLeaseOwner)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.LeaseOwner, (string?)null).SetProperty(x => x.LeaseExpiresAtUtc, (DateTime?)null)
                    .SetProperty(x => x.Version, x => x.Version + 1), token);
            await transaction.CommitAsync(token); return;
        }
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var session = await database.ConversationSessions.AsNoTracking().Where(x => x.Id == request.ConversationSessionId && x.LeaseOwner == request.SessionLeaseOwner)
            .Select(x => new { x.NextSequence }).SingleOrDefaultAsync(token) ?? throw new ConversationSessionOwnershipLostException("Conversation lease was lost before handoff commit.");
        var inbound = await database.ConversationMessages.SingleAsync(x => x.Id == request.MessageId, token);
        inbound.GroupProfileId = request.GroupProfileId; inbound.ConversationSessionId = request.ConversationSessionId;
        inbound.SessionSequence = session.NextSequence + 1; inbound.ProcessingState = "completed";
        database.RetrievalAudits.Add(new RetrievalAuditEntity { ConversationMessageId = request.MessageId, GroupProfileId = request.GroupProfileId,
            ModelConfigurationId = request.ModelConfigurationId == Guid.Empty ? null : request.ModelConfigurationId,
            Decision = AnswerDecisionKind.Handoff.ToString(), ConfidenceThreshold = result.Audit.ConfidenceThreshold, ConfidenceValue = result.Audit.ConfidenceValue,
            ContextPolicy = result.Audit.ContextPolicy, FailureCode = result.Audit.FailureCode,
            EvidenceJson = JsonSerializer.Serialize(result.Audit.Evidence.Select(x => new { x.DocumentId, x.VersionId, x.ChunkId, x.PageNumber, x.Similarity, x.TagIds })),
            InputSummaryJson = result.Audit.InputSummaryJson, CreatedAtUtc = now });
        var guarded = await database.ConversationSessions.Where(x => x.Id == request.ConversationSessionId && x.LeaseOwner == request.SessionLeaseOwner &&
                x.LeaseExpiresAtUtc > now && x.NextSequence == session.NextSequence)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.LeaseOwner, (string?)null).SetProperty(x => x.LeaseExpiresAtUtc, (DateTime?)null)
                .SetProperty(x => x.NextSequence, session.NextSequence + 1).SetProperty(x => x.LastActivityAtUtc, now)
                .SetProperty(x => x.UpdatedAtUtc, now).SetProperty(x => x.Version, x => x.Version + 1), token);
        if (guarded != 1) { await transaction.RollbackAsync(token); throw new ConversationSessionOwnershipLostException("Conversation lease was lost before handoff commit."); }
        await database.SaveChangesAsync(token); await transaction.CommitAsync(token);
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
