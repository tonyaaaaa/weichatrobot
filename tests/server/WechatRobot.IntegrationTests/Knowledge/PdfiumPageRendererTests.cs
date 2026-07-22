using WechatRobot.Application.Knowledge.Parsing;
using WechatRobot.Infrastructure.Knowledge.Ocr;

namespace WechatRobot.IntegrationTests.Knowledge;

public sealed class PdfiumPageRendererTests
{
    [Fact]
    public async Task Renders_only_requested_pages_in_memory_with_bounded_dimensions()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "tests", "fixtures", "documents", "scanned-empty.pdf"));
        await using var pdf = File.OpenRead(path);
        using var context = new DocumentProcessingContext(new DocumentParsingLimits(1024 * 1024, 10, 8 * 1024 * 1024, TimeSpan.FromSeconds(10)), TestContext.Current.CancellationToken);
        var renderer = new PdfiumPageRenderer(new OcrProcessingOptions { MaximumImagePixels = 4_000_000, MaximumRenderedBytes = 4 * 1024 * 1024 });

        Assert.Equal(1, await renderer.GetPageCountAsync(pdf, context));
        pdf.Position = 0;
        var page = Assert.Single(await renderer.RenderAsync(pdf, [1], context));
        Assert.Equal(1, page.PageNumber);
        Assert.True(page.ImageBytes.Length > 8);
        Assert.Equal(new byte[] { 137, 80, 78, 71 }, page.ImageBytes[..4]);
        Assert.InRange((long)page.Width * page.Height, 1, 4_000_000);
    }
}
