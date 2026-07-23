using System.Text.Json.Nodes;
using WechatRobot.Application.Audit;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.Infrastructure.Logging;

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
        IConversationAuditQuery query,
        CancellationToken token)
    {
        if (!Pagination.TryNormalize(page ?? 0, pageSize ?? 0, out var normalizedPage, out var normalizedPageSize, out _))
            return TypedResults.BadRequest(new { error = "Page must not exceed 1000000." });
        if (fromUtc is not null && toUtc is not null && fromUtc >= toUtc)
            return TypedResults.BadRequest(new { error = "Audit UTC window is invalid." });

        var result = await query.ListAsync(new(groupId, fromUtc, toUtc, normalizedPage, normalizedPageSize), token);
        var items = result.Items.Select(item =>
        {
            var evidence = SafeJson(item.EvidenceJson);
            return new
            {
                item.Id,
                item.GroupProfileId,
                item.WorkToolMessageId,
                item.Question,
                item.Answer,
                item.Decision,
                item.ConfidenceThreshold,
                item.ConfidenceValue,
                item.ContextPolicy,
                item.FailureCode,
                sources = Sources(evidence),
                evidence,
                inputSummary = SafeJson(item.InputSummaryJson),
                send = item.Send,
                handoff = item.Handoff is null ? null : new
                {
                    item.Handoff.State, item.Handoff.ReasonCode, item.Handoff.PauseScope,
                    evidence = SafeJson(item.Handoff.EvidenceJson), item.Handoff.CreatedAtUtc, item.Handoff.UpdatedAtUtc,
                    item.Handoff.Transitions
                },
                item.KnowledgeCandidate,
                item.CreatedAtUtc
            };
        }).ToArray();

        return TypedResults.Ok(new { items, result.Total, result.Page, result.PageSize });
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
                    ScalarValue(item["title"])
                    ?? ScalarValue(item["documentId"])
                    ?? ScalarValue(item["chunkId"]))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => RedactionEnricher.RedactMessage(value!))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

    private static string? ScalarValue(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<string>(out var text)) return text;
        if (value.TryGetValue<Guid>(out var id)) return id.ToString("D");
        return value.ToString();
    }
}
