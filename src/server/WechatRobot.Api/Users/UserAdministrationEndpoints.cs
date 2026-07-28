using System.Security.Claims;
using WechatRobot.Api.Security;
using WechatRobot.Infrastructure.Identity;

namespace WechatRobot.Api.Users;

public static class UserAdministrationEndpoints
{
    public static RouteGroupBuilder MapUserAdministrationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/admin/users")
            .RequireAuthorization(SystemRoles.Admin)
            .RequireRateLimiting(RateLimitPolicies.Ordinary);
        group.MapGet("", ListAsync);
        group.MapGet("/roles", () => TypedResults.Ok(SystemRoles.Assignable));
        group.MapPost("", CreateAsync);
        group.MapPut("/{id:guid}/enabled", SetEnabledAsync);
        group.MapPut("/{id:guid}/roles", SetRolesAsync);
        return group;
    }

    private static async Task<IResult> ListAsync(
        string? q,
        string? state,
        int page,
        int pageSize,
        UserAdministrationService service,
        CancellationToken cancellationToken)
    {
        if (!Pagination.TryNormalize(page, pageSize, out page, out pageSize, out _))
            return TypedResults.BadRequest(new { error = "Page must not exceed 1000000." });
        bool? enabled = state?.Trim().ToLowerInvariant() switch
        {
            null or "" or "all" => null,
            "enabled" => true,
            "disabled" => false,
            _ => null
        };
        if (!string.IsNullOrWhiteSpace(state) &&
            !new[] { "all", "enabled", "disabled" }.Contains(state.Trim(), StringComparer.OrdinalIgnoreCase))
            return TypedResults.BadRequest(new { error = "state must be all, enabled, or disabled." });
        return TypedResults.Ok(await service.ListAsync(q, enabled, page, pageSize, cancellationToken));
    }

    private static async Task<IResult> CreateAsync(
        CreateManagedUser request,
        ClaimsPrincipal principal,
        UserAdministrationService service,
        CancellationToken cancellationToken)
    {
        try
        {
            var user = await service.CreateAsync(Actor(principal), request, cancellationToken);
            return TypedResults.Created($"/api/admin/users/{user.Id:D}", user);
        }
        catch (UserAdministrationException exception)
        {
            return Failure(exception);
        }
    }

    private static async Task<IResult> SetEnabledAsync(
        Guid id,
        SetManagedUserEnabled request,
        ClaimsPrincipal principal,
        UserAdministrationService service,
        CancellationToken cancellationToken)
    {
        try
        {
            return TypedResults.Ok(await service.SetEnabledAsync(
                Actor(principal), id, request.IsEnabled, cancellationToken));
        }
        catch (UserAdministrationException exception)
        {
            return Failure(exception);
        }
    }

    private static async Task<IResult> SetRolesAsync(
        Guid id,
        SetManagedUserRoles request,
        ClaimsPrincipal principal,
        UserAdministrationService service,
        CancellationToken cancellationToken)
    {
        try
        {
            return TypedResults.Ok(await service.SetRolesAsync(
                Actor(principal), id, request, cancellationToken));
        }
        catch (UserAdministrationException exception)
        {
            return Failure(exception);
        }
    }

    private static IResult Failure(UserAdministrationException exception) => exception.Code switch
    {
        "user-not-found" => TypedResults.NotFound(new { error = exception.Code }),
        "last-enabled-admin" => TypedResults.Conflict(new { error = exception.Code }),
        "worktool-display-name-conflict" => TypedResults.Conflict(new { error = exception.Code }),
        _ => TypedResults.BadRequest(new { error = exception.Code, errors = exception.Errors })
    };

    private static string Actor(ClaimsPrincipal principal) =>
        principal.Identity?.Name ??
        principal.FindFirstValue(ClaimTypes.NameIdentifier) ??
        "unknown";

    public sealed record SetManagedUserEnabled(bool IsEnabled);
}
