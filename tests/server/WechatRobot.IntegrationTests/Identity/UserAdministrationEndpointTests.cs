using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.IntegrationTests.Infrastructure;

namespace WechatRobot.IntegrationTests.Identity;

public sealed class UserAdministrationEndpointTests : IClassFixture<UserAdministrationApiFactory>
{
    private readonly UserAdministrationApiFactory _factory;

    public UserAdministrationEndpointTests(UserAdministrationApiFactory factory) => _factory = factory;

    [Fact]
    public async Task User_management_requires_admin_role()
    {
        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/admin/users", TestContext.Current.CancellationToken)).StatusCode);

        using var operatorClient = _factory.CreateClient();
        operatorClient.DefaultRequestHeaders.Add("X-Test-Role", SystemRoles.KnowledgeOperator);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await operatorClient.GetAsync("/api/admin/users", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Admin_can_create_list_disable_and_assign_existing_roles_without_secret_exposure()
    {
        await _factory.ResetAsync();
        var admin = await _factory.CreateUserAsync(
            "admin-one@example.test", "Admin One", "Temporary1!Password", [SystemRoles.Admin]);
        await _factory.CreateUserAsync(
            "admin-two@example.test", "Admin Two", "Temporary1!Password", [SystemRoles.Admin]);
        using var client = _factory.CreateAdminClient(admin);

        const string temporaryPassword = "CreateMe1!Password";
        var create = await client.PostAsJsonAsync("/api/admin/users", new
        {
            email = "agent@example.test",
            displayName = "客服一号",
            temporaryPassword,
            roles = new[] { SystemRoles.HumanAgent }
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var createJson = await create.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(temporaryPassword, createJson, StringComparison.Ordinal);
        Assert.DoesNotContain("passwordHash", createJson, StringComparison.OrdinalIgnoreCase);
        var created = JsonSerializer.Deserialize<ManagedUserResponse>(createJson, JsonOptions)!;

        var list = await client.GetFromJsonAsync<ManagedUserPageResponse>(
            "/api/admin/users?q=agent&page=1&pageSize=20", JsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(list);
        Assert.Equal(1, list.Total);
        Assert.Equal(created.Id, list.Items.Single().Id);

        var disable = await client.PutAsJsonAsync($"/api/admin/users/{created.Id:D}/enabled",
            new { isEnabled = false }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
        Assert.False((await disable.Content.ReadFromJsonAsync<ManagedUserResponse>(
            JsonOptions, TestContext.Current.CancellationToken))!.IsEnabled);

        var updateRoles = await client.PutAsJsonAsync($"/api/admin/users/{created.Id:D}/roles",
            new { roles = new[] { SystemRoles.HumanAgent, SystemRoles.KnowledgeOperator } },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, updateRoles.StatusCode);
        var updated = await updateRoles.Content.ReadFromJsonAsync<ManagedUserResponse>(
            JsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal([SystemRoles.HumanAgent, SystemRoles.KnowledgeOperator], updated!.Roles);

        var auditJson = string.Join('\n', await _factory.ReadAuditDetailsAsync());
        Assert.DoesNotContain(temporaryPassword, auditJson, StringComparison.Ordinal);
        Assert.DoesNotContain("password", auditJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Last_enabled_admin_mutations_return_conflict_and_role_catalogue_is_fixed()
    {
        await _factory.ResetAsync();
        var admin = await _factory.CreateUserAsync(
            "only-admin@example.test", "Only Admin", "Temporary1!Password", [SystemRoles.Admin]);
        using var client = _factory.CreateAdminClient(admin);

        var roles = await client.GetFromJsonAsync<string[]>(
            "/api/admin/users/roles", TestContext.Current.CancellationToken);
        Assert.Equal(SystemRoles.All, roles);

        var disable = await client.PutAsJsonAsync($"/api/admin/users/{admin.Id:D}/enabled",
            new { isEnabled = false }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, disable.StatusCode);

        var removeAdmin = await client.PutAsJsonAsync($"/api/admin/users/{admin.Id:D}/roles",
            new { roles = new[] { SystemRoles.KnowledgeOperator } }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, removeAdmin.StatusCode);
    }

    [Fact]
    public async Task Disabled_user_cannot_login_or_resolve_current_user()
    {
        await _factory.ResetAsync();
        var disabled = await _factory.CreateUserAsync(
            "disabled@example.test", "Disabled", "Temporary1!Password", [SystemRoles.HumanAgent], false);
        using var client = _factory.CreateClient();

        var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            email = disabled.Email,
            password = "Temporary1!Password"
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);

        client.DefaultRequestHeaders.Add("X-Test-Role", SystemRoles.HumanAgent);
        client.DefaultRequestHeaders.Add("X-Test-UserId", disabled.Id.ToString("D"));
        var me = await client.GetAsync("/api/auth/me", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    [Fact]
    public async Task Admin_can_bind_and_clear_a_unique_WorkTool_display_name()
    {
        await _factory.ResetAsync();
        var admin = await _factory.CreateUserAsync(
            "nickname-admin@example.test",
            "Admin",
            "Temporary1!Password",
            [SystemRoles.Admin]);
        var agent = await _factory.CreateUserAsync(
            "nickname-agent@example.test",
            "后台名称",
            "Temporary1!Password",
            [SystemRoles.HumanAgent]);
        using var client = _factory.CreateAdminClient(admin);

        var bind = await client.PutAsJsonAsync(
            $"/api/admin/users/{agent.Id:D}/worktool-display-name",
            new { displayName = "企微客服甲" },
            TestContext.Current.CancellationToken);

        bind.EnsureSuccessStatusCode();
        var bound = await bind.Content.ReadFromJsonAsync<ManagedUserResponse>(
            JsonOptions,
            TestContext.Current.CancellationToken);
        Assert.Equal("企微客服甲", bound!.WorkToolDisplayName);

        var clear = await client.DeleteAsync(
            $"/api/admin/users/{agent.Id:D}/worktool-display-name",
            TestContext.Current.CancellationToken);
        clear.EnsureSuccessStatusCode();
        var cleared = await clear.Content.ReadFromJsonAsync<ManagedUserResponse>(
            JsonOptions,
            TestContext.Current.CancellationToken);
        Assert.Null(cleared!.WorkToolDisplayName);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private sealed record ManagedUserResponse(
        Guid Id,
        string Email,
        string DisplayName,
        bool IsEnabled,
        string? WorkToolDisplayName,
        string[] Roles);
    private sealed record ManagedUserPageResponse(ManagedUserResponse[] Items, int Total, int Page, int PageSize);
}

public sealed class UserAdministrationApiFactory : WebApplicationFactory<Program>
{
    private static readonly ServiceProvider InMemoryProvider = new ServiceCollection()
        .AddEntityFrameworkInMemoryDatabase()
        .BuildServiceProvider();
    private readonly string _databaseName = $"user-admin-api-{Guid.NewGuid():N}";

    public UserAdministrationApiFactory()
    {
        Environment.SetEnvironmentVariable("WECHATROBOT_MASTER_KEY_BASE64",
            Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        Environment.SetEnvironmentVariable("Jwt__Issuer", "user-admin-tests");
        Environment.SetEnvironmentVariable("Jwt__Audience", "user-admin-tests-api");
        Environment.SetEnvironmentVariable("Jwt__SigningKey", "user-admin-tests-signing-key-must-be-at-least-32-bytes");
        Environment.SetEnvironmentVariable("ConnectionStrings__WechatRobot", "Server=localhost;Database=user-admin-tests;User=test;Password=test");
        Environment.SetEnvironmentVariable("Cors__AllowedOrigins__0", "https://admin.example.test");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.DisableStartupMigrations();
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<WechatRobotDbContext>>();
            services.RemoveAll<WechatRobotDbContext>();
            services.AddDbContext<WechatRobotDbContext>(options =>
                options.UseInMemoryDatabase(_databaseName).UseInternalServiceProvider(InMemoryProvider));
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = "user-admin-test";
                options.DefaultChallengeScheme = "user-admin-test";
                options.DefaultForbidScheme = "user-admin-test";
            }).AddScheme<AuthenticationSchemeOptions, UserAdministrationAuthenticationHandler>(
                "user-admin-test", _ => { });
        });
    }

    public async Task ResetAsync()
    {
        using var scope = Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        await database.Database.EnsureDeletedAsync(TestContext.Current.CancellationToken);
        await database.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        await IdentitySeeder.SeedRolesAsync(
            scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>(),
            TestContext.Current.CancellationToken);
    }

    public async Task<ApplicationUser> CreateUserAsync(
        string email,
        string displayName,
        string password,
        IReadOnlyCollection<string> roles,
        bool isEnabled = true)
    {
        using var scope = Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            Email = email,
            UserName = email,
            DisplayName = displayName,
            EmailConfirmed = true,
            IsEnabled = isEnabled
        };
        Assert.True((await manager.CreateAsync(user, password)).Succeeded);
        Assert.True((await manager.AddToRolesAsync(user, roles)).Succeeded);
        return user;
    }

    public HttpClient CreateAdminClient(ApplicationUser admin)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", SystemRoles.Admin);
        client.DefaultRequestHeaders.Add("X-Test-UserId", admin.Id.ToString("D"));
        client.DefaultRequestHeaders.Add("X-Test-Name", admin.Email);
        return client;
    }

    public async Task<string[]> ReadAuditDetailsAsync()
    {
        using var scope = Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>()
            .AdministrationAudits.Select(item => item.SanitizedDetailJson)
            .ToArrayAsync(TestContext.Current.CancellationToken);
    }
}

public sealed class UserAdministrationAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    System.Text.Encodings.Web.UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var role = Request.Headers["X-Test-Role"].ToString();
        if (string.IsNullOrWhiteSpace(role))
            return Task.FromResult(AuthenticateResult.NoResult());
        var userId = Request.Headers["X-Test-UserId"].ToString();
        var name = Request.Headers["X-Test-Name"].ToString();
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, string.IsNullOrWhiteSpace(name) ? "test-user" : name),
            new(ClaimTypes.Role, role)
        };
        if (!string.IsNullOrWhiteSpace(userId))
            claims.Add(new Claim(ClaimTypes.NameIdentifier, userId));
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
