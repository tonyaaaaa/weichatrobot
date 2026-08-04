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
        var allNamedGroups = database.GroupProfiles.AsNoTracking()
            .Where(item => item.RobotConfigId == message.RobotConfigId && item.Name == groupName);
        GroupProfileEntity[] lifecycleMatches;
        if (string.IsNullOrWhiteSpace(groupRemark))
        {
            lifecycleMatches = await allNamedGroups
                .Where(item => item.WorkToolGroupRemark == null)
                .OrderBy(item => item.Id)
                .Take(2)
                .ToArrayAsync(token);
        }
        else
        {
            lifecycleMatches = await allNamedGroups
                .Where(item => item.WorkToolGroupRemark == groupRemark)
                .OrderBy(item => item.Id)
                .Take(2)
                .ToArrayAsync(token);
            if (lifecycleMatches.Length == 0)
                lifecycleMatches = await allNamedGroups
                    .Where(item => item.WorkToolGroupRemark == null)
                    .OrderBy(item => item.Id)
                    .Take(2)
                    .ToArrayAsync(token);
        }
        if (lifecycleMatches.Length == 1 && !lifecycleMatches[0].IsEnabled)
            return NoReply(
                messageId,
                lifecycleMatches[0].Id,
                lifecycleMatches[0].ArchivedAtUtc is null ? "group_disabled" : "group_archived",
                "group_lifecycle_inactive");
        var namedGroups = database.GroupProfiles.AsNoTracking()
            .Where(item => item.RobotConfigId == message.RobotConfigId &&
                           item.IsEnabled &&
                           item.Name == groupName);
        GroupProfileEntity[] exactNames;
        if (string.IsNullOrWhiteSpace(groupRemark))
        {
            exactNames = await namedGroups
                .OrderBy(item => item.Id)
                .Take(2)
                .ToArrayAsync(token);
        }
        else
        {
            exactNames = await namedGroups
                .Where(item => item.WorkToolGroupRemark == groupRemark)
                .OrderBy(item => item.Id)
                .Take(2)
                .ToArrayAsync(token);
            if (exactNames.Length == 0)
            {
                exactNames = await namedGroups
                    .Where(item => item.WorkToolGroupRemark == null)
                    .OrderBy(item => item.Id)
                    .Take(2)
                    .ToArrayAsync(token);
            }
        }
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
            var candidateQuery = database.GroupProfiles.AsNoTracking()
                .Where(item => item.RobotConfigId == message.RobotConfigId &&
                               item.IsEnabled);
            if (!string.IsNullOrWhiteSpace(groupRemark))
            {
                candidateQuery = candidateQuery.Where(item =>
                    item.WorkToolGroupRemark == null ||
                    item.WorkToolGroupRemark == groupRemark);
            }
            candidates = await candidateQuery
                .OrderBy(item => item.Id)
                .Take(MaximumPolicyCandidateGroups + 1)
                .ToArrayAsync(token);
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
        var message = await database.ConversationMessages.SingleOrDefaultAsync(
            item => item.Id == decision.MessageId && item.Direction == "inbound",
            token) ?? throw new InvalidOperationException("Inbound no-reply terminal could not be persisted.");
        message.GroupProfileId = decision.GroupProfileId;
        message.ConversationSessionId = null;
        message.SessionSequence = null;
        message.ProcessingState = "completed";
        message.TerminalDecision = "no_reply";
        message.TerminalReason = decision.Reason;
        message.TerminalEvidenceJson = decision.EvidenceJson;
        await database.SaveChangesAsync(token);
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

    public async Task ReleaseLeaseAsync(Guid sessionId, string leaseOwner, CancellationToken token)
    {
        var session = await database.ConversationSessions.SingleOrDefaultAsync(
            item => item.Id == sessionId && item.LeaseOwner == leaseOwner,
            token);
        if (session is null) return;
        session.LeaseOwner = null;
        session.LeaseExpiresAtUtc = null;
        session.Version++;
        try
        {
            await database.SaveChangesAsync(token);
        }
        catch (DbUpdateConcurrencyException)
        {
            database.ChangeTracker.Clear();
        }
    }

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
            .Select(item => new ConversationHistoryMessage(
                item.Role,
                scope.ScopeKey,
                item.Text,
                item.CreatedAtUtc,
                item.Id,
                item.SessionSequence,
                item.SenderDisplayName)).ToArrayAsync(token))
            .Reverse().ToArray();
        var summary = session?.Summary;
        var allowedTags = await database.GroupProfileTags.AsNoTracking().Where(item => item.GroupProfileId == group.Id)
            .Select(item => item.KnowledgeTagId).ToArrayAsync(token);
        var config = await database.ModelConfigs.AsNoTracking().Where(item => item.ConfigurationType == "chat" && item.IsEnabled)
            .OrderByDescending(item => item.IsDefault).ThenBy(item => item.CreatedAtUtc).FirstOrDefaultAsync(token)
            ?? throw new InvalidOperationException("No enabled chat model configuration exists.");
        var provider = modelConfigurations.ToProviderConfiguration(new(config.Id, config.Name, config.Provider, config.BaseUrl, config.Model,
            config.EncryptedApiKey, config.TimeoutSeconds, config.MaxRetries, config.IsEnabled, config.IsDefault,
            config.EmbeddingDimension, config.WebSearchMode));
        return new(message.Id, message.RobotConfigId, robot.WorkToolRobotId, group.Id, groupName, message.SenderDisplayName, message.StableSenderId, scope,
            message.Text, message.ReceivedAtUtc, allowedTags, history, summary, policy, provider, config.Id,
            AnswerFallback: new GroupAnswerFallbackSettings(
                group.WebSearchEnabled,
                group.ModelKnowledgeFallbackEnabled,
                group.WebSearchShowSources,
                group.WebSearchResultCount,
                group.WebSearchRecency,
                group.WebSearchDomainFilter,
                group.WebSearchContentSize,
                group.FinalNoEvidencePolicy),
            ModelConfigurationVersion: config.Version);
    }

    public async Task PersistAnswerAndEnqueueAsync(ConversationProcessingRequest request, GroundedAnswerResult result, CancellationToken token)
    {
        await using var sendGate = await MySqlRobotSendCoordinator.AcquireAsync(database, request.RobotConfigId, token);
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        var groupState = await database.GroupProfiles.FromSqlInterpolated($"SELECT * FROM group_profile WHERE Id = {request.GroupProfileId} FOR UPDATE")
            .AsNoTracking().SingleAsync(token);
        if (!groupState.IsEnabled || groupState.ArchivedAtUtc is not null)
        {
            var terminalReason = groupState.ArchivedAtUtc is null ? "group_disabled" : "group_archived";
            var terminal = await database.ConversationMessages.SingleAsync(
                item => item.Id == request.MessageId,
                token);
            terminal.ProcessingState = "completed";
            terminal.TerminalDecision = "no_reply";
            terminal.TerminalReason = terminalReason;
            if (request.ConversationSessionId != Guid.Empty && request.SessionLeaseOwner is not null)
                await ReleaseOwnedSessionTrackedAsync(request, token);
            await database.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return;
        }
        if (await database.RetrievalAudits.AnyAsync(item => item.ConversationMessageId == request.MessageId, token))
        {
            var duplicate = await database.ConversationMessages.SingleAsync(
                item => item.Id == request.MessageId,
                token);
            duplicate.ProcessingState = "completed";
            if (request.ConversationSessionId != Guid.Empty && request.SessionLeaseOwner is not null)
                await ReleaseOwnedSessionTrackedAsync(request, token);
            await database.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var inbound = await database.ConversationMessages.SingleAsync(item => item.Id == request.MessageId, token);
        inbound.GroupProfileId = request.GroupProfileId;
        inbound.ProcessingState = "completed";
        if (request.ConversationSessionId == Guid.Empty || string.IsNullOrWhiteSpace(request.SessionLeaseOwner))
            throw new ConversationSessionOwnershipLostException("No owned conversation session lease is attached to the request.");
        var session = await database.ConversationSessions.SingleOrDefaultAsync(
            item => item.Id == request.ConversationSessionId && item.LeaseOwner == request.SessionLeaseOwner,
            token)
            ?? throw new ConversationSessionOwnershipLostException("Conversation session lease ownership was lost before sequence allocation.");
        var nextSequence = session.NextSequence;
        inbound.ConversationSessionId = request.ConversationSessionId;
        inbound.SessionSequence = nextSequence + 1;
        var outbound = new ConversationMessageEntity
        {
            RobotConfigId = request.RobotConfigId,
            GroupProfileId = request.GroupProfileId,
            ConversationSessionId = request.ConversationSessionId,
            SessionSequence = nextSequence + 2,
            GroupName = request.ReplyGroupName,
            Direction = "outbound",
            Role = "assistant",
            InReplyToMessageId = request.MessageId,
            FallbackHash = $"outbound:{request.MessageId:D}",
            FallbackWindowStartUtc = DateTime.UnixEpoch,
            SenderDisplayName = "机器人",
            StableSenderId = null,
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
            AnswerSource = result.Audit.AnswerSource,
            FixedReplyTemplateId = result.Audit.FixedReplyTemplateId,
            FixedReplyTemplateVersion = result.Audit.FixedReplyTemplateVersion,
            WebSearchFailureCode = result.Audit.WebSearchFailureCode,
            WebSearchSourcesJson = JsonSerializer.Serialize(
                (result.Audit.WebSearchSources ?? []).Take(20).Select((source, index) => new
                {
                    title = source.Title,
                    url = source.Url.AbsoluteUri,
                    site = source.Site,
                    publishedAt = source.PublishedAt,
                    summary = source.Summary,
                    index = source.Index ?? index + 1
                })),
            MemoryRecallJson = JsonSerializer.Serialize(new
            {
                failureCode = result.Audit.MemoryRecall?.FailureCode,
                memories = (result.Audit.MemoryRecall?.Memories ?? []).Select(memory => new
                {
                    id = memory.Id,
                    scope = memory.ScopeType,
                    type = memory.MemoryType,
                    version = memory.Version,
                    score = memory.Score
                })
            }),
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
            PayloadJson = JsonSerializer.Serialize(new
            {
                GroupName = request.ReplyGroupName,
                Text = result.Decision.GroupText,
                MemoryRecallIds = (result.Audit.MemoryRecall?.Memories ?? []).Select(memory => memory.Id).ToArray()
            }),
            Status = sendStatus,
            NextAttemptAtUtc = now,
            CreatedAtUtc = now
        });
        var memoryJobId = CreateDeterministicGuid($"memory-extract:{request.MessageId:D}");
        if (!await database.DurableJobs.AnyAsync(item => item.Id == memoryJobId, token))
        {
            var explicitRequest = request.Question.Contains("记住", StringComparison.Ordinal);
            var availableAtUtc = explicitRequest ? now : now.AddMinutes(30);
            database.DurableJobs.Add(new DurableJobEntity
            {
                Id = memoryJobId,
                JobType = "ExtractConversationMemory",
                GroupProfileId = request.GroupProfileId,
                PayloadJson = JsonSerializer.Serialize(new
                {
                    messageId = request.MessageId,
                    conversationSessionId = request.ConversationSessionId,
                    groupProfileId = request.GroupProfileId,
                    modelConfigurationId = request.ModelConfigurationId,
                    modelConfigurationVersion = request.ModelConfigurationVersion,
                    explicitRequest
                }),
                Status = "pending",
                AvailableAtUtc = availableAtUtc,
                NextAttemptAtUtc = availableAtUtc,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
        }

        if (session.LeaseExpiresAtUtc is not { } leaseExpiry || leaseExpiry <= now ||
            session.NextSequence != nextSequence)
        {
            await transaction.RollbackAsync(token);
            throw new ConversationSessionOwnershipLostException("Conversation session lease ownership was lost before commit.");
        }
        session.LeaseOwner = null;
        session.LeaseExpiresAtUtc = null;
        session.LastActivityAtUtc = now;
        session.UpdatedAtUtc = now;
        session.NextSequence = nextSequence + 2;
        session.Summary = result.ResetContextBeforeCurrent
            ? result.UpdatedSummary
            : result.UpdatedSummary ?? session.Summary;
        if (result.ResetContextBeforeCurrent)
        {
            session.ClearedThroughSequence = nextSequence;
            session.ClearedAtUtc = request.ReceivedAtUtc;
        }
        session.Version++;
        try
        {
            await database.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            database.ChangeTracker.Clear();
            throw new ConversationSessionOwnershipLostException("Conversation session lease ownership was lost before commit.");
        }
    }

    private async Task ReleaseOwnedSessionTrackedAsync(
        ConversationProcessingRequest request,
        CancellationToken token)
    {
        var session = await database.ConversationSessions.SingleOrDefaultAsync(
            item => item.Id == request.ConversationSessionId && item.LeaseOwner == request.SessionLeaseOwner,
            token);
        if (session is null) return;
        session.LeaseOwner = null;
        session.LeaseExpiresAtUtc = null;
        session.Version++;
    }

    private static Guid CreateDeterministicGuid(string value)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value));
        return new Guid(hash.AsSpan(0, 16));
    }

    public async Task<int> ClearGroupContextAsync(Guid groupProfileId, DateTime clearedAtUtc, CancellationToken token)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        var cleared = await ClearGroupSessionsAsync(groupProfileId, clearedAtUtc, token);
        await transaction.CommitAsync(token);
        return cleared;
    }

    public async Task<GroupConversationContextSourcePage?> GetGroupContextAsync(
        Guid groupProfileId,
        int page,
        int pageSize,
        CancellationToken token)
    {
        (page, pageSize) = NormalizePage(page, pageSize);
        var group = await database.GroupProfiles.AsNoTracking()
            .Where(item => item.Id == groupProfileId)
            .Select(item => new
            {
                item.Id,
                item.ConfigurationVersion,
                item.ContextSenderIsolated,
                item.ContextHistoryTurns,
                item.ContextIdleTimeoutMinutes,
                item.ContextTokenCap,
                item.ContextSummaryEnabled,
                item.ContextIncludeBotHistory
            })
            .SingleOrDefaultAsync(token);
        if (group is null) return null;

        var sessionsQuery = database.ConversationSessions.AsNoTracking()
            .Where(item => item.GroupProfileId == groupProfileId);
        var total = await sessionsQuery.CountAsync(token);
        var sessions = await sessionsQuery
            .OrderByDescending(item => item.LastActivityAtUtc)
            .ThenBy(item => item.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(item => new
            {
                item.Id,
                item.SenderScopeKey,
                item.Summary,
                item.ClearedAtUtc,
                item.ClearedThroughSequence,
                item.LastActivityAtUtc,
                item.Version
            })
            .ToArrayAsync(token);

        var maximumMessagesPerSession = Math.Max(
            200,
            ((group.ContextHistoryTurns ?? GroupConfigurationService.DefaultHistoryTurns) * 2) + 1);
        var items = new List<ConversationContextSessionSource>(sessions.Length);
        foreach (var session in sessions)
        {
            var messages = await database.ConversationMessages.AsNoTracking()
                .Where(message => message.ConversationSessionId == session.Id
                    && message.SessionSequence > session.ClearedThroughSequence)
                .OrderByDescending(message => message.SessionSequence)
                .ThenByDescending(message => message.Id)
                .Take(maximumMessagesPerSession)
                .Select(message => new
                {
                    message.Id,
                    message.Role,
                    message.SenderDisplayName,
                    message.Text,
                    message.CreatedAtUtc,
                    message.SessionSequence
                })
                .ToArrayAsync(token);
            Array.Reverse(messages);
            var senderDisplayName = messages
                .LastOrDefault(message => string.Equals(message.Role, "user", StringComparison.Ordinal))
                ?.SenderDisplayName ?? "未知成员";
            items.Add(new ConversationContextSessionSource(
                session.Id,
                session.SenderScopeKey,
                senderDisplayName,
                session.Summary,
                session.ClearedAtUtc,
                session.ClearedThroughSequence,
                session.LastActivityAtUtc,
                session.Version,
                messages.Select(message => new ConversationHistoryMessage(
                    message.Role,
                    session.SenderScopeKey,
                    message.Text,
                    message.CreatedAtUtc,
                    message.Id,
                    message.SessionSequence,
                    message.SenderDisplayName)).ToArray()));
        }

        return new GroupConversationContextSourcePage(
            group.Id,
            group.ConfigurationVersion,
            new GroupContextOverrides(
                group.ContextSenderIsolated,
                group.ContextHistoryTurns,
                group.ContextIdleTimeoutMinutes,
                group.ContextTokenCap,
                group.ContextSummaryEnabled,
                group.ContextIncludeBotHistory),
            items,
            total,
            page,
            pageSize);
    }

    public async Task<ClearConversationContextResult> ClearGroupContextAsync(
        Guid groupProfileId,
        int expectedConfigurationVersion,
        DateTime clearedAtUtc,
        CancellationToken token)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        var currentVersion = await database.GroupProfiles
            .Where(item => item.Id == groupProfileId)
            .Select(item => (int?)item.ConfigurationVersion)
            .SingleOrDefaultAsync(token);
        if (currentVersion is null)
            return new(ClearConversationContextStatus.NotFound);
        if (currentVersion.Value != expectedConfigurationVersion)
            return new(ClearConversationContextStatus.Conflict, CurrentConfigurationVersion: currentVersion.Value);

        var cleared = await ClearGroupSessionsAsync(groupProfileId, clearedAtUtc, token);
        await transaction.CommitAsync(token);
        return new(
            ClearConversationContextStatus.Cleared,
            cleared,
            currentVersion.Value);
    }

    private async Task<int> ClearGroupSessionsAsync(
        Guid groupProfileId,
        DateTime clearedAtUtc,
        CancellationToken token)
    {
        var sessions = await database.ConversationSessions
            .Where(item => item.GroupProfileId == groupProfileId)
            .ToArrayAsync(token);
        foreach (var session in sessions)
        {
            session.ClearedAtUtc = clearedAtUtc;
            session.ClearedThroughSequence = session.NextSequence;
            session.Summary = null;
            session.LeaseOwner = null;
            session.LeaseExpiresAtUtc = null;
            session.UpdatedAtUtc = clearedAtUtc;
            session.Version++;
        }
        var cleared = sessions.Length;
        if (!sessions.Any(item => item.SenderScopeKey == "group"))
        {
            database.ConversationSessions.Add(new ConversationSessionEntity
            {
                GroupProfileId = groupProfileId, SenderScopeKey = "group", LastActivityAtUtc = clearedAtUtc,
                ClearedAtUtc = clearedAtUtc, ClearedThroughSequence = 0, CreatedAtUtc = clearedAtUtc, UpdatedAtUtc = clearedAtUtc
            });
            cleared++;
        }
        await database.SaveChangesAsync(token);
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
