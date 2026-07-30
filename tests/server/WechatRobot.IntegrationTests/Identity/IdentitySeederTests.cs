using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.Infrastructure.Persistence;

namespace WechatRobot.IntegrationTests.Identity;

public sealed class IdentitySeederTests
{
    [Fact]
    public async Task Existing_configured_bootstrap_user_without_admin_role_is_repaired()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<WechatRobotDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddIdentityCore<ApplicationUser>()
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<WechatRobotDbContext>();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var existingUser = new ApplicationUser
        {
            UserName = "bootstrap@example.test",
            Email = "bootstrap@example.test",
            DisplayName = "Existing bootstrap user"
        };
        var createResult = await userManager.CreateAsync(existingUser, "BootstrapPassword123!");
        Assert.True(createResult.Succeeded);
        Assert.False(await userManager.IsInRoleAsync(existingUser, SystemRoles.Admin));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BootstrapAdmin:Email"] = existingUser.Email,
                ["BootstrapAdmin:Password"] = "BootstrapPassword123!",
                ["BootstrapAdmin:DisplayName"] = existingUser.DisplayName
            })
            .Build();

        await IdentitySeeder.SeedAsync(scope.ServiceProvider, configuration, TestContext.Current.CancellationToken);

        Assert.True(await userManager.IsInRoleAsync(existingUser, SystemRoles.Admin));
    }

    [Fact]
    public async Task Existing_bootstrap_user_with_replacement_characters_gets_the_configured_display_name()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<WechatRobotDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddIdentityCore<ApplicationUser>()
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<WechatRobotDbContext>();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var existingUser = new ApplicationUser
        {
            UserName = "bootstrap@example.test",
            Email = "bootstrap@example.test",
            DisplayName = "ϵͳ����Ա"
        };
        var createResult = await userManager.CreateAsync(existingUser, "BootstrapPassword123!");
        Assert.True(createResult.Succeeded);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BootstrapAdmin:Email"] = existingUser.Email,
                ["BootstrapAdmin:Password"] = "BootstrapPassword123!",
                ["BootstrapAdmin:DisplayName"] = "系统管理员"
            })
            .Build();

        await IdentitySeeder.SeedAsync(scope.ServiceProvider, configuration, TestContext.Current.CancellationToken);

        var repaired = await userManager.FindByEmailAsync(existingUser.Email);
        Assert.Equal("系统管理员", repaired!.DisplayName);
    }
}
