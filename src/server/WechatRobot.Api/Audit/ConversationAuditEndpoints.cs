using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Audit;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.Infrastructure.Groups;
using WechatRobot.Infrastructure.Logging;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using System.Security.Claims;

namespace WechatRobot.Api.Audit;

public static class ConversationAuditEndpoints
{
    public static IEndpointRouteBuilder MapConversationAuditEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/audit/conversations", ListAsync)
            .RequireAuthorization(SystemRoles.KnowledgeOperator);
        endpoints.MapGet("/api/audit/group-options", GroupOptionsAsync)
            .RequireAuthorization(SystemRoles.KnowledgeOperator);
        endpoints.MapPost("/api/audit/conversations/{id:guid}/knowledge-candidate", CreateKnowledgeCandidateAsync)
            .RequireAuthorization(SystemRoles.KnowledgeOperator);
        return endpoints;
    }

    private static async Task<IResult> CreateKnowledgeCandidateAsync(
        Guid id,
        ManualKnowledgeCandidateRequest request,
        ClaimsPrincipal principal,
        WechatRobotDbContext database,
        TimeProvider timeProvider,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.Answer) || request.Answer.Trim().Length > 10000)
            return TypedResults.BadRequest(new { error = "The corrected answer is invalid." });
        var source = await (
            from audit in database.RetrievalAudits
            join message in database.ConversationMessages on audit.ConversationMessageId equals message.Id
            where audit.Id == id
            select new { Audit = audit, Message = message })
            .SingleOrDefaultAsync(token);
        if (source is null) return TypedResults.NotFound();
        var existing = await database.KnowledgeCandidates.AsNoTracking()
            .SingleOrDefaultAsync(x => x.SourceType == "ManualCorrection" &&
                                       x.SourceConversationMessageId == source.Message.Id, token);
        if (existing is not null)
            return TypedResults.Conflict(new { error = "A manual correction candidate already exists for this conversation." });

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var candidate = new KnowledgeCandidateEntity
        {
            HandoffCaseId = null,
            QuestionMessageId = source.Message.Id,
            SourceConversationMessageId = source.Message.Id,
            SourceType = "ManualCorrection",
            Question = source.Message.Text,
            Answer = request.Answer.Trim(),
            EvidenceJson = source.Audit.EvidenceJson,
            Status = "pending",
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        database.KnowledgeCandidates.Add(candidate);
        database.AdministrationAudits.Add(new AdministrationAuditEntity
        {
            Actor = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown",
            Action = "knowledge.candidate.manual-correction",
            TargetType = "KnowledgeCandidate",
            TargetId = candidate.Id.ToString("D"),
            SanitizedDetailJson = System.Text.Json.JsonSerializer.Serialize(new { auditId = id }),
            CreatedAtUtc = now
        });
        await database.SaveChangesAsync(token);
        return TypedResults.Ok(new { candidate.Id, candidate.Status, candidate.Version });
    }

    private static async Task<IResult> GroupOptionsAsync(
        GroupOptionQuery query,
        CancellationToken token)
        => TypedResults.Ok(await query.ListAsync(token));

    private static async Task<IResult> ListAsync(
        Guid? groupId,
        string? channelType,
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
        if (channelType is not null && channelType is not ("Group" or "Private"))
            return TypedResults.BadRequest(new { error = "Audit channel type is invalid." });

        var result = await query.ListAsync(new(groupId, channelType, fromUtc, toUtc, normalizedPage, normalizedPageSize), token);
        var items = result.Items.Select(item =>
        {
            var evidence = SafeJson(item.EvidenceJson);
            return new
            {
                item.Id,
                item.GroupProfileId,
                item.ChannelType,
                item.ModelConfigurationId,
                item.WorkToolMessageId,
                item.Question,
                item.Answer,
                item.Decision,
                item.ConfidenceThreshold,
                item.ConfidenceValue,
                item.ContextPolicy,
                item.FailureCode,
                item.AnswerSource,
                item.WebSearchFailureCode,
                webSearchSources = SafeWebSources(item.WebSearchSourcesJson),
                memoryRecall = SafeJson(item.MemoryRecallJson),
                sources = Sources(evidence),
                evidence,
                inputSummary = SafeJson(item.InputSummaryJson),
                send = item.Send,
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

    private static WebSourceResponse[] SafeWebSources(string json)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
                return [];
            var results = new List<WebSourceResponse>();
            foreach (var source in document.RootElement.EnumerateArray().Take(20))
            {
                var rawUrl = JsonString(source, "url");
                if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var url)
                    || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
                    || !string.IsNullOrEmpty(url.UserInfo)
                    || url.AbsoluteUri.Length > 2048)
                    continue;
                var safeUrl = RedactionEnricher.RedactMessage(url.AbsoluteUri);
                if (safeUrl.Contains("[REDACTED]", StringComparison.Ordinal))
                    continue;
                var title = RedactionEnricher.RedactMessage(
                    JsonString(source, "title") ?? url.Host);
                if (title.Length > 256) title = title[..256];
                results.Add(new(
                    title,
                    safeUrl,
                    Bound(JsonString(source, "site"), 128),
                    Bound(JsonString(source, "publishedAt"), 64),
                    source.TryGetProperty("index", out var index)
                        && index.TryGetInt32(out var parsedIndex)
                            ? parsedIndex
                            : results.Count + 1));
            }
            return results.ToArray();
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private static string? JsonString(System.Text.Json.JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? Bound(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var redacted = RedactionEnricher.RedactMessage(value.Trim());
        return redacted.Length <= maximumLength ? redacted : redacted[..maximumLength];
    }

    private sealed record WebSourceResponse(
        string Title,
        string Url,
        string? Site,
        string? PublishedAt,
        int Index);
}

public sealed record ManualKnowledgeCandidateRequest(string Answer);
