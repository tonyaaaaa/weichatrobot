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
        group.MapGet("/robots/{id:guid}/groups", DiscoverGroupsAsync);
        group.MapPost("/robots/{id:guid}/groups/import", ImportGroupsAsync);
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

    private static async Task<IResult> DiscoverGroupsAsync(
        Guid id,
        string? query,
        int? page,
        int? pageSize,
        WorkToolGroupImportService service,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(await service.DiscoverAsync(
                id,
                query,
                page ?? 1,
                pageSize ?? 50,
                cancellationToken));
        }
        catch (ArgumentOutOfRangeException)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["pagination"] = ["Page must be positive and pageSize must be between 1 and 100."]
            });
        }
        catch (WorkToolGroupListException exception)
        {
            return Results.Json(
                new { error = exception.FailureCode },
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> ImportGroupsAsync(
        Guid id,
        ImportGroupsRequest request,
        ClaimsPrincipal user,
        WorkToolGroupImportService service,
        CancellationToken cancellationToken)
    {
        if (!TryGetOperator(user, out var actor))
            return Results.Forbid();
        try
        {
            return Results.Ok(await service.ImportAsync(
                id,
                request.Groups,
                actor,
                cancellationToken));
        }
        catch (ArgumentException)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["groups"] = ["One to 100 unique available group selections are required."]
            });
        }
        catch (WorkToolGroupListException exception)
        {
            return Results.Json(
                new { error = exception.FailureCode },
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> ListRobotsAsync(WechatRobotDbContext database, CancellationToken cancellationToken)
    {
        var robots = await database.RobotConfigs.AsNoTracking().OrderBy(robot => robot.Name).ToArrayAsync(cancellationToken);
        return Results.Ok(robots.Select(ToRobotResponse));
    }

    private static async Task<IResult> UpsertRobotAsync(Guid id, UpdateRobotRequest request, WechatRobotDbContext database,
        ISecretProtector protector, GroupOperationConfirmationService confirmation, ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        if (!TryGetOperator(user, out var actor)) return Results.Forbid();
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 128 ||
            request.WorkToolRobotId?.Length > 128 || request.SendRateLimitPerMinute is < 1 or > 60)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["robot"] = ["Robot settings are invalid."] });

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
        var credentialChanged = !string.IsNullOrWhiteSpace(request.WorkToolRobotId);
        if (robot is null && !credentialChanged)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["workToolRobotId"] = ["WorkTool robot ID is required for a new robot."] });
        if (robot is null && request.IsEnabled)
            return Results.Conflict(new { error = "robot-probe-required", message = "Create the robot disabled, test the connection, then enable it." });
        if (robot?.IsEnabled == true && credentialChanged)
            return Results.Conflict(new { error = "robot-disable-before-credential-rotation", message = "Disable the robot before replacing its WorkTool robot ID." });
        if (robot?.IsEnabled == false && request.IsEnabled &&
            (string.IsNullOrWhiteSpace(request.EnableConfirmationToken) ||
             !confirmation.IsValid(request.EnableConfirmationToken, actor, EnablePayload(robot), DateTime.UtcNow)))
            return Results.Conflict(new { error = "robot-probe-required", message = "A current successful connection test is required before enabling." });
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
        if (credentialChanged)
            robot.EncryptedWorkToolRobotId = protector.Protect(request.WorkToolRobotId!.Trim());
        robot.CallbackRouteCode ??= Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        robot.IsEnabled = request.IsEnabled;
        robot.SendRateLimitPerMinute = request.SendRateLimitPerMinute;
        robot.SendRateTokens = Math.Min(robot.SendRateTokens, request.SendRateLimitPerMinute);
        robot.UpdatedAtUtc = DateTime.UtcNow;
        database.AdministrationAudits.Add(new AdministrationAuditEntity
        {
            Actor = actor,
            Action = wasEnabled.HasValue ? "robot.update" : "robot.create",
            TargetType = "RobotConfig",
            TargetId = robot.Id.ToString("D"),
            SanitizedDetailJson = JsonSerializer.Serialize(new
            {
                credential = credentialChanged ? (wasEnabled.HasValue ? "rotated" : "configured") : "unchanged",
                robot.IsEnabled,
                robot.SendRateLimitPerMinute
            })
        });
        await database.SaveChangesAsync(cancellationToken);
        if (wasEnabled == true && !request.IsEnabled)
        {
            var commands = database.SendCommands.Where(command => command.RobotConfigId == id &&
                (command.Status == "pending" || command.Status == "retrying" || command.Status == "leased"));
            if (isMySql)
                await commands.ExecuteUpdateAsync(setters => setters.SetProperty(command => command.Status, "blocked")
                    .SetProperty(command => command.LeaseOwner, (string?)null).SetProperty(command => command.LeaseExpiresAtUtc, (DateTime?)null)
                    .SetProperty(command => command.Version, command => command.Version + 1), cancellationToken);
            else
                foreach (var command in await commands.ToArrayAsync(cancellationToken))
                {
                    command.Status = "blocked";
                    command.LeaseOwner = null;
                    command.LeaseExpiresAtUtc = null;
                    command.Version++;
                }
            robot.SendLeaseOwner = null; robot.SendLeaseExpiresAtUtc = null; robot.SendCoordinationVersion++;
            await database.SaveChangesAsync(cancellationToken);
        }
        else if (wasEnabled == false && request.IsEnabled)
        {
            var now = DateTime.UtcNow;
            var commands = database.SendCommands.Where(command => command.RobotConfigId == id && command.Status == "blocked");
            if (isMySql)
                await commands.ExecuteUpdateAsync(setters => setters.SetProperty(command => command.Status, "pending")
                    .SetProperty(command => command.NextAttemptAtUtc, now).SetProperty(command => command.LeaseOwner, (string?)null)
                    .SetProperty(command => command.LeaseExpiresAtUtc, (DateTime?)null)
                    .SetProperty(command => command.Version, command => command.Version + 1), cancellationToken);
            else
                foreach (var command in await commands.ToArrayAsync(cancellationToken))
                {
                    command.Status = "pending";
                    command.NextAttemptAtUtc = now;
                    command.LeaseOwner = null;
                    command.LeaseExpiresAtUtc = null;
                    command.Version++;
                }
            await database.SaveChangesAsync(cancellationToken);
        }
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return Results.Ok(ToRobotResponse(robot));
    }

    private static async Task<IResult> ProbeRobotAsync(
        Guid id,
        WechatRobotDbContext database,
        RobotCallbackConfigurationService service,
        GroupOperationConfirmationService confirmation,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        if (!TryGetOperator(user, out var actor)) return Results.Forbid();
        var robot = await database.RobotConfigs.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (robot is null) return Results.NotFound();
        if (string.IsNullOrWhiteSpace(robot.EncryptedWorkToolRobotId))
            return MissingWorkToolCredential();
        try
        {
            var result = await service.ProbeAsync(robot.Id, cancellationToken);
            var expiresAtUtc = DateTime.UtcNow.Add(ConfirmationLifetime);
            var enableToken = result.Reachable
                ? confirmation.Issue(actor, EnablePayload(robot), DateTime.UtcNow, ConfirmationLifetime)
                : null;
            return Results.Ok(new RobotProbeResponse(
                result.Reachable,
                result.Online,
                result.MessageCallbackEnabled,
                result.ReplyAllEnabled,
                result.FailureCode,
                enableToken,
                enableToken is null ? null : expiresAtUtc));
        }
        catch (WorkToolCredentialUnavailableException) { return MissingWorkToolCredential(); }
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { return Results.Problem("WorkTool connection test failed.", statusCode: 502); }
    }

    private static IResult MissingWorkToolCredential() =>
        Results.Conflict(new
        {
            error = "worktool-credential-required",
            message = "Save a WorkTool robot ID before testing the connection."
        });

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
        Results.Ok(await (
            from profile in database.GroupProfiles.AsNoTracking()
            join robot in database.RobotConfigs.AsNoTracking()
                on profile.RobotConfigId equals robot.Id
            orderby profile.Name, profile.Id
            select new KnownGroupResponse(
                profile.Id,
                profile.RobotConfigId,
                robot.Name,
                profile.Name,
                profile.WorkToolGroupRemark,
                profile.IsEnabled,
                profile.UpdatedAtUtc))
            .ToArrayAsync(cancellationToken));

    private static async Task<IResult> RegisterExistingGroupAsync(RegisterExistingGroupRequest request, WechatRobotDbContext database, CancellationToken cancellationToken)
    {
        var name = request.Name?.Trim();
        var remark = string.IsNullOrWhiteSpace(request.WorkToolGroupRemark)
            ? null
            : request.WorkToolGroupRemark.Trim();
        if (!request.ManualInvitationCompleted || string.IsNullOrWhiteSpace(name) ||
            name.Length > 256 || remark?.Length > 256)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["group"] = ["A human must first invite the robot in Enterprise WeChat before registering an existing group."] });
        var robot = await database.RobotConfigs.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == request.RobotConfigId && item.IsEnabled, cancellationToken);
        if (robot is null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["robotConfigId"] = ["Enabled robot was not found."] });
        var matches = await database.GroupProfiles
            .Where(group => group.RobotConfigId == request.RobotConfigId &&
                            group.Name == name &&
                            group.WorkToolGroupRemark == remark)
            .Take(2)
            .ToArrayAsync(cancellationToken);
        if (matches.Length > 1)
            return Results.Conflict(new { error = "More than one registered group has the same WorkTool name and remark." });
        var existing = matches.SingleOrDefault();
        if (existing is null)
        {
            existing = new GroupProfileEntity
            {
                RobotConfigId = request.RobotConfigId,
                Name = name,
                WorkToolGroupRemark = remark
            };
            database.GroupProfiles.Add(existing);
            await database.SaveChangesAsync(cancellationToken);
        }
        return Results.Ok(new KnownGroupResponse(
            existing.Id,
            existing.RobotConfigId,
            robot.Name,
            existing.Name,
            existing.WorkToolGroupRemark,
            existing.IsEnabled,
            existing.UpdatedAtUtc));
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
        var audit = NewAudit(operatorName, operation.Kind, sanitized, WorkToolCommandStatuses.Queued, null);
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
        if (!Enum.TryParse<WorkToolGroupOperationKind>(request.Kind, true, out var kind) || string.IsNullOrWhiteSpace(request.GroupIdentifier) || request.GroupIdentifier.Length > 256 || request.MemberDisplayNames.Count > 100 || request.MemberDisplayNames.Any(member => string.IsNullOrWhiteSpace(member) || member.Length > 128) || request.Value?.Length > 4000) { error = "Operation input is invalid."; return false; }
        if (kind == WorkToolGroupOperationKind.Create && request.MemberDisplayNames.Count == 0) { error = "New groups require at least one member."; return false; }
        if (kind is WorkToolGroupOperationKind.AddMembers or WorkToolGroupOperationKind.RemoveMembers && request.MemberDisplayNames.Count == 0) { error = "Member changes require at least one member."; return false; }
        if (kind is WorkToolGroupOperationKind.Rename or WorkToolGroupOperationKind.UpdateAnnouncement && string.IsNullOrWhiteSpace(request.Value)) { error = "This operation requires a value."; return false; }
        operation = new WorkToolGroupOperationRequest(request.RobotConfigId, kind, request.GroupIdentifier.Trim(), request.MemberDisplayNames.Select(member => member.Trim()).OrderBy(member => member, StringComparer.Ordinal).ToArray(), request.Value?.Trim());
        sanitized = JsonSerializer.Serialize(new { robotConfigId = request.RobotConfigId, kind = kind.ToString(), groupIdentifier = operation.GroupIdentifier, memberCount = operation.MemberDisplayNames.Count, memberDisplayNamesHash = Hash(string.Join("\n", operation.MemberDisplayNames)), valueLength = operation.Value?.Length ?? 0, valueHash = Hash(operation.Value ?? string.Empty) });
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
    private static string EnablePayload(RobotConfigEntity robot) =>
        JsonSerializer.Serialize(new { robotId = robot.Id, updatedAtUtc = robot.UpdatedAtUtc });
    private static RobotResponse ToRobotResponse(RobotConfigEntity robot) => new(
        robot.Id,
        robot.Name,
        robot.EncryptedWorkToolRobotId is null ? "missing" : "configured",
        robot.EncryptedWorkToolRobotId is not null,
        robot.IsEnabled,
        robot.SendRateLimitPerMinute,
        robot.UpdatedAtUtc);
    public sealed record UpdateRobotRequest(
        string Name,
        string? WorkToolRobotId,
        bool IsEnabled,
        int SendRateLimitPerMinute = 50,
        string? EnableConfirmationToken = null);
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
        string? FailureCode,
        string? EnableConfirmationToken,
        DateTime? EnableConfirmationExpiresAtUtc);
    public sealed record RobotResponse(
        Guid Id,
        string Name,
        string RobotReference,
        bool HasWorkToolRobotId,
        bool IsEnabled,
        int SendRateLimitPerMinute,
        DateTime UpdatedAtUtc);
    public sealed record RegisterExistingGroupRequest(Guid RobotConfigId, string Name, string? WorkToolGroupRemark, bool ManualInvitationCompleted);
    public sealed record KnownGroupResponse(
        Guid Id,
        Guid RobotConfigId,
        string RobotName,
        string Name,
        string? WorkToolGroupRemark,
        bool IsEnabled,
        DateTime UpdatedAtUtc);
    public sealed record GroupOperationRequest(Guid RobotConfigId, string Kind, string GroupIdentifier, IReadOnlyList<string>? MemberDisplayNames, string? Value)
    {
        public IReadOnlyList<string> MemberDisplayNames { get; init; } = MemberDisplayNames ?? [];
    }
    public sealed record ExecuteOperationRequest(GroupOperationRequest Operation, string ConfirmationToken);
    public sealed record PreviewResponse(string SanitizedRequest, string ConfirmationToken, DateTime ExpiresAtUtc);
    public sealed record CommandStatusResponse(bool Succeeded, string Message, Guid? AuditId = null);
    public sealed record AuditResponse(Guid Id, string Operation, int WorkToolCommandNumber, string Status, string? Result, DateTime CreatedAtUtc, string SanitizedRequest);
    public sealed record AuditScopeResponse(string Scope);
    public sealed record ImportGroupsRequest(
        IReadOnlyList<GroupImportSelection> Groups);
}
