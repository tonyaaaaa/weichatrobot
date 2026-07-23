using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WechatRobot.Application.Knowledge.Ocr;

namespace WechatRobot.Infrastructure.Knowledge.Ocr;

public sealed class AliyunOcrOptions
{
    public const string SectionName = "Ocr";
    public const string AccessKeyIdEnvironmentVariable = "ALIBABA_CLOUD_OCR_ACCESS_KEY_ID";
    public const string AccessKeySecretEnvironmentVariable = "ALIBABA_CLOUD_OCR_ACCESS_KEY_SECRET";
    public const string RealTestEnvironmentVariable = "RUN_ALIYUN_OCR_E2E";
    public string Provider { get; set; } = "Aliyun";
    public string Action { get; set; } = "RecognizeGeneral";
    public string Endpoint { get; set; } = "ocr-api.cn-hangzhou.aliyuncs.com";
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
    public int MaximumAttempts { get; set; } = 3;

    public static bool IsAllowedEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || endpoint.Length > 253 ||
            endpoint.Contains("://", StringComparison.Ordinal) ||
            endpoint.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-')))
            return false;
        var labels = endpoint.Split('.');
        if (labels.Length < 4 || !labels[0].Equals("ocr-api", StringComparison.OrdinalIgnoreCase) ||
            !labels[^2].Equals("aliyuncs", StringComparison.OrdinalIgnoreCase) ||
            !labels[^1].Equals("com", StringComparison.OrdinalIgnoreCase))
            return false;
        return labels.Skip(1).Take(labels.Length - 3).All(IsValidDnsLabel);
    }

    public void Validate()
    {
        if (Provider != "Aliyun" || Action != "RecognizeGeneral" ||
            !IsAllowedEndpoint(Endpoint) || Timeout != TimeSpan.FromSeconds(30) || MaximumAttempts != 3)
            throw new InvalidOperationException(
                "OCR provider configuration must use Aliyun RecognizeGeneral, a safe ocr-api.<region>.aliyuncs.com endpoint host, a 30-second timeout, and 3 attempts.");
    }

    private static bool IsValidDnsLabel(string label) =>
        label.Length is > 0 and <= 63 &&
        char.IsAsciiLetterOrDigit(label[0]) &&
        char.IsAsciiLetterOrDigit(label[^1]) &&
        label.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');
}

public sealed record AliyunOcrProviderResult(string? Data, string? RequestId);

public sealed class AliyunOcrProviderException(
    string code,
    string message,
    string? requestId = null,
    Exception? inner = null,
    int? statusCode = null,
    bool isTimeout = false,
    TimeSpan? retryAfter = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;
    public string? RequestId { get; } = requestId;
    public int? StatusCode { get; } = statusCode;
    public bool IsTimeout { get; } = isTimeout;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

public interface IAliyunOcrProvider
{
    Task<AliyunOcrProviderResult> RecognizeGeneralAsync(Stream body, int pageNumber, CancellationToken cancellationToken);
}

public interface IAliyunOcrDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public interface IAliyunOcrJitter
{
    double NextDouble();
}

public static class AliyunOcrResponseParser
{
    public static IReadOnlyList<OcrTextBlock> Parse(string? data)
    {
        if (string.IsNullOrWhiteSpace(data))
            throw Invalid();
        try
        {
            using var document = JsonDocument.Parse(data);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw Invalid();
            var root = document.RootElement;
            var blocks = new List<OcrTextBlock>();
            if (root.TryGetProperty("prism_wordsInfo", out var words) && words.ValueKind == JsonValueKind.Array)
            {
                foreach (var word in words.EnumerateArray())
                {
                    if (!word.TryGetProperty("word", out var textValue) || textValue.ValueKind != JsonValueKind.String) continue;
                    var text = textValue.GetString();
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    var confidence = word.TryGetProperty("prob", out var probability) && probability.TryGetInt32(out var percent)
                        ? Math.Clamp(percent / 100d, 0d, 1d) : 0d;
                    blocks.Add(new OcrTextBlock(blocks.Count, text, confidence));
                }
            }
            if (blocks.Count == 0 && root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(content.GetString()))
                blocks.Add(new OcrTextBlock(0, content.GetString()!, 0d));
            return blocks;
        }
        catch (JsonException exception)
        {
            throw new OcrClientException(OcrClientError.InvalidResponse, "Alibaba Cloud OCR response data was malformed.", exception);
        }
    }

    private static OcrClientException Invalid() =>
        new(OcrClientError.InvalidResponse, "Alibaba Cloud OCR response data was empty or malformed.");
}

public sealed class AliyunOcrClient : IOcrClient
{
    private const int MaximumBytes = 10 * 1024 * 1024;
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(30);
    private readonly IAliyunOcrProvider _provider;
    private readonly AliyunOcrOptions _options;
    private readonly ILogger<AliyunOcrClient> _logger;
    private readonly IAliyunOcrDelay _delay;
    private readonly IAliyunOcrJitter _jitter;

    public AliyunOcrClient(IAliyunOcrProvider provider, AliyunOcrOptions options, ILogger<AliyunOcrClient> logger)
        : this(provider, options, logger, new SystemAliyunOcrDelay(), new SystemAliyunOcrJitter()) { }

    public AliyunOcrClient(
        IAliyunOcrProvider provider,
        AliyunOcrOptions options,
        ILogger<AliyunOcrClient> logger,
        IAliyunOcrDelay delay,
        IAliyunOcrJitter jitter)
    {
        _provider = provider;
        _options = options;
        _logger = logger;
        _delay = delay;
        _jitter = jitter;
    }

    public async Task<IReadOnlyList<OcrPageResult>> RecognizeAsync(IReadOnlyList<OcrRenderedPage> pages, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var page in pages) Validate(page);

        var results = new List<OcrPageResult>(pages.Count);
        foreach (var page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await RecognizePageAsync(page, cancellationToken));
        }
        return results;
    }

    private async Task<OcrPageResult> RecognizePageAsync(OcrRenderedPage page, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= _options.MaximumAttempts; attempt++)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_options.Timeout);
                using var body = new MemoryStream(page.ImageBytes, writable: false);
                var response = await _provider.RecognizeGeneralAsync(body, page.PageNumber, timeout.Token);
                var blocks = AliyunOcrResponseParser.Parse(response.Data);
                _logger.LogInformation("OCR action {Action} completed in {DurationMs}ms for page {Page} attempt {Attempt}; provider code {ProviderCode}; RequestId {RequestId}",
                    _options.Action, stopwatch.ElapsedMilliseconds, page.PageNumber, attempt, "200", response.RequestId);
                return new OcrPageResult(page.PageNumber, OcrPageStatus.Completed, blocks, null);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                LogFailure(page.PageNumber, attempt, stopwatch.ElapsedMilliseconds, "Timeout", null);
                throw new OcrClientException(OcrClientError.Timeout, "Alibaba Cloud OCR request timed out.", exception);
            }
            catch (TimeoutException exception)
            {
                LogFailure(page.PageNumber, attempt, stopwatch.ElapsedMilliseconds, "Timeout", null);
                throw new OcrClientException(OcrClientError.Timeout, "Alibaba Cloud OCR request timed out.", exception);
            }
            catch (AliyunOcrProviderException exception)
            {
                LogFailure(page.PageNumber, attempt, stopwatch.ElapsedMilliseconds, exception.Code, exception.RequestId);
                if (exception.IsTimeout)
                    throw new OcrClientException(OcrClientError.Timeout, "Alibaba Cloud OCR request timed out.", exception);
                if (IsRetryable(exception) && attempt < _options.MaximumAttempts)
                {
                    await _delay.DelayAsync(GetRetryDelay(exception, attempt), cancellationToken);
                    continue;
                }
                if (exception.Code.Contains("AlgorithmTimeOut", StringComparison.OrdinalIgnoreCase))
                    throw new OcrClientException(OcrClientError.Timeout, "Alibaba Cloud OCR algorithm timed out.", exception);
                throw new OcrClientException(OcrClientError.Unavailable, "Alibaba Cloud OCR provider request failed.", exception);
            }
        }
        throw new OcrClientException(OcrClientError.Unavailable, "Alibaba Cloud OCR provider request failed.");
    }

    private void LogFailure(int page, int attempt, long duration, string code, string? requestId) =>
        _logger.LogWarning("OCR action {Action} failed in {DurationMs}ms for page {Page} attempt {Attempt}; provider code {ProviderCode}; RequestId {RequestId}",
            _options.Action, duration, page, attempt, code, requestId);

    private TimeSpan GetRetryDelay(AliyunOcrProviderException exception, int attempt)
    {
        if (exception.RetryAfter is { } guidance && guidance > TimeSpan.Zero)
            return guidance > MaximumRetryDelay ? MaximumRetryDelay : guidance;
        var baseMilliseconds = Math.Min(5_000d, 200d * Math.Pow(2, attempt - 1));
        var jitterMilliseconds = baseMilliseconds * .5d * Math.Clamp(_jitter.NextDouble(), 0d, 1d);
        return TimeSpan.FromMilliseconds(baseMilliseconds + jitterMilliseconds);
    }

    private static bool IsRetryable(AliyunOcrProviderException exception) =>
        exception.StatusCode == 503 ||
        IsRetryable(exception.Code);

    private static bool IsRetryable(string code) =>
        code.Contains("Throttl", StringComparison.OrdinalIgnoreCase) ||
        code.Contains("ServiceUnavailable", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(code, "503", StringComparison.OrdinalIgnoreCase) ||
        code.Contains("AlgorithmTimeOut", StringComparison.OrdinalIgnoreCase);

    private static void Validate(OcrRenderedPage page)
    {
        if (page.ImageBytes.Length is 0 or > MaximumBytes)
            throw InvalidPage("OCR image must be between 1 byte and 10 MB.");
        if (page.Width is < 16 or > 8191 || page.Height is < 16 or > 8191)
            throw InvalidPage("OCR image dimensions must each be between 16 and 8191 pixels.");
        var ratio = Math.Max((double)page.Width / page.Height, (double)page.Height / page.Width);
        if (ratio >= 50) throw InvalidPage("OCR image aspect ratio must be less than 50.");
        if (!IsSupportedImage(page.ImageBytes)) throw InvalidPage("OCR image format is unsupported.");
    }

    private static bool IsSupportedImage(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4e, 0x47]) || // PNG; renderer invariant
        bytes.StartsWith((ReadOnlySpan<byte>)[0xff, 0xd8, 0xff]) || // JPEG
        bytes.StartsWith("BM"u8) || bytes.StartsWith("GIF8"u8) ||
        bytes.StartsWith((ReadOnlySpan<byte>)[0x49, 0x49, 0x2a, 0x00]) || bytes.StartsWith((ReadOnlySpan<byte>)[0x4d, 0x4d, 0x00, 0x2a]) ||
        (bytes.Length >= 12 && bytes.StartsWith("RIFF"u8) && bytes[8..].StartsWith("WEBP"u8));

    private static OcrClientException InvalidPage(string message) => new(OcrClientError.InvalidResponse, message);

    private sealed class SystemAliyunOcrDelay : IAliyunOcrDelay
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
            Task.Delay(delay, cancellationToken);
    }

    private sealed class SystemAliyunOcrJitter : IAliyunOcrJitter
    {
        public double NextDouble() => Random.Shared.NextDouble();
    }
}
