using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.Infrastructure.Logging;
using WechatRobot.Infrastructure.Persistence;

namespace WechatRobot.Api.Audit;

public static class AdministrationAuditEndpoints
{
    public static IEndpointRouteBuilder MapAdministrationAuditEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/admin/administration-audits", ListAsync)
            .RequireAuthorization(SystemRoles.Admin);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        string? actor,
        string? action,
        string? targetType,
        string? targetId,
        DateTime? fromUtc,
        DateTime? toUtc,
        int? page,
        int? pageSize,
        WechatRobotDbContext database,
        CancellationToken cancellationToken)
    {
        if (!Pagination.TryNormalize(
                page ?? 0,
                pageSize ?? 0,
                out var normalizedPage,
                out var normalizedPageSize,
                out var skip))
            return TypedResults.BadRequest(new { error = "Page must not exceed 1000000." });
        if (fromUtc is not null && toUtc is not null && fromUtc >= toUtc)
            return TypedResults.BadRequest(new { error = "Administration audit UTC window is invalid." });
        if (new[] { actor, action, targetType, targetId }
            .Any(value => value?.Length > 256))
            return TypedResults.BadRequest(new { error = "Administration audit filter is too long." });

        var query = database.AdministrationAudits.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(actor))
            query = query.Where(item => item.Actor.Contains(actor.Trim()));
        if (!string.IsNullOrWhiteSpace(action))
            query = query.Where(item => item.Action == action.Trim());
        if (!string.IsNullOrWhiteSpace(targetType))
            query = query.Where(item => item.TargetType == targetType.Trim());
        if (!string.IsNullOrWhiteSpace(targetId))
            query = query.Where(item => item.TargetId == targetId.Trim());
        if (fromUtc is { } from)
            query = query.Where(item => item.CreatedAtUtc >= from);
        if (toUtc is { } to)
            query = query.Where(item => item.CreatedAtUtc < to);

        var total = await query.CountAsync(cancellationToken);
        var selected = await query
            .OrderByDescending(item => item.CreatedAtUtc)
            .ThenByDescending(item => item.Id)
            .Skip(skip)
            .Take(normalizedPageSize)
            .Select(item => new
            {
                item.Id,
                item.Actor,
                item.Action,
                item.TargetType,
                item.TargetId,
                item.SanitizedDetailJson,
                item.CreatedAtUtc
            })
            .ToArrayAsync(cancellationToken);
        var items = selected.Select(item => new
        {
            item.Id,
            item.Actor,
            item.Action,
            item.TargetType,
            item.TargetId,
            detail = SafeDetail(item.SanitizedDetailJson),
            item.CreatedAtUtc
        }).ToArray();
        return TypedResults.Ok(new
        {
            items,
            total,
            page = normalizedPage,
            pageSize = normalizedPageSize
        });
    }

    private static JsonNode? SafeDetail(string json)
    {
        try
        {
            var node = JsonNode.Parse(RedactionEnricher.RedactMessage(json));
            RemoveUrlFields(node);
            return node;
        }
        catch (JsonException)
        {
            return JsonValue.Create("[INVALID_SANITIZED_DETAIL]");
        }
    }

    private static void RemoveUrlFields(JsonNode? node)
    {
        if (node is JsonObject item)
        {
            foreach (var property in item.ToList())
            {
                if (property.Key.EndsWith("url", StringComparison.OrdinalIgnoreCase) ||
                    property.Key.EndsWith("uri", StringComparison.OrdinalIgnoreCase))
                    item.Remove(property.Key);
                else
                    RemoveUrlFields(property.Value);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
                RemoveUrlFields(child);
        }
    }
}
