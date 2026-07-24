using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace WechatRobot.IntegrationTests.Infrastructure;

public static class NonRelationalApiFactoryConfiguration
{
    public const string ApplyMigrationsKey = "Database:ApplyMigrationsOnStartup";
    public const string ApplyMigrationsEnvironmentVariable = "Database__ApplyMigrationsOnStartup";

    public static void DisableStartupMigrations(this IWebHostBuilder builder) =>
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { [ApplyMigrationsKey] = "false" }));
}
