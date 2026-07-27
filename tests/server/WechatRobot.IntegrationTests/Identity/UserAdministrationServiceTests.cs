using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.Infrastructure.Persistence;

namespace WechatRobot.IntegrationTests.Identity;

public sealed class UserAdministrationServiceTests
{
    [Fact]
    public async Task Creates_lists_and_mutates_users_through_identity_without_auditing_passwords()
    {
        await using var fixture = await UserAdministrationFixture.CreateAsync();
        var service = fixture.Services.GetRequiredService<UserAdministrationService>();

        var created = await service.CreateAsync(
            "admin@example.test",
            new CreateManagedUser("agent@example.test", "客服一号", "Temporary1!Password", [SystemRoles.HumanAgent]),
            TestContext.Current.CancellationToken);

        Assert.True(created.IsEnabled);
        Assert.Equal([SystemRoles.HumanAgent], created.Roles);

        var page = await service.ListAsync("agent", null, 1, 20, TestContext.Current.CancellationToken);
        Assert.Equal(1, page.Total);
        Assert.Equal(created.Id, page.Items.Single().Id);

        var disabled = await service.SetEnabledAsync(
            "admin@example.test", created.Id, false, TestContext.Current.CancellationToken);
        Assert.False(disabled.IsEnabled);

        var roles = await service.SetRolesAsync(
            "admin@example.test",
            created.Id,
            new SetManagedUserRoles([SystemRoles.KnowledgeOperator, SystemRoles.HumanAgent]),
            TestContext.Current.CancellationToken);
        Assert.Equal([SystemRoles.HumanAgent, SystemRoles.KnowledgeOperator], roles.Roles.Order().ToArray());

        var auditJson = string.Join('\n', await fixture.Database.AdministrationAudits
            .Select(item => item.SanitizedDetailJson)
            .ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain("Temporary1!Password", auditJson, StringComparison.Ordinal);
        Assert.DoesNotContain("password", auditJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rejects_unknown_roles_and_identity_password_policy_failures()
    {
        await using var fixture = await UserAdministrationFixture.CreateAsync();
        var service = fixture.Services.GetRequiredService<UserAdministrationService>();

        var roleError = await Assert.ThrowsAsync<UserAdministrationException>(() =>
            service.CreateAsync("admin@example.test",
                new CreateManagedUser("bad-role@example.test", "Bad Role", "Temporary1!Password", ["InventedRole"]),
                TestContext.Current.CancellationToken));
        Assert.Equal("unknown-role", roleError.Code);

        var passwordError = await Assert.ThrowsAsync<UserAdministrationException>(() =>
            service.CreateAsync("admin@example.test",
                new CreateManagedUser("weak@example.test", "Weak", "short", [SystemRoles.HumanAgent]),
                TestContext.Current.CancellationToken));
        Assert.Equal("identity-validation", passwordError.Code);
        Assert.DoesNotContain("short", string.Join(' ', passwordError.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Protects_the_last_enabled_administrator_from_disable_and_role_removal()
    {
        await using var fixture = await UserAdministrationFixture.CreateAsync();
        var service = fixture.Services.GetRequiredService<UserAdministrationService>();
        var admin = await fixture.CreateUserAsync(
            "only-admin@example.test", "Only Admin", "Temporary1!Password", [SystemRoles.Admin]);

        var disableError = await Assert.ThrowsAsync<UserAdministrationException>(() =>
            service.SetEnabledAsync("only-admin@example.test", admin.Id, false, TestContext.Current.CancellationToken));
        Assert.Equal("last-enabled-admin", disableError.Code);

        var roleError = await Assert.ThrowsAsync<UserAdministrationException>(() =>
            service.SetRolesAsync("only-admin@example.test", admin.Id,
                new SetManagedUserRoles([SystemRoles.KnowledgeOperator]), TestContext.Current.CancellationToken));
        Assert.Equal("last-enabled-admin", roleError.Code);
    }

    [Fact]
    public async Task Allows_disabling_an_administrator_when_another_enabled_administrator_remains()
    {
        await using var fixture = await UserAdministrationFixture.CreateAsync();
        var service = fixture.Services.GetRequiredService<UserAdministrationService>();
        var first = await fixture.CreateUserAsync(
            "first-admin@example.test", "First Admin", "Temporary1!Password", [SystemRoles.Admin]);
        await fixture.CreateUserAsync(
            "second-admin@example.test", "Second Admin", "Temporary1!Password", [SystemRoles.Admin]);

        var result = await service.SetEnabledAsync(
            "second-admin@example.test", first.Id, false, TestContext.Current.CancellationToken);

        Assert.False(result.IsEnabled);
    }

    private sealed class UserAdministrationFixture : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly AsyncServiceScope _scope;

        private UserAdministrationFixture(ServiceProvider provider, AsyncServiceScope scope)
        {
            _provider = provider;
            _scope = scope;
        }

        public IServiceProvider Services => _scope.ServiceProvider;
        public WechatRobotDbContext Database => Services.GetRequiredService<WechatRobotDbContext>();

        public static async Task<UserAdministrationFixture> CreateAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(TimeProvider.System);
            services.AddDbContext<WechatRobotDbContext>(
                options => options.UseInMemoryDatabase($"user-admin-{Guid.NewGuid():N}"));
            services.AddIdentityCore<ApplicationUser>(options =>
                {
                    options.Password.RequiredLength = 12;
                    options.Password.RequireDigit = true;
                    options.Password.RequireLowercase = true;
                    options.Password.RequireUppercase = true;
                    options.Password.RequireNonAlphanumeric = true;
                    options.User.RequireUniqueEmail = true;
                })
                .AddRoles<IdentityRole<Guid>>()
                .AddEntityFrameworkStores<WechatRobotDbContext>();
            services.AddScoped<UserAdministrationService>();

            var provider = services.BuildServiceProvider();
            var scope = provider.CreateAsyncScope();
            var fixture = new UserAdministrationFixture(provider, scope);
            await fixture.Database.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            var roleManager = fixture.Services.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            await IdentitySeeder.SeedRolesAsync(roleManager, TestContext.Current.CancellationToken);
            return fixture;
        }

        public async Task<ApplicationUser> CreateUserAsync(
            string email,
            string displayName,
            string password,
            IReadOnlyCollection<string> roles)
        {
            var userManager = Services.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser
            {
                Email = email,
                UserName = email,
                DisplayName = displayName,
                EmailConfirmed = true,
                IsEnabled = true
            };
            Assert.True((await userManager.CreateAsync(user, password)).Succeeded);
            Assert.True((await userManager.AddToRolesAsync(user, roles)).Succeeded);
            return user;
        }

        public async ValueTask DisposeAsync()
        {
            await _scope.DisposeAsync();
            await _provider.DisposeAsync();
        }
    }
}
