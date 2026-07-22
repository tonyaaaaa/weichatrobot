using System.Diagnostics;
using System.Text.Json;
using WechatRobot.Application.Knowledge.Parsing;
using WechatRobot.Infrastructure.Knowledge.Ocr;

namespace WechatRobot.IntegrationTests.Knowledge;

public sealed class PdfiumPageRendererTests
{
    [Fact]
    public async Task Real_isolated_helper_renders_fixture_successfully()
    {
        var root = FindRepositoryRoot();
        var executable = Path.Combine(root, "src", "server", "WechatRobot.Worker", "bin", "Debug", "net10.0",
            OperatingSystem.IsWindows() ? "WechatRobot.PdfRenderer.exe" : "WechatRobot.PdfRenderer");
        var fixture = Path.Combine(root, "tests", "fixtures", "documents", "scanned-empty.pdf");
        var renderer = new IsolatedPdfPageRenderer(new OcrProcessingOptions
        {
            RendererExecutablePath = executable, MaximumPages = 2, MaximumImagePixels = 4_000_000,
            MaximumRenderedBytes = 4 * 1024 * 1024, RenderTimeoutSeconds = 10
        });
        using var context = Context();
        await using var countSource = File.OpenRead(fixture);

        Assert.Equal(1, await renderer.GetPageCountAsync(countSource, context));
        await using var renderSource = File.OpenRead(fixture);
        var page = Assert.Single(await renderer.RenderAsync(renderSource, [1], context));
        Assert.Equal([137, 80, 78, 71], page.ImageBytes[..4]);
        Assert.InRange((long)page.Width * page.Height, 1, 4_000_000);
    }

    [Fact]
    public async Task Isolated_helper_success_contract_uses_generated_paths_and_bounded_output()
    {
        var factory = new FakeProcessFactory(startInfo =>
        {
            var output = Argument(startInfo, "--output");
            var mode = Argument(startInfo, "--mode");
            Directory.CreateDirectory(output);
            if (mode == "count")
                File.WriteAllText(Path.Combine(output, "manifest.json"), "{\"pageCount\":1,\"pages\":[]}");
            else
            {
                File.WriteAllBytes(Path.Combine(output, "page-1.png"), [137, 80, 78, 71, 1]);
                File.WriteAllText(Path.Combine(output, "manifest.json"), "{\"pageCount\":1,\"pages\":[{\"pageNumber\":1,\"fileName\":\"page-1.png\",\"width\":1,\"height\":1}]}");
            }
            return new FakeProcess(completed: true);
        });
        var renderer = new IsolatedPdfPageRenderer(new OcrProcessingOptions
        {
            RendererExecutablePath = "trusted-renderer.exe", MaximumPages = 2, MaximumImagePixels = 4, MaximumRenderedBytes = 16, RenderTimeoutSeconds = 2
        }, factory);
        using var context = Context();

        Assert.Equal(1, await renderer.GetPageCountAsync(new MemoryStream([1, 2, 3]), context));
        var page = Assert.Single(await renderer.RenderAsync(new MemoryStream([1, 2, 3]), [1], context));

        Assert.Equal([137, 80, 78, 71, 1], page.ImageBytes);
        Assert.All(factory.Starts, start =>
        {
            Assert.False(start.UseShellExecute);
            Assert.True(start.CreateNoWindow);
            Assert.StartsWith(Path.Combine(Path.GetTempPath(), "wechatrobot-pdf-render"), Argument(start, "--input"), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(start.ArgumentList, value => value == "1; rm -rf");
        });
        Assert.Empty(Directory.GetDirectories(Path.Combine(Path.GetTempPath(), "wechatrobot-pdf-render")));
    }

    [Fact]
    public async Task Hanging_helper_is_killed_and_subsequent_render_remains_responsive()
    {
        var hanging = new FakeProcess(completed: false);
        var factory = new FakeProcessFactory(_ => hanging);
        var renderer = new IsolatedPdfPageRenderer(new OcrProcessingOptions
        {
            RendererExecutablePath = "trusted-renderer.exe", RenderTimeoutSeconds = 1, MaximumPages = 1
        }, factory);
        using var context = Context(TimeSpan.FromSeconds(5));

        var exception = await Assert.ThrowsAsync<DocumentParsingException>(() => renderer.GetPageCountAsync(new MemoryStream([1]), context));

        Assert.Equal(DocumentParsingError.Timeout, exception.Error);
        Assert.True(hanging.KilledEntireTree);

        var recoveryFactory = new FakeProcessFactory(startInfo =>
        {
            var output = Argument(startInfo, "--output");
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "manifest.json"), "{\"pageCount\":1,\"pages\":[]}");
            return new FakeProcess(completed: true);
        });
        var recovery = new IsolatedPdfPageRenderer(new OcrProcessingOptions { RendererExecutablePath = "trusted-renderer.exe", RenderTimeoutSeconds = 1 }, recoveryFactory);
        Assert.Equal(1, await recovery.GetPageCountAsync(new MemoryStream([1]), context));
    }

    private static DocumentProcessingContext Context(TimeSpan? timeout = null) => new(
        new DocumentParsingLimits(1024, 10, 8 * 1024 * 1024, timeout ?? TimeSpan.FromSeconds(10)), TestContext.Current.CancellationToken);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WechatRobot.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static string Argument(ProcessStartInfo info, string name)
    {
        var index = info.ArgumentList.IndexOf(name);
        return info.ArgumentList[index + 1];
    }

    private sealed class FakeProcessFactory(Func<ProcessStartInfo, IRendererProcess> start) : IRendererProcessFactory
    {
        public List<ProcessStartInfo> Starts { get; } = [];
        public IRendererProcess Start(ProcessStartInfo startInfo) { Starts.Add(startInfo); return start(startInfo); }
    }

    private sealed class FakeProcess(bool completed) : IRendererProcess
    {
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ExitCode => 0;
        public bool KilledEntireTree { get; private set; }
        public Task<string> StandardError => Task.FromResult(string.Empty);
        public Task WaitForExitAsync(CancellationToken cancellationToken) => completed ? Task.CompletedTask : _exit.Task.WaitAsync(cancellationToken);
        public void Kill(bool entireProcessTree) { KilledEntireTree = entireProcessTree; _exit.TrySetResult(); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
