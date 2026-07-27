namespace WechatRobot.ContractTests.Configuration;

public sealed class DotEnvHostWiringTests
{
    [Theory]
    [InlineData("WechatRobot.Api", "WebApplication.CreateBuilder")]
    [InlineData("WechatRobot.Worker", "Host.CreateApplicationBuilder")]
    public void Hosts_load_shared_dotenv_before_creating_the_builder(
        string projectName,
        string builderExpression)
    {
        var program = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src",
            "server",
            projectName,
            "Program.cs"));

        var loadIndex = program.IndexOf("DotEnvFileLoader.Load();", StringComparison.Ordinal);
        var builderIndex = program.IndexOf(builderExpression, StringComparison.Ordinal);

        Assert.True(loadIndex >= 0, $"{projectName} must call DotEnvFileLoader.Load().");
        Assert.True(builderIndex >= 0, $"{projectName} builder expression was not found.");
        Assert.True(loadIndex < builderIndex, $"{projectName} must load .env before creating its host builder.");
    }

    [Fact]
    public void Deployment_example_documents_the_complete_shared_configuration()
    {
        var path = Path.Combine(
            RepositoryRoot(),
            "deploy",
            "windows",
            "wechatrobot.env.example");
        Assert.True(File.Exists(path), "The shared production .env example must exist.");
        var content = File.ReadAllText(path);

        string[] requiredNames =
        [
            "ASPNETCORE_ENVIRONMENT",
            "DOTNET_ENVIRONMENT",
            "ConnectionStrings__WechatRobot",
            "WECHATROBOT_MASTER_KEY_BASE64",
            "Jwt__Issuer",
            "Jwt__Audience",
            "Jwt__SigningKey",
            "Cors__AllowedOrigins__0",
            "Qdrant__BaseUrl",
            "Qdrant__ApiKey",
            "Oss__AccessKeyId",
            "Oss__AccessKeySecret",
            "Oss__Bucket",
            "Oss__Endpoint",
            "Oss__PublicBaseUrl",
            "Oss__PublicReadRiskAccepted",
            "ALIBABA_CLOUD_OCR_ACCESS_KEY_ID",
            "ALIBABA_CLOUD_OCR_ACCESS_KEY_SECRET",
            "BootstrapAdmin__Email",
            "BootstrapAdmin__Password",
            "BootstrapAdmin__DisplayName",
            "Database__ApplyMigrationsOnStartup"
        ];

        foreach (var name in requiredNames)
            Assert.Contains($"{name}=", content, StringComparison.Ordinal);
        Assert.Contains(@"C:\wxrobot\config\.env", content, StringComparison.Ordinal);
        Assert.Contains("PublicBaseUrl", content, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "docker-compose.yml")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
