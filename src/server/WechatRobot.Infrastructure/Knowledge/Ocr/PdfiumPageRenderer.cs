using PDFtoImage;
using SkiaSharp;
using WechatRobot.Application.Knowledge.Ocr;
using WechatRobot.Application.Knowledge.Parsing;

namespace WechatRobot.Infrastructure.Knowledge.Ocr;

/// <summary>In-process PDFium rasterizer for the Worker deployment targets; it does not invoke a command shell or use file paths.</summary>
public sealed class PdfiumPageRenderer(OcrProcessingOptions options) : IPdfPageRenderer
{
    private const int Dpi = 150;

    public Task<int> GetPageCountAsync(Stream pdf, DocumentProcessingContext context) => Task.Run(() =>
    {
        context.Checkpoint("ocr-render-count-before");
#pragma warning disable CA1416 // Worker startup rejects platforms unsupported by the pinned PDFium package.
        var count = Conversion.GetPageCount(pdf, leaveOpen: true, password: null);
#pragma warning restore CA1416
        context.Checkpoint("ocr-render-count-after");
        return count;
    }, context.Token);

    public Task<IReadOnlyList<OcrRenderedPage>> RenderAsync(Stream pdf, IReadOnlyList<int> pageNumbers, DocumentProcessingContext context) => Task.Run<IReadOnlyList<OcrRenderedPage>>(() =>
    {
        var rendered = new List<OcrRenderedPage>(pageNumbers.Count);
        long totalBytes = 0;
        foreach (var pageNumber in pageNumbers)
        {
            context.Checkpoint($"ocr-render-size-before:{pageNumber}");
            var pageIndex = new Index(pageNumber - 1);
#pragma warning disable CA1416 // Worker startup rejects platforms unsupported by the pinned PDFium package.
            var pageSize = Conversion.GetPageSize(pdf, pageIndex, leaveOpen: true, password: null);
#pragma warning restore CA1416
            var width = checked((int)Math.Ceiling(pageSize.Width * Dpi / 72d));
            var height = checked((int)Math.Ceiling(pageSize.Height * Dpi / 72d));
            if (checked((long)width * height) > options.MaximumImagePixels)
                throw new DocumentParsingException(DocumentParsingError.OcrLimitExceeded, $"OCR page {pageNumber} exceeds the pixel limit.");
            context.Checkpoint($"ocr-render-page-before:{pageNumber}");
#pragma warning disable CA1416 // Worker startup rejects platforms unsupported by the pinned PDFium package.
            using var bitmap = Conversion.ToImage(pdf, pageIndex, leaveOpen: true, password: null, new RenderOptions { Dpi = Dpi });
#pragma warning restore CA1416
            using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            var bytes = png.ToArray();
            totalBytes = checked(totalBytes + bytes.LongLength);
            if (totalBytes > options.MaximumRenderedBytes)
                throw new DocumentParsingException(DocumentParsingError.OcrLimitExceeded, "OCR rendered bytes exceed the configured limit.");
            context.Checkpoint($"ocr-render-page-after:{pageNumber}");
            rendered.Add(new OcrRenderedPage(pageNumber, bytes, bitmap.Width, bitmap.Height));
        }
        return rendered;
    }, context.Token);
}
