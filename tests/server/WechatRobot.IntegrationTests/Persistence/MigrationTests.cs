using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.IntegrationTests.Infrastructure;

namespace WechatRobot.IntegrationTests.Persistence;

public sealed class MigrationTests : IClassFixture<MySqlFixture>
{
    private readonly MySqlFixture _fixture;

    public MigrationTests(MySqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Apply_migrations_and_seed_roles_idempotently()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<WechatRobotDbContext>(options => options.UseMySQL(_fixture.ConnectionString));
        services.AddIdentityCore<ApplicationUser>()
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<WechatRobotDbContext>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();

        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        await IdentitySeeder.SeedRolesAsync(roleManager, TestContext.Current.CancellationToken);
        await IdentitySeeder.SeedRolesAsync(roleManager, TestContext.Current.CancellationToken);

        var roles = await context.Roles.OrderBy(role => role.Name).Select(role => role.Name).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new[] { SystemRoles.Admin, SystemRoles.HumanAgent, SystemRoles.KnowledgeOperator }, roles);
    }
}
