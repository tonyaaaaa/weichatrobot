using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace WechatRobot.Infrastructure.Identity;

public static class IdentitySeeder
{
    public static async Task SeedAsync(IServiceProvider services, IConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var roleManager = services.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();

        await SeedRolesAsync(roleManager, cancellationToken);

        var email = configuration["BootstrapAdmin:Email"];
        var password = configuration["BootstrapAdmin:Password"];
        var displayName = configuration["BootstrapAdmin:DisplayName"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(displayName))
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var user = await userManager.FindByEmailAsync(email).WaitAsync(cancellationToken);
        if (user is null)
        {
            user = new ApplicationUser
            {
                UserName = email,
                Email = email,
                DisplayName = displayName,
                EmailConfirmed = true
            };
            cancellationToken.ThrowIfCancellationRequested();
            var result = await userManager.CreateAsync(user, password).WaitAsync(cancellationToken);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException($"Bootstrap admin could not be created: {string.Join("; ", result.Errors.Select(error => error.Code))}");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (await userManager.IsInRoleAsync(user, SystemRoles.Admin).WaitAsync(cancellationToken))
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var roleResult = await userManager.AddToRoleAsync(user, SystemRoles.Admin).WaitAsync(cancellationToken);
        if (!roleResult.Succeeded)
        {
            throw new InvalidOperationException($"Bootstrap admin role could not be assigned: {string.Join("; ", roleResult.Errors.Select(error => error.Code))}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!await userManager.IsInRoleAsync(user, SystemRoles.Admin).WaitAsync(cancellationToken))
        {
            throw new InvalidOperationException("Bootstrap admin role membership could not be verified.");
        }
    }

    public static async Task SeedRolesAsync(RoleManager<IdentityRole<Guid>> roleManager, CancellationToken cancellationToken = default)
    {
        foreach (var roleName in SystemRoles.All)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await roleManager.RoleExistsAsync(roleName).WaitAsync(cancellationToken))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var result = await roleManager.CreateAsync(new IdentityRole<Guid>(roleName)).WaitAsync(cancellationToken);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException($"Role '{roleName}' could not be seeded: {string.Join("; ", result.Errors.Select(error => error.Code))}");
            }
        }
    }
}
