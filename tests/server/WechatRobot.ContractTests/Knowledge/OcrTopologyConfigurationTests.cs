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
