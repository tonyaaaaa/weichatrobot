using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.WorkTool;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.Infrastructure.WorkTool;
using WechatRobot.Api.Security;
using WechatRobot.Application.Security;

namespace WechatRobot.Api.WorkTool;

public static class WorkToolGroupOperationEndpoints
{
    private static readonly TimeSpan ConfirmationLifetime = TimeSpan.FromMinutes(2);

    public static IEndpointRouteBuilder MapWorkToolGroupOperationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/admin/worktool")
            .RequireAuthorization(SystemRoles.Admin)
            .RequireRateLimiting(RateLimitPolicies.WorkToolCommands);
        group.MapGet("/robots", ListRobotsAsync);
        group.MapPut("/robots/{id:guid}", UpsertRobotAsync);
        group.MapPost("/robots/{id:guid}/test-connection", ProbeRobotAsync);
        group.MapGet("/robots/{id:guid}/probe", ProbeRobotAsync);
        group.MapPost("/robots/{id:guid}/message-callback/configure", ConfigureMessageCallbackAsync);
        group.MapPost("/robots/{id:guid}/command-result-callback/configure", ConfigureCommandResultCallbackAsync);
        group.MapGet("/robots/{id:guid}/callbacks", GetRobotCallbacksAsync);
        group.MapDelete("/robots/{id:guid}/callbacks/{type:int}", DeleteRobotCallbackAsync);
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
        return Results.Ok(robots.Select(robot => new RobotResponse(robot.Id, robot.Name, "configured", robot.IsEnabled)));
    }

    private static async Task<IResult> UpsertRobotAsync(Guid id, UpdateRobotRequest request, WechatRobotDbContext database,
        ISecretProtector protector, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 128 || string.IsNullOrWhiteSpace(request.WorkToolRobotId) || request.WorkToolRobotId.Length > 128)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["robot"] = ["Robot name and WorkTool robot ID are required."] });

        var isMySql = string.Equals(
                database.Database.ProviderName,
                "MySql.EntityFrameworkCore",
                StringComparison.Ordinal);
        await using var sendGate = isMySql
            ? await MySqlRobotSendCoordinator.AcquireAsync(database, id, cancellationToken)
            : null;
        await using var transaction = isMySql
            ? await database.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var robot = await database.RobotConfigs.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        var wasEnabled = robot?.IsEnabled;
        if (robot is null)
        {
            var callbackSecret = GenerateCallbackSecret();
            robot = new RobotConfigEntity
            {
                Id = id,
                WorkToolRobotId = $"migrated-{Guid.NewGuid():N}",
                CallbackRouteCode = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant(),
                CallbackSecretHash = Hash(callbackSecret),
                EncryptedCallbackSecret = protector.Protect(callbackSecret)
            };
            database.RobotConfigs.Add(robot);
        }
        robot.Name = request.Name.Trim();
        robot.EncryptedWorkToolRobotId = protector.Protect(request.WorkToolRobotId.Trim());
        robot.CallbackRouteCode ??= Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        robot.IsEnabled = request.IsEnabled;
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
        return Results.Ok(new RobotResponse(robot.Id, robot.Name, "configured", robot.IsEnabled));
    }

    private static async Task<IResult> ProbeRobotAsync(
        Guid id,
        WechatRobotDbContext database,
        RobotCallbackConfigurationService service,
        CancellationToken cancellationToken)
    {
        var robot = await database.RobotConfigs.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (robot is null) return Results.NotFound();
        try
        {
            var result = await service.ProbeAsync(robot.Id, cancellationToken);
            return Results.Ok(new RobotProbeResponse(
                result.Reachable,
                result.Online,
                result.MessageCallbackEnabled,
                result.ReplyAllEnabled,
                result.FailureCode));
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { return Results.Problem("WorkTool connection test failed.", statusCode: 502); }
    }

    private static async Task<IResult> ConfigureMessageCallbackAsync(
        Guid id,
        ConfigureMessageCallbackRequest request,
        ClaimsPrincipal user,
        RobotCallbackConfigurationService service,
        IHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        if (!TryGetOperator(user, out var actor)) return Results.Forbid();
        if (!TryParsePublicBaseUri(request.PublicBaseUrl, environment, out var baseUri))
            return InvalidCallbackBaseUrl();
        try
        {
            var result = await service.ConfigureMessageCallbackAsync(
                id,
                baseUri!,
                request.ReplyAll,
                actor,
                cancellationToken);
            return result.Succeeded
                ? Results.Ok(new RobotCallbackMutationResponse(true))
                : Results.Problem("WorkTool message callback configuration failed.", statusCode: StatusCodes.Status502BadGateway);
        }
        catch (RobotCallbackConfigurationNotFoundException) { return Results.NotFound(); }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.Problem("WorkTool message callback configuration failed.", statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> ConfigureCommandResultCallbackAsync(
        Guid id,
        ConfigureCommandResultCallbackRequest request,
        ClaimsPrincipal user,
        RobotCallbackConfigurationService service,
        IHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        if (!TryGetOperator(user, out var actor)) return Results.Forbid();
        if (!TryParsePublicBaseUri(request.PublicBaseUrl, environment, out var baseUri))
            return InvalidCallbackBaseUrl();
        try
        {
            var result = await service.ConfigureCommandResultCallbackAsync(
                id,
                baseUri!,
                actor,
                cancellationToken);
            return result.Succeeded
                ? Results.Ok(new RobotCallbackMutationResponse(true))
                : Results.Problem("WorkTool command-result callback configuration failed.", statusCode: StatusCodes.Status502BadGateway);
        }
        catch (RobotCallbackConfigurationNotFoundException) { return Results.NotFound(); }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.Problem("WorkTool command-result callback configuration failed.", statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> GetRobotCallbacksAsync(
        Guid id,
        WechatRobotDbContext database,
        RobotCallbackConfigurationService service,
        CancellationToken cancellationToken)
    {
        if (!await database.RobotConfigs.AsNoTracking().AnyAsync(
                item => item.Id == id && item.IsEnabled,
                cancellationToken))
            return Results.NotFound();
        try
        {
            var status = await service.GetStatusAsync(id, cancellationToken);
            return Results.Ok(new RobotCallbackStatusResponse(
                status.MessageCallbackConfigured,
                status.CommandResultCallbackConfigured,
                status.ReplyAll,
                status.CheckedAtUtc));
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.Problem("WorkTool callback query failed.", statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> DeleteRobotCallbackAsync(
        Guid id,
        int type,
        ClaimsPrincipal user,
        RobotCallbackConfigurationService service,
        CancellationToken cancellationToken)
    {
        if (!TryGetOperator(user, out var actor)) return Results.Forbid();
        if (type != 1)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["type"] = ["Only WorkTool command-result callback type 1 is managed."]
            });
        try
        {
            var result = await service.DeleteEventCallbackAsync(
                id,
                type,
                actor,
                cancellationToken);
            return result.Succeeded
                ? Results.Ok(new RobotCallbackMutationResponse(true))
                : Results.Problem("WorkTool callback deletion failed.", statusCode: StatusCodes.Status502BadGateway);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.Problem("WorkTool callback deletion failed.", statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static bool TryParsePublicBaseUri(
        string value,
        IHostEnvironment environment,
        out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var candidate) ||
            !string.IsNullOrEmpty(candidate.UserInfo) ||
            !string.IsNullOrEmpty(candidate.Query) ||
            !string.IsNullOrEmpty(candidate.Fragment) ||
            candidate.AbsolutePath != "/" ||
            (candidate.Scheme != Uri.UriSchemeHttps &&
             !(candidate.Scheme == Uri.UriSchemeHttp &&
               (environment.IsDevelopment() || environment.IsEnvironment("Testing")))))
            return false;
        uri = candidate;
        return true;
    }

    private static IResult InvalidCallbackBaseUrl() =>
        Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["publicBaseUrl"] = ["Public base URL must be an HTTPS origin without credentials, path, query, or fragment."]
        });

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
        if (!TryGetOperator(user, out var operatorName)) return Results.Forbid();
        var now = DateTime.UtcNow;
        var token = confirmation.Issue(operatorName, sanitized, now, ConfirmationLifetime);
        database.WorkToolOperationConfirmations.Add(new WorkToolOperationConfirmationEntity { TokenHash = Hash(token), OperatorName = operatorName, PayloadHash = Hash(sanitized), ExpiresAtUtc = now.Add(ConfirmationLifetime) });
        database.WorkToolOperationAudits.Add(NewAudit(operatorName, operation.Kind, sanitized, "Previewed", "Confirmation required."));
        await database.SaveChangesAsync(cancellationToken);
        return Results.Ok(new PreviewResponse(sanitized, token, now.Add(ConfirmationLifetime)));
    }

    private static async Task<IResult> ExecuteAsync(ExecuteOperationRequest request, ClaimsPrincipal user, WechatRobotDbContext database,
        GroupOperationConfirmationService confirmation, ISecretProtector protector, CancellationToken cancellationToken)
    {
        if (!TryBuild(request.Operation, out var operation, out var sanitized, out var error)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["operation"] = [error!] });
        if (!TryGetOperator(user, out var operatorName)) return Results.Forbid();
        var now = DateTime.UtcNow;
        var confirmationRow = await database.WorkToolOperationConfirmations.SingleOrDefaultAsync(item => item.TokenHash == Hash(request.ConfirmationToken), cancellationToken);
        var valid = confirmationRow is not null && confirmationRow.ConsumedAtUtc is null && confirmationRow.ExpiresAtUtc >= now && confirmationRow.OperatorName == operatorName && confirmationRow.PayloadHash == Hash(sanitized) && confirmation.IsValid(request.ConfirmationToken, operatorName, sanitized, now);
        if (!valid) { database.WorkToolOperationAudits.Add(NewAudit(operatorName, operation.Kind, sanitized, "Rejected", "Invalid, changed, expired, or already-used confirmation.")); await database.SaveChangesAsync(cancellationToken); return Results.BadRequest(new { error = "Confirmation token is invalid, expired, already used, or does not match this request." }); }
        var robot = await database.RobotConfigs.AsNoTracking().SingleOrDefaultAsync(item => item.Id == request.Operation.RobotConfigId && item.IsEnabled, cancellationToken);
        if (robot is null) { database.WorkToolOperationAudits.Add(NewAudit(operatorName, operation.Kind, sanitized, "Rejected", "Enabled robot was not found.")); await database.SaveChangesAsync(cancellationToken); return Results.ValidationProblem(new Dictionary<string, string[]> { ["robotConfigId"] = ["Enabled robot was not found."] }); }
        confirmationRow!.ConsumedAtUtc = now; confirmationRow.Version++;
        var audit = NewAudit(operatorName, operation.Kind, sanitized, "Queued", null);
        audit.RobotConfigId = robot.Id;
        audit.EncryptedCommandJson = protector.Protect(JsonSerializer.Serialize(operation with { RobotConfigId = robot.Id }));
        database.WorkToolOperationAudits.Add(audit);
        try { await database.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { database.Entry(confirmationRow).State = EntityState.Detached; database.Entry(audit).State = EntityState.Detached; database.WorkToolOperationAudits.Add(NewAudit(operatorName, operation.Kind, sanitized, "Rejected", "Confirmation was already used.")); await database.SaveChangesAsync(cancellationToken); return Results.BadRequest(new { error = "Confirmation token was already used." }); }
        return Results.Accepted($"/api/admin/worktool/group-operations/{audit.Id:D}",
            new CommandStatusResponse(true, "Command queued.", audit.Id));
    }

    private static bool TryBuild(GroupOperationRequest request, out WorkToolGroupOperationRequest operation, out string sanitized, out string? error)
    {
        operation = default!; sanitized = string.Empty; error = null;
        if (!Enum.TryParse<WorkToolGroupOperationKind>(request.Kind, true, out var kind) || string.IsNullOrWhiteSpace(request.GroupIdentifier) || request.GroupIdentifier.Length > 256 || request.MemberIds.Count > 100 || request.MemberIds.Any(member => string.IsNullOrWhiteSpace(member) || member.Length > 128) || request.Value?.Length > 4000) { error = "Operation input is invalid."; return false; }
        if (kind == WorkToolGroupOperationKind.Create && request.MemberIds.Count == 0) { error = "New groups require at least one member."; return false; }
        if (kind is WorkToolGroupOperationKind.AddMembers or WorkToolGroupOperationKind.RemoveMembers && request.MemberIds.Count == 0) { error = "Member changes require at least one member."; return false; }
        if (kind is WorkToolGroupOperationKind.Rename or WorkToolGroupOperationKind.UpdateAnnouncement && string.IsNullOrWhiteSpace(request.Value)) { error = "This operation requires a value."; return false; }
        operation = new WorkToolGroupOperationRequest(request.RobotConfigId, kind, request.GroupIdentifier.Trim(), request.MemberIds.Select(member => member.Trim()).OrderBy(member => member, StringComparer.Ordinal).ToArray(), request.Value?.Trim());
        sanitized = JsonSerializer.Serialize(new { robotConfigId = request.RobotConfigId, kind = kind.ToString(), groupIdentifier = operation.GroupIdentifier, memberCount = operation.MemberIds.Count, memberIdsHash = Hash(string.Join("\n", operation.MemberIds)), valueLength = operation.Value?.Length ?? 0, valueHash = Hash(operation.Value ?? string.Empty) });
        return true;
    }

    private static WorkToolOperationAuditEntity NewAudit(string operatorName, WorkToolGroupOperationKind operation, string request, string status, string? result) => new() { OperatorName = operatorName, Operation = operation.ToString(), WorkToolCommandNumber = operation == WorkToolGroupOperationKind.Create ? 206 : 207, SanitizedRequestJson = request, Status = status, Result = SafeResult(result) };
    private static bool TryGetOperator(ClaimsPrincipal user, out string operatorName)
    {
        operatorName = new[]
            {
                user.Identity?.Name,
                user.FindFirstValue(ClaimTypes.NameIdentifier),
                user.FindFirstValue("sub")
            }
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim()
            ?? string.Empty;
        return operatorName.Length > 0;
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string GenerateCallbackSecret() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    private static string? SafeResult(string? value) => string.IsNullOrWhiteSpace(value) ? value : value.Length > 512 ? value[..512] : value;
    public sealed record UpdateRobotRequest(string Name, string WorkToolRobotId, bool IsEnabled);
    public sealed record ConfigureMessageCallbackRequest(string PublicBaseUrl, bool ReplyAll);
    public sealed record ConfigureCommandResultCallbackRequest(string PublicBaseUrl);
    public sealed record RobotCallbackMutationResponse(bool Succeeded);
    public sealed record RobotCallbackStatusResponse(
        bool MessageCallbackConfigured,
        bool CommandResultCallbackConfigured,
        bool ReplyAll,
        DateTime CheckedAtUtc);
    public sealed record RobotProbeResponse(
        bool Reachable,
        bool? Online,
        bool MessageCallbackEnabled,
        bool ReplyAllEnabled,
        string? FailureCode);
    public sealed record RobotResponse(Guid Id, string Name, string RobotReference, bool IsEnabled);
    public sealed record RegisterExistingGroupRequest(Guid RobotConfigId, string ExternalGroupId, string Name, bool ManualInvitationCompleted);
    public sealed record KnownGroupResponse(Guid Id, Guid RobotConfigId, string ExternalGroupId, string Name);
    public sealed record GroupOperationRequest(Guid RobotConfigId, string Kind, string GroupIdentifier, IReadOnlyList<string>? MemberIds, string? Value) { public IReadOnlyList<string> MemberIds { get; init; } = MemberIds ?? []; }
    public sealed record ExecuteOperationRequest(GroupOperationRequest Operation, string ConfirmationToken);
    public sealed record PreviewResponse(string SanitizedRequest, string ConfirmationToken, DateTime ExpiresAtUtc);
    public sealed record CommandStatusResponse(bool Succeeded, string Message, Guid? AuditId = null);
    public sealed record AuditResponse(Guid Id, string Operation, int WorkToolCommandNumber, string Status, string? Result, DateTime CreatedAtUtc, string SanitizedRequest);
    public sealed record AuditScopeResponse(string Scope);
}
