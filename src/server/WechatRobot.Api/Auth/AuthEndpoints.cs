using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.Api.Security;

namespace WechatRobot.Api.Auth;

public static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/auth");
        group.MapPost("login", LoginAsync).AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Login);
        group.MapGet("me", GetCurrentUserAsync).RequireAuthorization();
        group.MapGet("probe/knowledge", () => Results.Ok()).RequireAuthorization(SystemRoles.KnowledgeOperator);
        return group;
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        IOptions<JwtOptions> jwtOptions)
    {
        var user = await userManager.FindByEmailAsync(request.Email);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (!user.IsEnabled)
        {
            return Results.Unauthorized();
        }

        var passwordResult = await signInManager.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
        if (!passwordResult.Succeeded)
        {
            return Results.Unauthorized();
        }

        var roles = await userManager.GetRolesAsync(user);
        var options = jwtOptions.Value;
        var roleList = roles.ToArray();
        var securityStamp = await userManager.GetSecurityStampAsync(user);
        var token = CreateToken(user, roleList, securityStamp, options);
        return Results.Ok(new LoginResponse(token, "Bearer", options.ExpirationMinutes * 60, new CurrentUserResponse(user.Id, user.Email!, user.DisplayName, roleList)));
    }

    private static async Task<IResult> GetCurrentUserAsync(ClaimsPrincipal principal, UserManager<ApplicationUser> userManager)
    {
        var userId = userManager.GetUserId(principal);
        if (!Guid.TryParse(userId, out var id))
        {
            return Results.Unauthorized();
        }

        var user = await userManager.FindByIdAsync(id.ToString());
        if (user is null || !user.IsEnabled)
        {
            return Results.Unauthorized();
        }

        var roles = await userManager.GetRolesAsync(user);
        return Results.Ok(new CurrentUserResponse(user.Id, user.Email!, user.DisplayName, roles.ToArray()));
    }

    private static string CreateToken(
        ApplicationUser user,
        IReadOnlyCollection<string> roles,
        string securityStamp,
        JwtOptions options)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.DisplayName),
            new("security_stamp_hash", HashSecurityStamp(securityStamp))
        };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            options.Issuer,
            options.Audience,
            claims,
            expires: DateTime.UtcNow.AddMinutes(options.ExpirationMinutes),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    internal static string HashSecurityStamp(string securityStamp) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(securityStamp)));

    public sealed record LoginRequest(string Email, string Password);
    public sealed record LoginResponse(string AccessToken, string TokenType, int ExpiresInSeconds, CurrentUserResponse User);
    public sealed record CurrentUserResponse(Guid Id, string Email, string DisplayName, IReadOnlyCollection<string> Roles);
}
