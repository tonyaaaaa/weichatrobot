using System.Diagnostics;
using System.Text;
using System.Text.Json;
using WechatRobot.Application.Knowledge.Ocr;
using WechatRobot.Application.Knowledge.Parsing;

namespace WechatRobot.Infrastructure.Knowledge.Ocr;

public interface IRendererProcessFactory
{
    IRendererProcess Start(ProcessStartInfo startInfo);
}

public interface IRendererProcess : IAsyncDisposable
{
    int ExitCode { get; }
    Task<string> StandardError { get; }
    Task WaitForExitAsync(CancellationToken cancellationToken);
    void Kill(bool entireProcessTree);
}

public sealed class SystemRendererProcessFactory : IRendererProcessFactory
{
    public IRendererProcess Start(ProcessStartInfo startInfo)
    {
        if (!Path.IsPathFullyQualified(startInfo.FileName) || !File.Exists(startInfo.FileName))
            throw new InvalidOperationException("The configured PDF renderer executable must be an existing absolute path.");
        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("The PDF renderer process could not be started.");
        return new SystemRendererProcess(process);
    }

    private sealed class SystemRendererProcess(Process process) : IRendererProcess
    {
        private readonly Task<string> _standardError = ReadBoundedAsync(process.StandardError, 8192);
        public int ExitCode => process.ExitCode;
        public Task<string> StandardError => _standardError;
        public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);
        public void Kill(bool entireProcessTree) { if (!process.HasExited) process.Kill(entireProcessTree); }
        public ValueTask DisposeAsync() { process.Dispose(); return ValueTask.CompletedTask; }

        private static async Task<string> ReadBoundedAsync(StreamReader reader, int maximumCharacters)
        {
            var result = new StringBuilder(maximumCharacters);
            var buffer = new char[1024];
            while (await reader.ReadAsync(buffer) is var read && read > 0)
            {
                var remaining = maximumCharacters - result.Length;
                if (remaining > 0) result.Append(buffer, 0, Math.Min(remaining, read));
            }
            return result.ToString();
        }
    }
}

public sealed class IsolatedPdfPageRenderer(OcrProcessingOptions options, IRendererProcessFactory? processFactory = null) : IPdfPageRenderer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IRendererProcessFactory _processFactory = processFactory ?? new SystemRendererProcessFactory();

    public async Task<int> GetPageCountAsync(Stream pdf, DocumentProcessingContext context)
    {
        var manifest = await ExecuteAsync(pdf, "count", [], context);
        if (manifest.PageCount is < 1 || manifest.PageCount > Math.Min(options.MaximumPages, context.Limits.MaximumPages))
            throw new DocumentParsingException(DocumentParsingError.PageLimitExceeded, "The PDF exceeds the OCR page limit.");
        return manifest.PageCount;
    }

    public async Task<IReadOnlyList<OcrRenderedPage>> RenderAsync(Stream pdf, IReadOnlyList<int> pageNumbers, DocumentProcessingContext context)
    {
        if (pageNumbers.Count == 0 || pageNumbers.Count > Math.Min(options.MaximumPages, context.Limits.MaximumPages) ||
            pageNumbers.Any(page => page < 1 || page > options.MaximumPages) || pageNumbers.Distinct().Count() != pageNumbers.Count)
            throw new DocumentParsingException(DocumentParsingError.PageLimitExceeded, "Invalid PDF render page selection.");
        var manifest = await ExecuteAsync(pdf, "render", pageNumbers, context);
        if (manifest.Pages.Count != pageNumbers.Count || !manifest.Pages.Select(page => page.PageNumber).Order().SequenceEqual(pageNumbers.Order()))
            throw new DocumentParsingException(DocumentParsingError.OcrIncomplete, "The PDF renderer returned an incomplete page set.");

        var rendered = new List<OcrRenderedPage>(manifest.Pages.Count);
        long totalBytes = 0;
        foreach (var page in manifest.Pages.OrderBy(page => page.PageNumber))
        {
            if (page.Width < 1 || page.Height < 1 || checked((long)page.Width * page.Height) > options.MaximumImagePixels ||
                page.FileName != Path.GetFileName(page.FileName))
                throw new DocumentParsingException(DocumentParsingError.OcrLimitExceeded, "The PDF renderer returned invalid page metadata.");
            var bytes = page.Data ?? throw new DocumentParsingException(DocumentParsingError.OcrIncomplete, "The PDF renderer page output was missing.");
            totalBytes = checked(totalBytes + bytes.LongLength);
            if (totalBytes > options.MaximumRenderedBytes)
                throw new DocumentParsingException(DocumentParsingError.OcrLimitExceeded, "The PDF renderer output exceeded the configured byte limit.");
            context.Reserve(bytes.LongLength, $"ocr-render:{page.PageNumber}");
            rendered.Add(new OcrRenderedPage(page.PageNumber, bytes, page.Width, page.Height));
        }
        return rendered;
    }

    private async Task<RendererManifest> ExecuteAsync(Stream pdf, string mode, IReadOnlyList<int> pages, DocumentProcessingContext context)
    {
        if (!pdf.CanSeek) throw new DocumentParsingException(DocumentParsingError.MalformedDocument, "OCR requires a seekable bounded PDF stream.");
        var root = Path.Combine(Path.GetTempPath(), "wechatrobot-pdf-render");
        var operation = Path.Combine(root, Guid.NewGuid().ToString("N"));
        var input = Path.Combine(operation, "source.pdf");
        var output = Path.Combine(operation, "output");
        Directory.CreateDirectory(output);
        try
        {
            pdf.Position = 0;
            await using (var target = new FileStream(input, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                var bufferSize = checked((int)Math.Min(81920, context.Limits.MaximumMemoryBytes));
                context.Reserve(bufferSize, "ocr-render-copy-buffer");
                var buffer = new byte[bufferSize];
                long total = 0;
                while (true)
                {
                    var read = await pdf.ReadAsync(buffer, context.Token);
                    if (read == 0) break;
                    total = checked(total + read);
                    if (total > context.Limits.MaximumSourceBytes)
                        throw new DocumentParsingException(DocumentParsingError.SourceTooLarge, "The PDF renderer input exceeded the source limit.");
                    await target.WriteAsync(buffer.AsMemory(0, read), context.Token);
                }
            }

            var start = CreateStartInfo(mode, input, output, pages);
            await using var process = _processFactory.Start(start);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.RenderTimeoutSeconds));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException exception)
            {
                process.Kill(entireProcessTree: true);
                using var reap = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await process.WaitForExitAsync(reap.Token); } catch (OperationCanceledException) { }
                if (context.Token.IsCancellationRequested) context.Token.ThrowIfCancellationRequested();
                throw new DocumentParsingException(DocumentParsingError.Timeout, "PDF rendering timed out and the renderer process was terminated.", exception);
            }
            if (process.ExitCode != 0)
            {
                var error = await process.StandardError;
                throw new DocumentParsingException(DocumentParsingError.MalformedDocument,
                    $"The isolated PDF renderer failed{(string.IsNullOrWhiteSpace(error) ? "." : ": " + error)}");
            }
            var manifestPath = Path.Combine(output, "manifest.json");
            var manifestInfo = new FileInfo(manifestPath);
            if (!manifestInfo.Exists || manifestInfo.Length > 64 * 1024)
                throw new DocumentParsingException(DocumentParsingError.OcrLimitExceeded, "The PDF renderer manifest is missing or oversized.");
            await using var manifestStream = File.OpenRead(manifestPath);
            var manifest = await JsonSerializer.DeserializeAsync<RendererManifest>(manifestStream, JsonOptions, context.Token)
                ?? throw new DocumentParsingException(DocumentParsingError.MalformedDocument, "The PDF renderer manifest is invalid.");
            var materialized = new List<RendererPageManifest>(manifest.Pages.Count);
            long outputBytes = 0;
            foreach (var page in manifest.Pages)
            {
                if (page.FileName != Path.GetFileName(page.FileName))
                    throw new DocumentParsingException(DocumentParsingError.MalformedDocument, "The PDF renderer returned an unsafe file name.");
                var pagePath = Path.Combine(output, page.FileName);
                var info = new FileInfo(pagePath);
                if (!info.Exists) throw new DocumentParsingException(DocumentParsingError.OcrIncomplete, "The PDF renderer page output was missing.");
                outputBytes = checked(outputBytes + info.Length);
                if (outputBytes > options.MaximumRenderedBytes)
                    throw new DocumentParsingException(DocumentParsingError.OcrLimitExceeded, "The PDF renderer output exceeded the configured byte limit.");
                context.Reserve(info.Length, $"ocr-render-file:{page.PageNumber}");
                materialized.Add(page with { Data = await File.ReadAllBytesAsync(pagePath, context.Token) });
            }
            return manifest with { Pages = materialized };
        }
        finally
        {
            try { if (Directory.Exists(operation)) Directory.Delete(operation, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private ProcessStartInfo CreateStartInfo(string mode, string input, string output, IReadOnlyList<int> pages)
    {
        var start = new ProcessStartInfo
        {
            FileName = options.RendererExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = false,
            WorkingDirectory = output
        };
        start.Environment.Clear();
        Add("--mode", mode); Add("--input", input); Add("--output", output);
        Add("--pages", string.Join(',', pages)); Add("--dpi", "150");
        Add("--max-pixels", options.MaximumImagePixels.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add("--max-bytes", options.MaximumRenderedBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return start;

        void Add(string name, string value) { start.ArgumentList.Add(name); start.ArgumentList.Add(value); }
    }

    private sealed record RendererManifest(int PageCount, IReadOnlyList<RendererPageManifest> Pages);
    private sealed record RendererPageManifest(int PageNumber, string FileName, int Width, int Height)
    {
        public byte[]? Data { get; init; }
    }
}
