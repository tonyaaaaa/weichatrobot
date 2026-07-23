using WechatRobot.Infrastructure.Knowledge.Ocr;

namespace WechatRobot.ContractTests.Knowledge;

public sealed class OcrTopologyConfigurationTests
{
    [Fact]
    public void Provider_defaults_are_binding()
    {
        var options = new AliyunOcrOptions();
        Assert.Equal("Aliyun", options.Provider);
        Assert.Equal("RecognizeGeneral", options.Action);
        Assert.Equal("ocr-api.cn-hangzhou.aliyuncs.com", options.Endpoint);
        Assert.Equal(TimeSpan.FromSeconds(30), options.Timeout);
        Assert.Equal(3, options.MaximumAttempts);
        Assert.Equal("ALIBABA_CLOUD_OCR_ACCESS_KEY_ID", AliyunOcrOptions.AccessKeyIdEnvironmentVariable);
        Assert.Equal("ALIBABA_CLOUD_OCR_ACCESS_KEY_SECRET", AliyunOcrOptions.AccessKeySecretEnvironmentVariable);
        Assert.Equal("RUN_ALIYUN_OCR_E2E", AliyunOcrOptions.RealTestEnvironmentVariable);
    }

    [Theory]
    [InlineData("ocr-api.cn-hangzhou.aliyuncs.com", true)]
    [InlineData("ocr-api.cn-shanghai.aliyuncs.com", true)]
    [InlineData("ocr-api.ap-southeast-1.aliyuncs.com", true)]
    [InlineData("ocr-api.foo.bar.aliyuncs.com", false)]
    [InlineData("https://ocr-api.cn-hangzhou.aliyuncs.com", false)]
    [InlineData("user@ocr-api.cn-hangzhou.aliyuncs.com", false)]
    [InlineData("ocr-api.cn-hangzhou.aliyuncs.com/path", false)]
    [InlineData("ocr-api.cn-hangzhou.aliyuncs.com:443", false)]
    [InlineData("ocr-api.evil.example", false)]
    [InlineData("ocr-api..aliyuncs.com", false)]
    public void Allows_only_safe_Alibaba_OCR_endpoint_hosts(string endpoint, bool expected)
    {
        Assert.Equal(expected, AliyunOcrOptions.IsAllowedEndpoint(endpoint));
    }

    [Fact]
    public void Complete_startup_options_validation_allows_region_override_and_rejects_arbitrary_host()
    {
        new AliyunOcrOptions { Endpoint = "ocr-api.cn-shanghai.aliyuncs.com" }.Validate();
        var invalid = new AliyunOcrOptions { Endpoint = "ocr-api.evil.example" };
        var exception = Assert.Throws<InvalidOperationException>(invalid.Validate);
        Assert.Contains("ocr-api.<region>.aliyuncs.com", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Repository_has_no_local_ocr_service_or_container()
    {
        var root = FindRepositoryRoot();
        Assert.False(Directory.Exists(Path.Combine(root, "src", "ocr-service")));
        var compose = File.ReadAllText(Path.Combine(root, "docker-compose.yml"));
        Assert.DoesNotContain("\n  ocr:", compose.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.DoesNotContain("paddle", compose, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "docker-compose.yml"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
