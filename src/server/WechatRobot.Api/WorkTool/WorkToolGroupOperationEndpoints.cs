using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.WorkTool;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.Api.WorkTool;

public static class WorkToolGroupOperationEndpoints
{
    private static readonly TimeSpan ConfirmationLifetime = TimeSpan.FromMinutes(2);

    public static IEndpointRouteBuilder MapWorkToolGroupOperationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/admin/worktool").RequireAuthorization(SystemRoles.Admin);
        group.MapGet("/robots", ListRobotsAsync);
        group.MapPut("/robots/{id:guid}", UpsertRobotAsync);
        group.MapPost("/robots/{id:guid}/test-connection", TestRobotConnectionAsync);
        group.MapGet("/groups", ListGroupsAsync);
        group.MapPost("/groups/register", RegisterExistingGroupAsync);
        group.MapGet("/group-operations", ListOperationsAsync);
        group.MapGet("/group-operations/audit-scope", () => Results.Ok(new AuditScopeResponse("Mutating WorkTool group commands only (206 create and 207 group changes). Connection tests are non-mutating health checks and are not group-command audit records.")));
        group.MapPost("/group-operations/preview", PreviewAsync);
        group.MapPost("/group-operations/execute", ExecuteAsync);
        return endpoints;
    }

    private static async Task<IResult> ListRobotsAsync(WechatRobotDbContext database, CancellationToken cancellationToken)
    {
        var robots = await database.RobotConfigs.AsNoTracking().OrderBy(robot => robot.Name).ToArrayAsync(cancellationToken);
        return Results.Ok(robots.Select(robot => new RobotResponse(robot.Id, robot.Name, MaskRobotId(robot.WorkToolRobotId), robot.IsEnabled)));
    }

    private static async Task<IResult> UpsertRobotAsync(Guid id, UpdateRobotRequest request, WechatRobotDbContext database, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 128 || string.IsNullOrWhiteSpace(request.WorkToolRobotId) || request.WorkToolRobotId.Length > 128)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["robot"] = ["Robot name and WorkTool robot ID are required."] });

        await using var sendGate = await MySqlRobotSendLock.AcquireAsync(database, id, cancellationToken);
        await using var transaction = database.Database.IsRelational() ? await database.Database.BeginTransactionAsync(cancellationToken) : null;
        var robot = await database.RobotConfigs.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        var wasEnabled = robot?.IsEnabled;
        if (robot is null)
        {
            robot = new RobotConfigEntity { Id = id, CallbackSecretHash = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)) };
            database.RobotConfigs.Add(robot);
        }
        robot.Name = request.Name.Trim(); robot.WorkToolRobotId = request.WorkToolRobotId.Trim(); robot.IsEnabled = request.IsEnabled;
        robot.UpdatedAtUtc = DateTime.UtcNow;
        await database.SaveChangesAsync(cancellationToken);
        if (wasEnabled == true && !request.IsEnabled)
        {
            await database.SendCommands.Where(command => command.RobotConfigId == id &&
                    (command.Status == "pending" || command.Status == "retrying" || command.Status == "leased"))
                .ExecuteUpdateAsync(setters => setters.SetProperty(command => command.Status, "blocked")
                    .SetProperty(command => command.LeaseOwner, (string?)null).SetProperty(command => command.LeaseExpiresAtUtc, (DateTime?)null)
                    .SetProperty(command => command.Version, command => command.Version + 1), cancellationToken);
            robot.SendLeaseOwner = null; robot.SendLeaseExpiresAtUtc = null; robot.SendCoordinationVersion++;
            await database.SaveChangesAsync(cancellationToken);
        }
        else if (wasEnabled == false && request.IsEnabled)
        {
            var now = DateTime.UtcNow;
            await database.SendCommands.Where(command => command.RobotConfigId == id && command.Status == "blocked")
                .ExecuteUpdateAsync(setters => setters.SetProperty(command => command.Status, "pending")
                    .SetProperty(command => command.NextAttemptAtUtc, now).SetProperty(command => command.LeaseOwner, (string?)null)
                    .SetProperty(command => command.LeaseExpiresAtUtc, (DateTime?)null)
                    .SetProperty(command => command.Version, command => command.Version + 1), cancellationToken);
        }
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return Results.Ok(new RobotResponse(robot.Id, robot.Name, MaskRobotId(robot.WorkToolRobotId), robot.IsEnabled));
    }

    private static async Task<IResult> TestRobotConnectionAsync(Guid id, WechatRobotDbContext database, IWorkToolClient client, CancellationToken cancellationToken)
    {
        var robot = await database.RobotConfigs.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (robot is null) return Results.NotFound();
        try
        {
            var result = await client.TestConnectionAsync(robot.WorkToolRobotId, cancellationToken);
            return result.Succeeded ? Results.Ok(new CommandStatusResponse(true, "Connection test succeeded.")) : Results.Problem("WorkTool connection test failed.", statusCode: 502);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { return Results.Problem("WorkTool connection test failed.", statusCode: 502); }
    }

    private static async Task<IResult> ListGroupsAsync(WechatRobotDbContext database, CancellationToken cancellationToken) =>
        Results.Ok(await database.GroupProfiles.AsNoTracking().OrderBy(group => group.Name).Select(group => new KnownGroupResponse(group.Id, group.RobotConfigId, group.ExternalGroupId, group.Name)).ToArrayAsync(cancellationToken));

    private static async Task<IResult> RegisterExistingGroupAsync(RegisterExistingGroupRequest request, WechatRobotDbContext database, CancellationToken cancellationToken)
    {
        if (!request.ManualInvitationCompleted || string.IsNullOrWhiteSpace(request.ExternalGroupId) || string.IsNullOrWhiteSpace(request.Name))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["group"] = ["A human must first invite the robot in Enterprise WeChat before registering an existing group."] });
        if (!await database.RobotConfigs.AnyAsync(robot => robot.Id == request.RobotConfigId && robot.IsEnabled, cancellationToken)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["robotConfigId"] = ["Enabled robot was not found."] });
        var existing = await database.GroupProfiles.SingleOrDefaultAsync(group => group.RobotConfigId == request.RobotConfigId && group.ExternalGroupId == request.ExternalGroupId.Trim(), cancellationToken);
        if (existing is null) { existing = new GroupProfileEntity { RobotConfigId = request.RobotConfigId, ExternalGroupId = request.ExternalGroupId.Trim(), Name = request.Name.Trim() }; database.GroupProfiles.Add(existing); await database.SaveChangesAsync(cancellationToken); }
        return Results.Ok(new KnownGroupResponse(existing.Id, existing.RobotConfigId, existing.ExternalGroupId, existing.Name));
    }

    private static async Task<IResult> ListOperationsAsync(WechatRobotDbContext database, CancellationToken cancellationToken) => Results.Ok(await database.WorkToolOperationAudits.AsNoTracking().OrderByDescending(item => item.CreatedAtUtc).Take(100).Select(item => new AuditResponse(item.Id, item.Operation, item.WorkToolCommandNumber, item.Status, item.Result, item.CreatedAtUtc, item.SanitizedRequestJson)).ToArrayAsync(cancellationToken));

    private static async Task<IResult> PreviewAsync(GroupOperationRequest request, ClaimsPrincipal user, WechatRobotDbContext database, GroupOperationConfirmationService confirmation, CancellationToken cancellationToken)
    {
        if (!TryBuild(request, out var operation, out var sanitized, out var error)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["operation"] = [error!] });
        var operatorName = RequireOperator(user);
        var now = DateTime.UtcNow;
        var token = confirmation.Issue(operatorName, sanitized, now, ConfirmationLifetime);
        database.WorkToolOperationConfirmations.Add(new WorkToolOperationConfirmationEntity { TokenHash = Hash(token), OperatorName = operatorName, PayloadHash = Hash(sanitized), ExpiresAtUtc = now.Add(ConfirmationLifetime) });
        database.WorkToolOperationAudits.Add(NewAudit(operatorName, operation.Kind, sanitized, "Previewed", "Confirmation required."));
        await database.SaveChangesAsync(cancellationToken);
        return Results.Ok(new PreviewResponse(sanitized, token, now.Add(ConfirmationLifetime)));
    }

    private static async Task<IResult> ExecuteAsync(ExecuteOperationRequest request, ClaimsPrincipal user, WechatRobotDbContext database, GroupOperationConfirmationService confirmation, IWorkToolClient client, CancellationToken cancellationToken)
    {
        if (!TryBuild(request.Operation, out var operation, out var sanitized, out var error)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["operation"] = [error!] });
        var operatorName = RequireOperator(user);
        var now = DateTime.UtcNow;
        var confirmationRow = await database.WorkToolOperationConfirmations.SingleOrDefaultAsync(item => item.TokenHash == Hash(request.ConfirmationToken), cancellationToken);
        var valid = confirmationRow is not null && confirmationRow.ConsumedAtUtc is null && confirmationRow.ExpiresAtUtc >= now && confirmationRow.OperatorName == operatorName && confirmationRow.PayloadHash == Hash(sanitized) && confirmation.IsValid(request.ConfirmationToken, operatorName, sanitized, now);
        if (!valid) { database.WorkToolOperationAudits.Add(NewAudit(operatorName, operation.Kind, sanitized, "Rejected", "Invalid, changed, expired, or already-used confirmation.")); await database.SaveChangesAsync(cancellationToken); return Results.BadRequest(new { error = "Confirmation token is invalid, expired, already used, or does not match this request." }); }
        var robot = await database.RobotConfigs.AsNoTracking().SingleOrDefaultAsync(item => item.Id == request.Operation.RobotConfigId && item.IsEnabled, cancellationToken);
        if (robot is null) { database.WorkToolOperationAudits.Add(NewAudit(operatorName, operation.Kind, sanitized, "Rejected", "Enabled robot was not found.")); await database.SaveChangesAsync(cancellationToken); return Results.ValidationProblem(new Dictionary<string, string[]> { ["robotConfigId"] = ["Enabled robot was not found."] }); }
        confirmationRow!.ConsumedAtUtc = now; confirmationRow.Version++;
        var audit = NewAudit(operatorName, operation.Kind, sanitized, "Pending", null); database.WorkToolOperationAudits.Add(audit);
        try { await database.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { database.Entry(confirmationRow).State = EntityState.Detached; database.Entry(audit).State = EntityState.Detached; database.WorkToolOperationAudits.Add(NewAudit(operatorName, operation.Kind, sanitized, "Rejected", "Confirmation was already used.")); await database.SaveChangesAsync(cancellationToken); return Results.BadRequest(new { error = "Confirmation token was already used." }); }
        try { var result = await client.ExecuteGroupOperationAsync(operation with { WorkToolRobotId = robot.WorkToolRobotId }, cancellationToken); audit.Status = result.Succeeded ? "Succeeded" : "Failed"; audit.Result = result.Succeeded ? null : "WorkTool rejected the command."; await database.SaveChangesAsync(cancellationToken); return Results.Ok(new CommandStatusResponse(result.Succeeded, result.Succeeded ? "Command accepted." : "WorkTool rejected the command.")); }
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { audit.Status = "Failed"; audit.Result = "WorkTool request failed."; await database.SaveChangesAsync(cancellationToken); return Results.Problem("WorkTool request failed.", statusCode: 502); }
    }

    private static bool TryBuild(GroupOperationRequest request, out WorkToolGroupOperationRequest operation, out string sanitized, out string? error)
    {
        operation = default!; sanitized = string.Empty; error = null;
        if (!Enum.TryParse<WorkToolGroupOperationKind>(request.Kind, true, out var kind) || string.IsNullOrWhiteSpace(request.GroupIdentifier) || request.GroupIdentifier.Length > 256 || request.MemberIds.Count > 100 || request.MemberIds.Any(member => string.IsNullOrWhiteSpace(member) || member.Length > 128) || request.Value?.Length > 4000) { error = "Operation input is invalid."; return false; }
        if (kind == WorkToolGroupOperationKind.Create && request.MemberIds.Count == 0) { error = "New groups require at least one member."; return false; }
        if (kind is WorkToolGroupOperationKind.AddMembers or WorkToolGroupOperationKind.RemoveMembers && request.MemberIds.Count == 0) { error = "Member changes require at least one member."; return false; }
        if (kind is WorkToolGroupOperationKind.Rename or WorkToolGroupOperationKind.UpdateAnnouncement && string.IsNullOrWhiteSpace(request.Value)) { error = "This operation requires a value."; return false; }
        operation = new WorkToolGroupOperationRequest(string.Empty, kind, request.GroupIdentifier.Trim(), request.MemberIds.Select(member => member.Trim()).OrderBy(member => member, StringComparer.Ordinal).ToArray(), request.Value?.Trim());
        sanitized = JsonSerializer.Serialize(new { robotConfigId = request.RobotConfigId, kind = kind.ToString(), groupIdentifier = operation.GroupIdentifier, memberCount = operation.MemberIds.Count, memberIdsHash = Hash(string.Join("\n", operation.MemberIds)), valueLength = operation.Value?.Length ?? 0, valueHash = Hash(operation.Value ?? string.Empty) });
        return true;
    }

    private static WorkToolOperationAuditEntity NewAudit(string operatorName, WorkToolGroupOperationKind operation, string request, string status, string? result) => new() { OperatorName = operatorName, Operation = operation.ToString(), WorkToolCommandNumber = operation == WorkToolGroupOperationKind.Create ? 206 : 207, SanitizedRequestJson = request, Status = status, Result = SafeResult(result) };
    private static string RequireOperator(ClaimsPrincipal user) => user.Identity?.Name ?? "unknown";
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string? SafeResult(string? value) => string.IsNullOrWhiteSpace(value) ? value : value.Length > 512 ? value[..512] : value;
    private static string MaskRobotId(string value) => value.Length <= 4 ? "****" : $"***{value[^4..]}";

    public sealed record UpdateRobotRequest(string Name, string WorkToolRobotId, bool IsEnabled);
    public sealed record RobotResponse(Guid Id, string Name, string RobotReference, bool IsEnabled);
    public sealed record RegisterExistingGroupRequest(Guid RobotConfigId, string ExternalGroupId, string Name, bool ManualInvitationCompleted);
    public sealed record KnownGroupResponse(Guid Id, Guid RobotConfigId, string ExternalGroupId, string Name);
    public sealed record GroupOperationRequest(Guid RobotConfigId, string Kind, string GroupIdentifier, IReadOnlyList<string>? MemberIds, string? Value) { public IReadOnlyList<string> MemberIds { get; init; } = MemberIds ?? []; }
    public sealed record ExecuteOperationRequest(GroupOperationRequest Operation, string ConfirmationToken);
    public sealed record PreviewResponse(string SanitizedRequest, string ConfirmationToken, DateTime ExpiresAtUtc);
    public sealed record CommandStatusResponse(bool Succeeded, string Message);
    public sealed record AuditResponse(Guid Id, string Operation, int WorkToolCommandNumber, string Status, string? Result, DateTime CreatedAtUtc, string SanitizedRequest);
    public sealed record AuditScopeResponse(string Scope);
}
