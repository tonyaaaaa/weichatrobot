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

        var handoffs = await database.HandoffCases.AsNoTracking()
            .Where(GuidBatchQuery.BuildPredicate<HandoffCaseEntity>(messageIds, item => item.QuestionMessageId)).ToArrayAsync(token);
        var handoffByMessage = handoffs.ToDictionary(item => item.QuestionMessageId);
        var sendKeys = messageIds.Select(item => $"grounded-reply:{item:D}")
            .Concat(handoffs.Select(item => item.StartIdempotencyKey).Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item!))
            .Distinct(StringComparer.Ordinal).ToArray();
        var sends = await database.SendCommands.AsNoTracking().Where(SendKeyPredicate(sendKeys)).ToArrayAsync(token);
        var sendByKey = sends.ToDictionary(item => item.IdempotencyKey, StringComparer.Ordinal);

        var handoffIds = handoffs.Select(item => item.Id).ToArray();
        var transitions = handoffIds.Length == 0 ? [] : await database.HandoffTransitions.AsNoTracking()
            .Where(GuidBatchQuery.BuildPredicate<HandoffTransitionEntity>(handoffIds, item => item.HandoffCaseId)).OrderBy(item => item.Sequence).ToArrayAsync(token);
        var transitionsByHandoff = transitions.GroupBy(item => item.HandoffCaseId).ToDictionary(group => group.Key,
            group => (IReadOnlyList<ConversationAuditTransition>)group.Select(item =>
                new ConversationAuditTransition(item.Sequence, item.FromState, item.ToState, item.ReasonCode, item.CreatedAtUtc)).ToArray());
        var candidates = handoffIds.Length == 0 ? [] : await database.KnowledgeCandidates.AsNoTracking()
            .Where(GuidBatchQuery.BuildPredicate<KnowledgeCandidateEntity>(handoffIds, item => item.HandoffCaseId)).ToArrayAsync(token);
        var candidateByHandoff = candidates.ToDictionary(item => item.HandoffCaseId);

        var items = audits.Select(audit =>
        {
            var question = questions[audit.ConversationMessageId];
            answers.TryGetValue(question.Id, out var answer);
            handoffByMessage.TryGetValue(question.Id, out var handoff);
            var sendKey = $"grounded-reply:{question.Id:D}";
            if (!sendByKey.TryGetValue(sendKey, out var send) && handoff?.StartIdempotencyKey is { Length: > 0 } handoffKey)
                sendByKey.TryGetValue(handoffKey, out send);
            var handoffResult = handoff is null ? null : new ConversationAuditHandoff(
                handoff.State, handoff.ReasonCode, handoff.PauseScope, handoff.EvidenceJson, handoff.CreatedAtUtc, handoff.UpdatedAtUtc,
                transitionsByHandoff.GetValueOrDefault(handoff.Id, []));
            candidateByHandoff.TryGetValue(handoff?.Id ?? Guid.Empty, out var candidate);
            return new ConversationAuditItem(
                audit.Id, audit.GroupProfileId, question.Id, question.WorkToolMessageId, question.Text, answer?.Text,
                audit.Decision, audit.ConfidenceThreshold, audit.ConfidenceValue, audit.ContextPolicy, audit.FailureCode,
                audit.EvidenceJson, audit.InputSummaryJson,
                send is null ? null : new(send.Status, send.AttemptCount, send.SentAtUtc, send.CompletedAtUtc),
                handoffResult,
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
