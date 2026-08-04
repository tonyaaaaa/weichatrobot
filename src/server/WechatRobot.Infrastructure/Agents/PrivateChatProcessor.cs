using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;
using WechatRobot.Application.Conversations;
using WechatRobot.Application.Agents;
using WechatRobot.Application.FixedReplies;
using WechatRobot.Application.Groups;
using WechatRobot.Application.Jobs;
using WechatRobot.Application.Models;
using WechatRobot.Application.PrivateChat;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.Infrastructure.Agents;

public sealed class PrivateChatProcessor(
    WechatRobotDbContext database,
    IAnswerAgent answerAgent,
    ModelConfigurationService modelConfigurations,
    IDurableJobRepository jobs,
    IPrivateKnowledgeIngestStore ingests,
    ConversationContextService contextService,
    TimeProvider timeProvider,
    MultiTurnRetrievalService multiTurnRetrieval,
    ITemplateRoutingAgent? templateRouter = null,
    FixedReplyTemplateService? fixedReplies = null,
    AgentRuntimeOptions? runtimeOptions = null) : IPrivateChatProcessor
{
    private static readonly GroupAnswerFallbackSettings PrivateAnswerFallback = new(
        WebSearchEnabled: true,
        ModelKnowledgeFallbackEnabled: true,
        WebSearchShowSources: false,
        WebSearchResultCount: 5,
        WebSearchRecency: "NoLimit",
        WebSearchDomainFilter: null,
        WebSearchContentSize: "Medium",
        FinalNoEvidencePolicy: "InsufficientEvidence");

    public async Task ProcessAsync(LeasedDurableJob job, CancellationToken cancellationToken)
    {
        if (job.JobType != "ProcessPrivateMessage") throw new InvalidOperationException("Unsupported private job.");
        var payload = JsonSerializer.Deserialize<Payload>(job.PayloadJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Private message payload is invalid.");
        var message = await database.ConversationMessages.SingleAsync(x => x.Id == payload.MessageId, cancellationToken);
        var runtime = runtimeOptions ?? new AgentRuntimeOptions();
        if (runtime.PrivateChatRuntimeMode == PrivateChatRuntimeMode.Disabled)
        {
            message.ProcessingState = "completed";
            message.TerminalDecision = "no_reply";
            message.TerminalReason = "private_chat_runtime_disabled";
            message.TerminalEvidenceJson =
                """{"decision":"no_reply","reason":"private_chat_runtime_disabled"}""";
            await database.SaveChangesAsync(cancellationToken);
            return;
        }
        var session = await EnsureSessionAsync(message, cancellationToken);
        var command = PrivateChatCommandParser.Parse(message.RoomType ?? 0, message.Text);
        if (command.Kind == PrivateChatMessageKind.UnsupportedIngest)
        {
            await ReplyAsync(message, "外部联系人私聊不支持直接知识入库。你仍可以正常提问。", cancellationToken);
            return;
        }
        if (command.Kind == PrivateChatMessageKind.DirectKnowledgeIngest)
        {
            var batch = await ingests.GetOrCreateAsync(message.RobotConfigId, message.Id, message.RoomType!.Value,
                message.PeerDisplayName ?? message.SenderDisplayName, timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
            if (!await database.DurableJobs.AnyAsync(x => x.Id == batch.Id, cancellationToken))
            {
                database.DurableJobs.Add(new DurableJobEntity
                {
                    Id = batch.Id,
                    JobType = "ProcessPrivateKnowledgeIngest",
                    PayloadJson = JsonSerializer.Serialize(new { BatchId = batch.Id })
                });
                await database.SaveChangesAsync(cancellationToken);
            }
            await ReplyAsync(message, "已收到，正在整理并对比现有知识。", cancellationToken);
            return;
        }

        if (ConversationalGreeting.TryCreate(command.Body, out var greeting))
        {
            await ReplyAsync(
                message,
                greeting.Decision.GroupText,
                cancellationToken,
                greeting);
            return;
        }

        var model = await database.ModelConfigs.AsNoTracking().SingleOrDefaultAsync(x =>
            x.ConfigurationType == "chat" && x.IsDefault && x.IsEnabled, cancellationToken);
        if (model is null)
        {
            await ReplyAsync(message, "系统暂时不可用，请稍后再试。", cancellationToken);
            return;
        }
        if (runtime.TemplateRoutingRuntimeMode == TemplateRoutingRuntimeMode.AgentFramework
            && templateRouter is not null
            && fixedReplies is not null)
        {
            var route = await templateRouter.RoutePrivateAsync(
                command.Body,
                cancellationToken);
            if (route is MatchFixedTemplate match)
            {
                var fixedReply = await fixedReplies.ResolveForPrivateAsync(
                    match.TemplateId,
                    match.ExpectedVersion,
                    cancellationToken);
                if (fixedReply is not null)
                {
                    var fixedResult = new GroundedAnswerResult(
                        new AnswerDecision(
                            AnswerDecisionKind.Answer,
                            fixedReply.ReplyText),
                        new RetrievalAuditDraft(
                            [],
                            0,
                            1,
                            "fixed_template",
                            "answer",
                            AnswerSource: "fixed_template",
                            FixedReplyTemplateId: fixedReply.Id,
                            FixedReplyTemplateVersion: fixedReply.Version));
                    await ReplyAsync(
                        message,
                        fixedReply.ReplyText,
                        cancellationToken,
                        fixedResult,
                        model.Id);
                    return;
                }
            }
        }
        var tagIds = await database.KnowledgeTags.AsNoTracking().Where(x => x.IsEnabled)
            .Select(x => x.Id).ToArrayAsync(cancellationToken);
        var historyRows = await database.ConversationMessages.AsNoTracking()
            .Where(x => x.RobotConfigId == message.RobotConfigId
                        && x.ChannelType == "Private"
                        && x.RoomType == message.RoomType
                        && x.ScopeHash == message.ScopeHash
                        && x.Id != message.Id)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(48)
            .OrderBy(x => x.CreatedAtUtc)
            .ToArrayAsync(cancellationToken);
        var ingestMessageIds = historyRows
            .Where(row =>
                string.Equals(row.Direction, "inbound", StringComparison.Ordinal)
                && PrivateChatCommandParser.Parse(
                    row.RoomType ?? 0,
                    row.Text).Kind != PrivateChatMessageKind.Question)
            .Select(row => row.Id)
            .ToHashSet();
        var history = historyRows
            .Where(row => !ingestMessageIds.Contains(row.Id))
            .Where(row =>
                row.InReplyToMessageId is not { } sourceId
                || !ingestMessageIds.Contains(sourceId))
            .TakeLast(24)
            .Select(row => new ConversationHistoryMessage(
                row.Role,
                message.ScopeHash ?? "*",
                row.Text,
                row.CreatedAtUtc,
                row.Id,
                row.SessionSequence,
                row.SenderDisplayName))
            .ToArray();
        var contextPolicy = new GroupContextSettings(false, 6, 30, 3000, true, true);
        var context = contextService.Build(
            history,
            contextPolicy,
            message.ScopeHash ?? "*",
            timeProvider.GetUtcNow().UtcDateTime,
            session.Summary);
        var providerConfiguration =
            modelConfigurations.ToProviderConfiguration(
                new ModelConfigurationRecord(
                    model.Id,
                    model.Name,
                    model.Provider,
                    model.BaseUrl,
                    model.Model,
                    model.EncryptedApiKey,
                    model.TimeoutSeconds,
                    model.MaxRetries,
                    model.IsEnabled,
                    model.IsDefault,
                    model.EmbeddingDimension,
                    model.WebSearchMode));
        var preparation = await multiTurnRetrieval.PrepareAsync(
            new QueryRewriteRequest(
                message.Id,
                session.Id,
                ConversationChannelType.Private,
                null,
                message.RobotConfigId,
                message.ScopeHash ?? message.Id.ToString("N"),
                message.PeerDisplayName ?? message.SenderDisplayName,
                command.Body,
                context,
                providerConfiguration,
                model.Id),
            cancellationToken);
        if (preparation.TerminalAnswer is not null)
        {
            var terminalResult = multiTurnRetrieval.CreateTerminalResult(
                preparation,
                contextPolicy);
            await ReplyAsync(
                message,
                terminalResult.Decision.GroupText,
                cancellationToken,
                terminalResult,
                model.Id);
            return;
        }

        var result = await answerAgent.AnswerAsync(new GroundedAnswerRequest(
            message.Id, Guid.Empty, message.ScopeHash ?? message.Id.ToString("N"), command.Body, tagIds,
            context,
            contextPolicy,
            providerConfiguration,
            preparation.RetrievalQuery,
            ModelConfigurationId: model.Id,
            RobotConfigId: message.RobotConfigId,
            SubjectKey: message.PeerDisplayName,
            SenderDisplayName: message.PeerDisplayName,
            AnswerFallback: PrivateAnswerFallback,
            QueryRewriteAudit: preparation.Audit), cancellationToken);
        await ReplyAsync(message, result.Decision.GroupText, cancellationToken, result, model.Id);
    }

    private async Task ReplyAsync(
        ConversationMessageEntity source,
        string text,
        CancellationToken cancellationToken,
        GroundedAnswerResult? answer = null,
        Guid? modelConfigurationId = null)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (!await database.ConversationMessages.AnyAsync(x => x.InReplyToMessageId == source.Id, cancellationToken))
        {
            var session = source.ConversationSessionId is { } sessionId
                ? await database.ConversationSessions.SingleAsync(x => x.Id == sessionId, cancellationToken)
                : null;
            var nextSequence = session is null ? (long?)null : session.NextSequence + 1;
            database.ConversationMessages.Add(new ConversationMessageEntity
            {
                RobotConfigId = source.RobotConfigId, Direction = "outbound", Role = "assistant",
                InReplyToMessageId = source.Id, FallbackHash = $"private-outbound:{source.Id:D}",
                FallbackWindowStartUtc = DateTime.UnixEpoch, GroupName = string.Empty,
                ConversationSessionId = source.ConversationSessionId,
                SessionSequence = nextSequence,
                ChannelType = "Private", RoomType = source.RoomType, PeerDisplayName = source.PeerDisplayName,
                ScopeHash = source.ScopeHash, SenderDisplayName = "机器人", Text = text,
                ReceivedAtUtc = now, CreatedAtUtc = now
            });
            if (answer is not null)
            {
                database.RetrievalAudits.Add(CreateAudit(source, answer, modelConfigurationId, now));
            }
            source.ProcessingState = "completed";
            source.TerminalDecision = "answer";
            if (session is not null)
            {
                session.NextSequence = nextSequence!.Value;
                session.LastActivityAtUtc = now;
                session.UpdatedAtUtc = now;
                session.Version++;
            }
            await database.SaveChangesAsync(cancellationToken);
        }
        var idempotencyKey = $"private-reply:{source.Id:D}";
        if (!await database.SendCommands.AnyAsync(
                command => command.IdempotencyKey == idempotencyKey,
                cancellationToken))
        {
            await jobs.EnqueueSendCommandAsync(new EnqueueSendCommandRequest(
                source.RobotConfigId, string.Empty, source.PeerDisplayName ?? source.SenderDisplayName,
                text, idempotencyKey), cancellationToken);
        }
    }

    private async Task<ConversationSessionEntity> EnsureSessionAsync(
        ConversationMessageEntity message,
        CancellationToken cancellationToken)
    {
        var session = await database.ConversationSessions.SingleOrDefaultAsync(
            x => x.ChannelType == "Private"
                 && x.RobotConfigId == message.RobotConfigId
                 && x.RoomType == message.RoomType
                 && x.ScopeHash == message.ScopeHash,
            cancellationToken);
        if (session is null)
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            session = new ConversationSessionEntity
            {
                ChannelType = "Private",
                RobotConfigId = message.RobotConfigId,
                RoomType = message.RoomType,
                PeerDisplayName = message.PeerDisplayName,
                ScopeHash = message.ScopeHash,
                SenderScopeKey = message.ScopeHash ?? "*",
                LastActivityAtUtc = now,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            database.ConversationSessions.Add(session);
            try
            {
                await database.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception) when (exception.InnerException is MySqlException { Number: 1062 })
            {
                database.Entry(session).State = EntityState.Detached;
                session = await database.ConversationSessions.SingleAsync(
                    x => x.ChannelType == "Private"
                         && x.RobotConfigId == message.RobotConfigId
                         && x.RoomType == message.RoomType
                         && x.ScopeHash == message.ScopeHash,
                    cancellationToken);
            }
        }
        if (message.ConversationSessionId is null)
        {
            session.NextSequence++;
            session.LastActivityAtUtc = message.ReceivedAtUtc;
            session.UpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
            session.Version++;
            message.ConversationSessionId = session.Id;
            message.SessionSequence = session.NextSequence;
            await database.SaveChangesAsync(cancellationToken);
        }
        return session;
    }

    private static RetrievalAuditEntity CreateAudit(
        ConversationMessageEntity source,
        GroundedAnswerResult answer,
        Guid? modelConfigurationId,
        DateTime nowUtc) =>
        new()
        {
            ConversationMessageId = source.Id,
            GroupProfileId = null,
            ChannelType = "Private",
            ModelConfigurationId = modelConfigurationId,
            Decision = answer.Audit.Decision,
            ConfidenceThreshold = answer.Audit.ConfidenceThreshold,
            ConfidenceValue = answer.Audit.ConfidenceValue,
            ContextPolicy = answer.Audit.ContextPolicy,
            FailureCode = answer.Audit.FailureCode,
            AnswerSource = answer.Audit.AnswerSource,
            FixedReplyTemplateId = answer.Audit.FixedReplyTemplateId,
            FixedReplyTemplateVersion = answer.Audit.FixedReplyTemplateVersion,
            WebSearchFailureCode = answer.Audit.WebSearchFailureCode,
            WebSearchSourcesJson = JsonSerializer.Serialize(
                (answer.Audit.WebSearchSources ?? []).Take(20).Select((sourceItem, index) => new
                {
                    title = sourceItem.Title,
                    url = sourceItem.Url.AbsoluteUri,
                    site = sourceItem.Site,
                    publishedAt = sourceItem.PublishedAt,
                    summary = sourceItem.Summary,
                    index = sourceItem.Index ?? index + 1
                })),
            MemoryRecallJson = JsonSerializer.Serialize(new
            {
                failureCode = answer.Audit.MemoryRecall?.FailureCode,
                memories = (answer.Audit.MemoryRecall?.Memories ?? []).Select(memory => new
                {
                    id = memory.Id,
                    scope = memory.ScopeType,
                    type = memory.MemoryType,
                    version = memory.Version,
                    score = memory.Score
                })
            }),
            EvidenceJson = JsonSerializer.Serialize(answer.Audit.Evidence.Select(item => new
            {
                item.DocumentId,
                item.VersionId,
                item.ChunkId,
                item.PageNumber,
                item.Similarity,
                item.TagIds,
                item.DocumentTitle,
                item.SourceFileName,
                item.SourceUri
            })),
            InputSummaryJson = answer.Audit.InputSummaryJson,
            CreatedAtUtc = nowUtc
        };

    private sealed class Payload { public Guid MessageId { get; init; } }
}
