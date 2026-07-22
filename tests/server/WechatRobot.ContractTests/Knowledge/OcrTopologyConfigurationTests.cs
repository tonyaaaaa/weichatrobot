using WechatRobot.Infrastructure.Knowledge.Ocr;

namespace WechatRobot.ContractTests.Knowledge;

public sealed class OcrTopologyConfigurationTests
{
    [Fact]
    public void Allows_only_loopback_or_explicit_compose_service()
    {
        Assert.True(OcrEndpointPolicy.IsAllowed(new Uri("http://127.0.0.1:18000/")));
        Assert.True(OcrEndpointPolicy.IsAllowed(new Uri("http://localhost:18000/")));
        Assert.True(OcrEndpointPolicy.IsAllowed(new Uri("http://ocr:8000/")));
        Assert.False(OcrEndpointPolicy.IsAllowed(new Uri("http://arbitrary-host:8000/")));
        Assert.False(OcrEndpointPolicy.IsAllowed(new Uri("https://ocr.example.com/")));
    }

    [Fact]
    public void Compose_and_windows_worker_defaults_are_loopback_only()
    {
        var root = FindRepositoryRoot();
        var compose = File.ReadAllText(Path.Combine(root, "docker-compose.yml"));
        var worker = File.ReadAllText(Path.Combine(root, "src", "server", "WechatRobot.Worker", "appsettings.json"));

        Assert.Contains("127.0.0.1:${OCR_PORT:-18000}:8000", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("expose:\n      - \"8000\"", compose.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.Contains("http://127.0.0.1:18000/", worker, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "docker-compose.yml"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
