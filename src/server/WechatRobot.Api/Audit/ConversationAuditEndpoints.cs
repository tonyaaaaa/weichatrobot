using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.Infrastructure.Logging;
using WechatRobot.Infrastructure.Persistence;

namespace WechatRobot.Api.Audit;

public static class ConversationAuditEndpoints
{
    public static IEndpointRouteBuilder MapConversationAuditEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/audit/conversations", ListAsync)
            .RequireAuthorization(SystemRoles.KnowledgeOperator);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        Guid? groupId,
        DateTime? fromUtc,
        DateTime? toUtc,
        int? page,
        int? pageSize,
        WechatRobotDbContext db,
        CancellationToken token)
    {
        if (!Pagination.TryNormalize(page ?? 0, pageSize ?? 0, out var normalizedPage, out var normalizedPageSize, out var skip))
            return TypedResults.BadRequest(new { error = "Page must not exceed 1000000." });
        if (fromUtc is not null && toUtc is not null && fromUtc >= toUtc)
            return TypedResults.BadRequest(new { error = "Audit UTC window is invalid." });

        var query = db.RetrievalAudits.AsNoTracking();
        if (groupId is { } id) query = query.Where(item => item.GroupProfileId == id);
        if (fromUtc is { } from) query = query.Where(item => item.CreatedAtUtc >= from);
        if (toUtc is { } to) query = query.Where(item => item.CreatedAtUtc < to);
        var total = await query.CountAsync(token);
        var audits = await query.OrderByDescending(item => item.CreatedAtUtc).ThenByDescending(item => item.Id)
            .Skip(skip).Take(normalizedPageSize).ToArrayAsync(token);
        var items = new List<object>(audits.Length);

        foreach (var audit in audits)
        {
            var question = await db.ConversationMessages.AsNoTracking().SingleAsync(item => item.Id == audit.ConversationMessageId, token);
            var answer = await db.ConversationMessages.AsNoTracking()
                .Where(item => item.InReplyToMessageId == question.Id && item.Direction == "outbound")
                .OrderByDescending(item => item.CreatedAtUtc).FirstOrDefaultAsync(token);
            var send = await db.SendCommands.AsNoTracking()
                .Where(item => item.GroupProfileId == audit.GroupProfileId && item.CreatedAtUtc >= audit.CreatedAtUtc.AddMinutes(-1))
                .OrderBy(item => item.CreatedAtUtc).FirstOrDefaultAsync(token);
            var handoff = await db.HandoffCases.AsNoTracking().SingleOrDefaultAsync(item => item.QuestionMessageId == question.Id, token);
            var transitions = handoff is null
                ? []
                : await db.HandoffTransitions.AsNoTracking().Where(item => item.HandoffCaseId == handoff.Id)
                    .OrderBy(item => item.Sequence).Select(item => new { item.Sequence, item.FromState, item.ToState, item.ReasonCode, item.CreatedAtUtc }).ToArrayAsync(token);
            var candidate = handoff is null
                ? null
                : await db.KnowledgeCandidates.AsNoTracking().SingleOrDefaultAsync(item => item.HandoffCaseId == handoff.Id, token);
            var evidence = SafeJson(audit.EvidenceJson);
            items.Add(new
            {
                audit.Id,
                audit.GroupProfileId,
                question.WorkToolMessageId,
                question = question.Text,
                answer = answer?.Text,
                audit.Decision,
                audit.ConfidenceThreshold,
                audit.ConfidenceValue,
                audit.ContextPolicy,
                audit.FailureCode,
                sources = Sources(evidence),
                evidence,
                inputSummary = SafeJson(audit.InputSummaryJson),
                send = send is null ? null : new { send.Status, send.AttemptCount, send.SentAtUtc, send.CompletedAtUtc },
                handoff = handoff is null ? null : new
                {
                    handoff.State, handoff.ReasonCode, handoff.PauseScope,
                    evidence = SafeJson(handoff.EvidenceJson), handoff.CreatedAtUtc, handoff.UpdatedAtUtc,
                    transitions
                },
                knowledgeCandidate = candidate is null ? null : new
                {
                    candidate.Status, candidate.KnowledgeDocumentVersionId, candidate.PublishedAtUtc,
                    candidate.CreatedAtUtc, candidate.UpdatedAtUtc
                },
                audit.CreatedAtUtc
            });
        }

        return TypedResults.Ok(new { items, total, page = normalizedPage, pageSize = normalizedPageSize });
    }

    private static JsonNode? SafeJson(string json)
    {
        var redacted = RedactionEnricher.RedactMessage(json);
        try
        {
            var node = JsonNode.Parse(redacted);
            RemoveUrls(node);
            return node;
        }
        catch (System.Text.Json.JsonException) { return JsonValue.Create("[INVALID_EVIDENCE]"); }
    }

    private static void RemoveUrls(JsonNode? node)
    {
        if (node is JsonObject item)
        {
            foreach (var property in item.ToList())
            {
                if (property.Key.EndsWith("url", StringComparison.OrdinalIgnoreCase)
                    || property.Key.EndsWith("uri", StringComparison.OrdinalIgnoreCase))
                    item.Remove(property.Key);
                else RemoveUrls(property.Value);
            }
        }
        else if (node is JsonArray array)
            foreach (var child in array) RemoveUrls(child);
    }

    private static string[] Sources(JsonNode? evidence) =>
        evidence is not JsonArray array
            ? []
            : array.OfType<JsonObject>().Select(item =>
                    item["title"]?.GetValue<string>()
                    ?? item["documentId"]?.ToJsonString()
                    ?? item["chunkId"]?.ToJsonString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => RedactionEnricher.RedactMessage(value!))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
}
