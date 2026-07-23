using Microsoft.Extensions.Logging.Abstractions;
using WechatRobot.Application.Knowledge.Ocr;
using WechatRobot.Infrastructure.Knowledge.Ocr;

namespace WechatRobot.UnitTests.Knowledge;

public sealed class AliyunOcrClientTests
{
    [Fact]
    public async Task Calls_once_per_valid_png_page_and_preserves_batch_order()
    {
        var provider = new FakeProvider((page, _) => new("""{"content":"x","prism_wordsInfo":[{"word":"x","prob":90}]}""", $"request-{page}"));
        var client = Create(provider);

        var results = await client.RecognizeAsync([PngPage(8), PngPage(2)], TestContext.Current.CancellationToken);

        Assert.Equal([8, 2], results.Select(x => x.PageNumber));
        Assert.Equal([8, 2], provider.Pages);
        Assert.All(results, result => Assert.Equal(OcrPageStatus.Completed, result.Status));
    }

    [Theory]
    [MemberData(nameof(InvalidPages))]
    public async Task Rejects_limits_before_provider_invocation(OcrRenderedPage page)
    {
        var provider = new FakeProvider();
        var client = Create(provider);
        await Assert.ThrowsAsync<OcrClientException>(() => client.RecognizeAsync([page], TestContext.Current.CancellationToken));
        Assert.Empty(provider.Pages);
    }

    public static TheoryData<OcrRenderedPage> InvalidPages => new()
    {
        new OcrRenderedPage(1, new byte[10 * 1024 * 1024 + 1], 100, 100),
        PngPage(1, 15, 100),
        PngPage(1, 100, 8200),
        PngPage(1, 5001, 100),
        new OcrRenderedPage(1, [1, 2, 3, 4], 100, 100)
    };

    [Theory]
    [InlineData("Throttling")]
    [InlineData("ServiceUnavailable")]
    [InlineData("503")]
    [InlineData("AlgorithmTimeOut")]
    public async Task Retries_retryable_provider_errors_exactly_three_times(string code)
    {
        var provider = new FakeProvider((_, _) => throw new AliyunOcrProviderException(code, "sanitized", "request"));
        var exception = await Assert.ThrowsAsync<OcrClientException>(() => Create(provider).RecognizeAsync([PngPage(1)], TestContext.Current.CancellationToken));
        Assert.Equal(code == "AlgorithmTimeOut" ? OcrClientError.Timeout : OcrClientError.Unavailable, exception.Error);
        Assert.Equal(3, provider.Pages.Count);
    }

    [Theory]
    [InlineData("InvalidAccessKeyId.NotFound")]
    [InlineData("Forbidden")]
    [InlineData("InvalidImage")]
    [InlineData("UnsupportedImageFormat")]
    [InlineData("QuotaExhausted")]
    public async Task Does_not_retry_non_retryable_errors(string code)
    {
        var provider = new FakeProvider((_, _) => throw new AliyunOcrProviderException(code, "sanitized", "request"));
        await Assert.ThrowsAsync<OcrClientException>(() => Create(provider).RecognizeAsync([PngPage(1)], TestContext.Current.CancellationToken));
        Assert.Single(provider.Pages);
    }

    [Fact]
    public async Task Preserves_caller_cancellation_and_maps_provider_timeout()
    {
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var cancelled = new FakeProvider();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(cancelled).RecognizeAsync([PngPage(1)], caller.Token));
        Assert.Empty(cancelled.Pages);

        var timeout = new FakeProvider((_, _) => throw new TimeoutException("provider timeout"));
        var exception = await Assert.ThrowsAsync<OcrClientException>(() => Create(timeout).RecognizeAsync([PngPage(1)], TestContext.Current.CancellationToken));
        Assert.Equal(OcrClientError.Timeout, exception.Error);
    }

    [Fact]
    public async Task Logs_sanitized_metadata_without_raw_response_or_image_payload()
    {
        var logger = new RecordingLogger();
        var provider = new FakeProvider((_, _) => new("""{"content":"RAW_SECRET_TEXT"}""", "request-1"));
        var client = new AliyunOcrClient(provider, new AliyunOcrOptions(), logger);

        await client.RecognizeAsync([PngPage(1)], TestContext.Current.CancellationToken);

        var log = Assert.Single(logger.Messages);
        Assert.Contains("RecognizeGeneral", log, StringComparison.Ordinal);
        Assert.Contains("request-1", log, StringComparison.Ordinal);
        Assert.DoesNotContain("RAW_SECRET_TEXT", log, StringComparison.Ordinal);
        Assert.DoesNotContain("89504e47", log, StringComparison.OrdinalIgnoreCase);
    }

    private static AliyunOcrClient Create(IAliyunOcrProvider provider) =>
        new(provider, new AliyunOcrOptions(), NullLogger<AliyunOcrClient>.Instance);

    private static OcrRenderedPage PngPage(int page, int width = 100, int height = 100) =>
        new(page, [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a], width, height);

    private sealed class FakeProvider(Func<int, CancellationToken, AliyunOcrProviderResult>? callback = null) : IAliyunOcrProvider
    {
        public List<int> Pages { get; } = [];
        public Task<AliyunOcrProviderResult> RecognizeGeneralAsync(Stream body, int pageNumber, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Pages.Add(pageNumber);
            return Task.FromResult(callback?.Invoke(pageNumber, cancellationToken) ??
                new AliyunOcrProviderResult("""{"content":"ok"}""", $"request-{pageNumber}"));
        }
    }

    private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger<AliyunOcrClient>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
