using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Agents;
using WechatRobot.Infrastructure.Persistence;

namespace WechatRobot.Infrastructure.Agents;

public sealed class MessageIntentDiagnosticsQuery(WechatRobotDbContext database)
    : IMessageIntentDiagnosticsQuery
{
    public async Task<MessageIntentDiagnosticsPage> ListAsync(
        MessageIntentDiagnosticsRequest request,
        CancellationToken cancellationToken)
    {
        var query = database.MessageIntentAudits.AsNoTracking();
        if (request.GroupProfileId is { } groupId)
        {
            query = query.Where(item => item.GroupProfileId == groupId);
        }
        if (request.RuntimeMode is { } runtimeMode)
        {
            var value = runtimeMode.ToString();
            query = query.Where(item => item.IntentRuntimeMode == value);
        }
        if (request.Decision is { } decision)
        {
            var value = decision.ToString();
            query = query.Where(item => item.IntentDecision == value);
        }
        if (request.FromUtc is { } fromUtc)
        {
            query = query.Where(item => item.IntentDecidedAtUtc >= fromUtc);
        }
        if (request.ToUtc is { } toUtc)
        {
            query = query.Where(item => item.IntentDecidedAtUtc < toUtc);
        }
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 100);
        var total = await query.CountAsync(cancellationToken);
        var rows = await (
            from audit in query
            join message in database.ConversationMessages.AsNoTracking()
                on audit.ConversationMessageId equals message.Id
            join profile in database.GroupProfiles.AsNoTracking()
                on audit.GroupProfileId equals profile.Id
            orderby audit.IntentDecidedAtUtc descending, audit.Id descending
            select new { audit, message, profile })
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToArrayAsync(cancellationToken);
        return new(
            rows.Select(row => new MessageIntentDiagnosticsItem(
                row.audit.Id,
                row.audit.ConversationMessageId,
                row.audit.GroupProfileId,
                row.profile.Name,
                row.message.SenderDisplayName,
                Enum.Parse<IntentDecision>(row.audit.IntentDecision),
                Enum.Parse<IntentCategory>(row.audit.IntentCategory),
                row.audit.IntentReasonCode,
                row.audit.IntentConfidence,
                row.audit.FailureCode,
                Enum.Parse<IntentRuntimeMode>(row.audit.IntentRuntimeMode),
                row.audit.IntentAgentVersion,
                row.audit.IntentModelConfigurationId,
                row.audit.IntentModelVersion,
                row.audit.IntentLatencyMilliseconds,
                row.audit.FormalConversationIncluded,
                row.audit.IntentDecidedAtUtc))
                .ToArray(),
            total,
            page,
            pageSize);
    }
}
