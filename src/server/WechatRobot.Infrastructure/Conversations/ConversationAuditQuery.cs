using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;
using WechatRobot.Application.Audit;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.Infrastructure.Conversations;

public sealed class ConversationAuditQuery(WechatRobotDbContext database) : IConversationAuditQuery
{
    public async Task<ConversationAuditPage> ListAsync(ConversationAuditRequest request, CancellationToken token)
    {
        var query = database.RetrievalAudits.AsNoTracking();
        if (request.GroupId is { } groupId) query = query.Where(item => item.GroupProfileId == groupId);
        if (!string.IsNullOrWhiteSpace(request.ChannelType))
            query = query.Where(item => item.ChannelType == request.ChannelType);
        if (request.FromUtc is { } from) query = query.Where(item => item.CreatedAtUtc >= from);
        if (request.ToUtc is { } to) query = query.Where(item => item.CreatedAtUtc < to);

        var total = await query.CountAsync(token);
        var audits = await query.OrderByDescending(item => item.CreatedAtUtc).ThenByDescending(item => item.Id)
            .Skip((request.Page - 1) * request.PageSize).Take(request.PageSize).ToArrayAsync(token);
        if (audits.Length == 0) return new([], total, request.Page, request.PageSize);

        var messageIds = audits.Select(item => item.ConversationMessageId).Distinct().ToArray();
        var messages = await database.ConversationMessages.AsNoTracking()
            .Where(MessagePredicate(messageIds))
            .ToArrayAsync(token);
        var questions = messages.Where(item => messageIds.Contains(item.Id)).ToDictionary(item => item.Id);
        var answers = messages.Where(item => item.InReplyToMessageId is not null && item.Direction == "outbound")
            .GroupBy(item => item.InReplyToMessageId!.Value)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.CreatedAtUtc).First());

        var sendKeys = messageIds
            .SelectMany(item => new[] { $"grounded-reply:{item:D}", $"private-reply:{item:D}" })
            .ToArray();
        var sends = await database.SendCommands.AsNoTracking().Where(SendKeyPredicate(sendKeys)).ToArrayAsync(token);
        var sendByKey = sends.ToDictionary(item => item.IdempotencyKey, StringComparer.Ordinal);

        var candidates = await database.KnowledgeCandidates.AsNoTracking()
            .Where(GuidBatchQuery.BuildPredicate<KnowledgeCandidateEntity>(messageIds, item => item.QuestionMessageId))
            .ToArrayAsync(token);
        var candidateByMessage = candidates
            .GroupBy(item => item.QuestionMessageId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.UpdatedAtUtc).First());

        var items = audits.Select(audit =>
        {
            var question = questions[audit.ConversationMessageId];
            answers.TryGetValue(question.Id, out var answer);
            var sendKey = string.Equals(audit.ChannelType, "Private", StringComparison.Ordinal)
                ? $"private-reply:{question.Id:D}"
                : $"grounded-reply:{question.Id:D}";
            sendByKey.TryGetValue(sendKey, out var send);
            candidateByMessage.TryGetValue(question.Id, out var candidate);
            return new ConversationAuditItem(
                audit.Id, audit.GroupProfileId, audit.ChannelType, question.Id, audit.ModelConfigurationId, question.WorkToolMessageId, question.Text, answer?.Text,
                audit.Decision, audit.ConfidenceThreshold, audit.ConfidenceValue, audit.ContextPolicy, audit.FailureCode,
                audit.AnswerSource, audit.WebSearchFailureCode, audit.WebSearchSourcesJson,
                audit.MemoryRecallJson, audit.EvidenceJson, audit.InputSummaryJson,
                send is null ? null : new(send.Status, send.AttemptCount, send.SentAtUtc, send.CompletedAtUtc),
                candidate is null ? null : new(candidate.Status, candidate.KnowledgeDocumentVersionId, candidate.PublishedAtUtc,
                    candidate.CreatedAtUtc, candidate.UpdatedAtUtc),
                audit.CreatedAtUtc);
        }).ToArray();
        return new(items, total, request.Page, request.PageSize);
    }

    private static Expression<Func<SendCommandEntity, bool>> SendKeyPredicate(IReadOnlyCollection<string> keys)
    {
        var parameter = Expression.Parameter(typeof(SendCommandEntity), "send");
        var property = Expression.Property(parameter, nameof(SendCommandEntity.IdempotencyKey));
        Expression body = Expression.Constant(false);
        foreach (var key in keys)
            body = Expression.OrElse(body, Expression.Equal(property, Expression.Constant(key)));
        return Expression.Lambda<Func<SendCommandEntity, bool>>(body, parameter);
    }

    private static Expression<Func<ConversationMessageEntity, bool>> MessagePredicate(IReadOnlyCollection<Guid> ids)
    {
        var parameter = Expression.Parameter(typeof(ConversationMessageEntity), "message");
        var idProperty = Expression.Property(parameter, nameof(ConversationMessageEntity.Id));
        var replyProperty = Expression.Property(parameter, nameof(ConversationMessageEntity.InReplyToMessageId));
        Expression body = Expression.Constant(false);
        foreach (var id in ids)
        {
            body = Expression.OrElse(body, Expression.Equal(idProperty, Expression.Constant(id)));
            body = Expression.OrElse(body, Expression.Equal(replyProperty, Expression.Constant((Guid?)id, typeof(Guid?))));
        }
        return Expression.Lambda<Func<ConversationMessageEntity, bool>>(body, parameter);
    }
}
