using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.IntegrationTests.Auth;
using WechatRobot.IntegrationTests.Handoffs;
using WechatRobot.IntegrationTests.Knowledge;
using WechatRobot.IntegrationTests.Models;

namespace WechatRobot.IntegrationTests.Infrastructure;

public sealed class ApiFactoryStartupTests
{
    [Fact]
    public void Non_relational_factories_start_when_process_environment_forces_startup_migrations()
    {
        var previous = Environment.GetEnvironmentVariable(NonRelationalApiFactoryConfiguration.ApplyMigrationsEnvironmentVariable);
        Environment.SetEnvironmentVariable(NonRelationalApiFactoryConfiguration.ApplyMigrationsEnvironmentVariable, "true");
        try
        {
            using var model = new ModelConfigurationApiFactory();
            using var documents = new DocumentUploadApiFactory();
            using var handoffs = new HandoffReadEndpointTests.ReadApiFactory();
            using var authorization = new RoleAuthorizationApiFactory();

            AssertInMemoryFactoryStarts(model);
            AssertInMemoryFactoryStarts(documents);
            AssertInMemoryFactoryStarts(handoffs);
            Assert.NotNull(authorization.Services);
        }
        finally
        {
            Environment.SetEnvironmentVariable(NonRelationalApiFactoryConfiguration.ApplyMigrationsEnvironmentVariable, previous);
        }
    }

    private static void AssertInMemoryFactoryStarts(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        Assert.Equal("Microsoft.EntityFrameworkCore.InMemory", database.Database.ProviderName);
    }
}
