using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WechatRobot.Api.Security;
using WechatRobot.Application.WorkTool;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.Api.Operations;

public static class SendCommandOperationsEndpoints
{
    private const int MaximumPageSize = 100;
    private const int MaximumFilterLength = 256;

    public static IEndpointRouteBuilder MapSendCommandOperationsEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/admin/operations/send-commands")
            .RequireAuthorization(SystemRoles.Admin)
            .RequireRateLimiting(RateLimitPolicies.Ordinary);
        group.MapGet("", ListAsync);
        group.MapPost("/{id:guid}/cancel", CancelAsync);
        group.MapPost(
            "/{id:guid}/acknowledge-unknown",
            AcknowledgeUnknownAsync);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        Guid? robotConfigId,
        string? group,
        string? status,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        int? page,
        int? pageSize,
        WechatRobotDbContext database,
        CancellationToken cancellationToken)
    {
        var actualPage = page ?? 1;
        var actualPageSize = pageSize ?? 20;
        if (actualPage < 1 ||
            actualPageSize is < 1 or > MaximumPageSize ||
            group?.Length > MaximumFilterLength ||
            status?.Length > 32 ||
            fromUtc > toUtc)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["query"] = ["The send-command query is invalid."]
            });
        }

        var groupFilter = group?.Trim();
        var statusFilter = status?.Trim();
        var query =
            from command in database.SendCommands.AsNoTracking()
            join robot in database.RobotConfigs.AsNoTracking()
                on command.RobotConfigId equals robot.Id
            where (!robotConfigId.HasValue ||
                   command.RobotConfigId == robotConfigId.Value) &&
                  (string.IsNullOrEmpty(groupFilter) ||
                   command.PayloadJson.Contains(groupFilter)) &&
                  (string.IsNullOrEmpty(statusFilter) ||
                   command.Status == statusFilter) &&
                  (!fromUtc.HasValue ||
                   command.CreatedAtUtc >= fromUtc.Value.UtcDateTime) &&
                  (!toUtc.HasValue ||
                   command.CreatedAtUtc < toUtc.Value.UtcDateTime)
            select new
            {
                Command = command,
                RobotName = robot.Name
            };

        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(item => item.Command.CreatedAtUtc)
            .ThenByDescending(item => item.Command.Id)
            .Skip((actualPage - 1) * actualPageSize)
            .Take(actualPageSize)
            .ToArrayAsync(cancellationToken);
        var items = rows.Select(item =>
        {
            var payload = ReadPayload(item.Command.PayloadJson);
            return new SendCommandItemResponse(
                item.Command.Id,
                item.Command.RobotConfigId,
                item.RobotName,
                payload.GroupName,
                item.Command.Status,
                item.Command.AttemptCount,
                item.Command.CreatedAtUtc,
                item.Command.ExternalDispatchStartedAtUtc,
                item.Command.CompletedAtUtc,
                SafeReason(item.Command),
                item.Command.Version,
                payload.MessageLength);
        }).ToArray();

        return Results.Ok(new SendCommandPageResponse(
            items,
            total,
            actualPage,
            actualPageSize));
    }

    private static Task<IResult> CancelAsync(
        Guid id,
        SendCommandMutationRequest request,
        ClaimsPrincipal principal,
        WechatRobotDbContext database,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) =>
        MutateAsync(
            id,
            request,
            principal,
            database,
            timeProvider,
            new HashSet<string>(StringComparer.Ordinal)
            {
                WorkToolCommandStatuses.Pending,
                WorkToolCommandStatuses.Retrying
            },
            WorkToolCommandStatuses.Cancelled,
            "send-command.cancel",
            cancellationToken);

    private static Task<IResult> AcknowledgeUnknownAsync(
        Guid id,
        SendCommandMutationRequest request,
        ClaimsPrincipal principal,
        WechatRobotDbContext database,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) =>
        MutateAsync(
            id,
            request,
            principal,
            database,
            timeProvider,
            new HashSet<string>(StringComparer.Ordinal)
            {
                WorkToolCommandStatuses.DeliveryUnknown
            },
            WorkToolCommandStatuses.DeliveryUnknownResolved,
            "send-command.acknowledge-unknown",
            cancellationToken);

    private static async Task<IResult> MutateAsync(
        Guid id,
        SendCommandMutationRequest request,
        ClaimsPrincipal principal,
        WechatRobotDbContext database,
        TimeProvider timeProvider,
        IReadOnlySet<string> allowedStatuses,
        string targetStatus,
        string action,
        CancellationToken cancellationToken)
    {
        if (request.ExpectedVersion < 0)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["expectedVersion"] = ["Expected version cannot be negative."]
            });
        }

        var command = await database.SendCommands.SingleOrDefaultAsync(
            item => item.Id == id,
            cancellationToken);
        if (command is null ||
            command.Version != request.ExpectedVersion ||
            !allowedStatuses.Contains(command.Status))
        {
            return Results.Conflict(new
            {
                error = "send-command-state-conflict"
            });
        }

        var actor = principal.FindFirstValue(ClaimTypes.Name);
        if (string.IsNullOrWhiteSpace(actor))
            return Results.Unauthorized();
        var sourceStatus = command.Status;
        command.Status = targetStatus;
        command.CompletedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        command.LeaseOwner = null;
        command.LeaseExpiresAtUtc = null;
        command.Version++;
        if (targetStatus == WorkToolCommandStatuses.Cancelled)
            command.ReconciliationReason = "cancelled_by_admin";
        database.AdministrationAudits.Add(new AdministrationAuditEntity
        {
            Actor = actor,
            Action = action,
            TargetType = "SendCommand",
            TargetId = command.Id.ToString("D"),
            SanitizedDetailJson = JsonSerializer.Serialize(new
            {
                commandId = command.Id,
                sourceStatus,
                targetStatus,
                previousVersion = request.ExpectedVersion
            })
        });

        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Conflict(new
            {
                error = "send-command-state-conflict"
            });
        }

        return Results.Ok(new
        {
            command.Id,
            command.Status,
            command.Version
        });
    }

    private static SendPayloadProjection ReadPayload(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var groupName = ReadString(document.RootElement, "groupName")
                ?? ReadString(document.RootElement, "GroupName")
                ?? "未记录群";
            var text = ReadString(document.RootElement, "text")
                ?? ReadString(document.RootElement, "Text")
                ?? string.Empty;
            return new(
                groupName.Length > MaximumFilterLength
                    ? groupName[..MaximumFilterLength]
                    : groupName,
                text.Length);
        }
        catch (JsonException)
        {
            return new("无法读取", 0);
        }
    }

    private static string? ReadString(
        JsonElement element,
        string propertyName) =>
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string? SafeReason(SendCommandEntity command) =>
        command.Status switch
        {
            WorkToolCommandStatuses.DeliveryUnknown =>
                "delivery_outcome_unknown",
            WorkToolCommandStatuses.DeliveryUnknownResolved =>
                "delivery_outcome_reviewed",
            WorkToolCommandStatuses.Cancelled =>
                "cancelled_by_admin",
            WorkToolCommandStatuses.ResultTimeout =>
                "execution_result_timeout",
            WorkToolCommandStatuses.DeadLetter =>
                "retry_limit_exhausted",
            _ => null
        };

    public sealed record SendCommandMutationRequest(int ExpectedVersion);

    private sealed record SendPayloadProjection(
        string GroupName,
        int MessageLength);

    private sealed record SendCommandItemResponse(
        Guid Id,
        Guid RobotConfigId,
        string RobotName,
        string GroupName,
        string Status,
        int AttemptCount,
        DateTime CreatedAtUtc,
        DateTime? ExternalDispatchStartedAtUtc,
        DateTime? CompletedAtUtc,
        string? Reason,
        int Version,
        int MessageLength);

    private sealed record SendCommandPageResponse(
        IReadOnlyList<SendCommandItemResponse> Items,
        int Total,
        int Page,
        int PageSize);
}
