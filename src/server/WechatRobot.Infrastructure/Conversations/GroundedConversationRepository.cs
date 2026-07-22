using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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
    public async Task<ConversationProcessingRequest> LoadForProcessingAsync(Guid messageId, CancellationToken token)
    {
        var message = await database.ConversationMessages.AsNoTracking().SingleOrDefaultAsync(item => item.Id == messageId, token)
            ?? throw new KeyNotFoundException("Inbound conversation message was not found.");
        if (message.Direction != "inbound") throw new InvalidOperationException("Only inbound messages can be processed.");
        var robot = await database.RobotConfigs.AsNoTracking().SingleAsync(item => item.Id == message.RobotConfigId, token);
        var groupName = await database.DurableJobs.AsNoTracking().Where(job => job.JobType == "ProcessInboundMessage" && job.PayloadJson.Contains(messageId.ToString()))
            .Select(job => job.PayloadJson).FirstOrDefaultAsync(token) is { } payloadJson
            ? JsonDocument.Parse(payloadJson).RootElement.GetProperty("GroupName").GetString() ?? string.Empty
            : string.Empty;
        var group = message.GroupProfileId is { } groupId
            ? await database.GroupProfiles.AsNoTracking().SingleOrDefaultAsync(item => item.Id == groupId && item.IsEnabled, token)
            : await database.GroupProfiles.AsNoTracking().Where(item => item.RobotConfigId == message.RobotConfigId && item.IsEnabled &&
                    (item.ExternalGroupId == groupName || item.Name == groupName))
                .FirstOrDefaultAsync(token);
        if (group is null) throw new InvalidOperationException("No enabled group profile matches the inbound message.");

        var policy = new GroupConfigurationService().GetEffectiveContext(new(group.ContextSenderIsolated, group.ContextHistoryTurns,
            group.ContextIdleTimeoutMinutes, group.ContextTokenCap, group.ContextSummaryEnabled, group.ContextIncludeBotHistory));
        var senderScope = policy.SenderIsolated ? message.SenderExternalUserId : "*";
        var sessions = await database.ConversationSessions.AsNoTracking().Where(item => item.GroupProfileId == group.Id &&
            (item.SenderScopeKey == "*" || item.SenderScopeKey == senderScope)).ToArrayAsync(token);
        var clearedAt = sessions.Max(item => item.ClearedAtUtc);
        var history = await database.ConversationMessages.AsNoTracking()
            .Where(item => item.GroupProfileId == group.Id && item.Id != message.Id && item.CreatedAtUtc < message.CreatedAtUtc &&
                (clearedAt == null || item.CreatedAtUtc > clearedAt))
            .OrderBy(item => item.CreatedAtUtc)
            .Select(item => new ConversationHistoryMessage(item.Role, item.SenderExternalUserId, item.Text, item.CreatedAtUtc)).ToArrayAsync(token);
        var summary = sessions.FirstOrDefault(item => item.SenderScopeKey == senderScope)?.Summary;
        var allowedTags = await database.GroupProfileTags.AsNoTracking().Where(item => item.GroupProfileId == group.Id)
            .Select(item => item.KnowledgeTagId).ToArrayAsync(token);
        var config = await database.ModelConfigs.AsNoTracking().Where(item => item.ConfigurationType == "chat" && item.IsEnabled)
            .OrderByDescending(item => item.IsDefault).ThenBy(item => item.CreatedAtUtc).FirstOrDefaultAsync(token)
            ?? throw new InvalidOperationException("No enabled chat model configuration exists.");
        var provider = modelConfigurations.ToProviderConfiguration(new(config.Id, config.Name, config.Provider, config.BaseUrl, config.Model,
            config.EncryptedApiKey, config.TimeoutSeconds, config.MaxRetries, config.IsEnabled, config.IsDefault));
        return new(message.Id, message.RobotConfigId, robot.WorkToolRobotId, group.Id, group.Name, message.SenderExternalUserId,
            message.Text, message.ReceivedAtUtc, allowedTags, history, summary, policy, provider);
    }

    public async Task PersistAnswerAndEnqueueAsync(ConversationProcessingRequest request, GroundedAnswerResult result, CancellationToken token)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        if (await database.RetrievalAudits.AnyAsync(item => item.ConversationMessageId == request.MessageId, token))
        {
            await transaction.CommitAsync(token);
            return;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var inbound = await database.ConversationMessages.SingleAsync(item => item.Id == request.MessageId, token);
        inbound.GroupProfileId = request.GroupProfileId;
        var senderScope = request.ContextPolicy.SenderIsolated ? request.SenderExternalUserId : "*";
        var session = await database.ConversationSessions.SingleOrDefaultAsync(item => item.GroupProfileId == request.GroupProfileId && item.SenderScopeKey == senderScope, token);
        if (session is null)
        {
            session = new ConversationSessionEntity { GroupProfileId = request.GroupProfileId, SenderScopeKey = senderScope, CreatedAtUtc = now };
            database.ConversationSessions.Add(session);
        }
        inbound.ConversationSessionId = session.Id;
        var outbound = new ConversationMessageEntity
        {
            RobotConfigId = request.RobotConfigId,
            GroupProfileId = request.GroupProfileId,
            ConversationSessionId = session.Id,
            Direction = "outbound",
            Role = "assistant",
            InReplyToMessageId = request.MessageId,
            FallbackHash = $"outbound:{request.MessageId:D}",
            FallbackWindowStartUtc = DateTime.UnixEpoch,
            SenderExternalUserId = request.SenderExternalUserId,
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
                item.DocumentId, item.VersionId, item.ChunkId, item.PageNumber, item.Similarity, item.TagIds, item.DocumentTitle
            })),
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

        session.LastActivityAtUtc = now;
        session.UpdatedAtUtc = now;
        await database.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }

    public async Task<int> ClearContextAsync(Guid groupProfileId, string? senderExternalUserId, DateTime clearedAtUtc, CancellationToken token)
    {
        var scope = string.IsNullOrWhiteSpace(senderExternalUserId) ? "*" : senderExternalUserId.Trim();
        var session = await database.ConversationSessions.SingleOrDefaultAsync(item => item.GroupProfileId == groupProfileId && item.SenderScopeKey == scope, token);
        if (session is null)
        {
            session = new ConversationSessionEntity { GroupProfileId = groupProfileId, SenderScopeKey = scope, LastActivityAtUtc = clearedAtUtc };
            database.ConversationSessions.Add(session);
        }
        session.ClearedAtUtc = clearedAtUtc;
        session.Summary = null;
        session.UpdatedAtUtc = clearedAtUtc;
        return await database.SaveChangesAsync(token);
    }

    public async Task<PageResult<ConversationPageItem>> GetHistoryAsync(Guid groupProfileId, int page, int pageSize, CancellationToken token)
    {
        (page, pageSize) = NormalizePage(page, pageSize);
        var query = database.ConversationMessages.AsNoTracking().Where(item => item.GroupProfileId == groupProfileId);
        var total = await query.CountAsync(token);
        var items = await query.OrderByDescending(item => item.CreatedAtUtc).ThenByDescending(item => item.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(item => new ConversationPageItem(item.Id, groupProfileId, item.ConversationSessionId, item.Direction, item.Role, item.SenderExternalUserId, item.Text, item.CreatedAtUtc)).ToArrayAsync(token);
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
