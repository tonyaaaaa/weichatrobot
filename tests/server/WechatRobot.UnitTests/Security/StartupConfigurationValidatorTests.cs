using Microsoft.Extensions.Configuration;
using WechatRobot.Infrastructure.Security;

namespace WechatRobot.UnitTests.Security;

[Collection("EnvironmentVariables")]
public sealed class StartupConfigurationValidatorTests
{
    [Fact]
    public void Valid_configuration_passes()
    {
        using var key = MasterKeyScope.Valid();
        StartupConfigurationValidator.Validate(Configuration(), requireCors: true);
    }

    [Fact]
    public void Missing_master_key_fails_fast()
    {
        using var key = MasterKeyScope.With(null);
        Assert.Contains("WECHATROBOT_MASTER_KEY_BASE64",
            Assert.Throws<InvalidOperationException>(() => StartupConfigurationValidator.Validate(Configuration(), true)).Message);
    }

    [Fact]
    public void Missing_mysql_connection_fails_fast()
    {
        using var key = MasterKeyScope.Valid();
        var values = ValidValues();
        values.Remove("ConnectionStrings:WechatRobot");
        Assert.Contains("ConnectionStrings:WechatRobot",
            Assert.Throws<InvalidOperationException>(() => StartupConfigurationValidator.Validate(Configuration(values), true)).Message);
    }

    [Fact]
    public void Wildcard_cors_origin_fails_fast()
    {
        using var key = MasterKeyScope.Valid();
        var values = ValidValues();
        values["Cors:AllowedOrigins:0"] = "https://*.example.test";
        Assert.Contains("Cors:AllowedOrigins",
            Assert.Throws<InvalidOperationException>(() => StartupConfigurationValidator.Validate(Configuration(values), true)).Message);
    }

    [Theory]
    [InlineData("https://user:password@admin.example.test")]
    [InlineData("https://admin.example.test/")]
    [InlineData("https://admin.example.test/path")]
    [InlineData("https://admin.example.test?mode=test")]
    [InlineData("https://admin.example.test#fragment")]
    public void Cors_origin_must_be_an_exact_normalized_authority(string origin)
    {
        using var key = MasterKeyScope.Valid();
        var values = ValidValues();
        values["Cors:AllowedOrigins:0"] = origin;

        Assert.Contains("Cors:AllowedOrigins",
            Assert.Throws<InvalidOperationException>(() => StartupConfigurationValidator.Validate(Configuration(values), true)).Message);
    }

    [Fact]
    public void Unsafe_upload_limits_fail_fast()
    {
        using var key = MasterKeyScope.Valid();
        var values = ValidValues();
        values["DocumentUpload:MaximumBytes"] = ((long)int.MaxValue + 1).ToString();
        Assert.Contains("upload limits",
            Assert.Throws<InvalidOperationException>(() => StartupConfigurationValidator.Validate(Configuration(values), true)).Message);
    }

    [Fact]
    public void Send_limit_above_worktool_maximum_fails_fast()
    {
        using var key = MasterKeyScope.Valid();
        var values = ValidValues();
        values["FixedReply:SendRateLimitPerMinute"] = "61";
        Assert.Contains("60",
            Assert.Throws<InvalidOperationException>(() => StartupConfigurationValidator.Validate(Configuration(values), true)).Message);
    }

    private static IConfiguration Configuration(Dictionary<string, string?>? values = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(values ?? ValidValues()).Build();

    private static Dictionary<string, string?> ValidValues() => new()
    {
        ["ConnectionStrings:WechatRobot"] = "Server=localhost;Database=wechatrobot",
        ["Cors:AllowedOrigins:0"] = "https://admin.example.test",
        ["DocumentUpload:MaximumBytes"] = "20971520",
        ["DocumentUpload:MaximumArchiveEntries"] = "2000",
        ["DocumentUpload:MaximumExpandedArchiveBytes"] = "209715200",
        ["DocumentUpload:MaximumArchiveExpansionRatio"] = "100",
        ["FixedReply:SendRateLimitPerMinute"] = "50"
    };

    private sealed class MasterKeyScope : IDisposable
    {
        private readonly string? _previous;
        private MasterKeyScope(string? value)
        {
            _previous = Environment.GetEnvironmentVariable("WECHATROBOT_MASTER_KEY_BASE64");
            Environment.SetEnvironmentVariable("WECHATROBOT_MASTER_KEY_BASE64", value);
        }
        public static MasterKeyScope Valid() => With(Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        public static MasterKeyScope With(string? value) => new(value);
        public void Dispose() => Environment.SetEnvironmentVariable("WECHATROBOT_MASTER_KEY_BASE64", _previous);
    }
}
