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
    public async Task Retries_normalized_http_503_exactly_three_times()
    {
        var provider = new FakeProvider((_, _) => throw new AliyunOcrProviderException(
            "ServiceError", "sanitized", "request", statusCode: 503));
        await Assert.ThrowsAsync<OcrClientException>(() =>
            Create(provider).RecognizeAsync([PngPage(1)], TestContext.Current.CancellationToken));
        Assert.Equal(3, provider.Pages.Count);
    }

    [Fact]
    public async Task Provider_retry_guidance_wins_and_is_safely_capped()
    {
        var provider = new FakeProvider((_, _) => throw new AliyunOcrProviderException(
            "Throttling", "sanitized", retryAfter: TimeSpan.FromMinutes(5)));
        var delay = new RecordingDelay();
        var client = Create(provider, delay, new FixedJitter(.5));

        await Assert.ThrowsAsync<OcrClientException>(() =>
            client.RecognizeAsync([PngPage(1)], TestContext.Current.CancellationToken));

        Assert.Equal([TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)], delay.Delays);
    }

    [Fact]
    public async Task Missing_retry_guidance_uses_exponential_backoff_with_jitter()
    {
        var provider = new FakeProvider((_, _) => throw new AliyunOcrProviderException("Throttling", "sanitized"));
        var delay = new RecordingDelay();
        var client = Create(provider, delay, new FixedJitter(.5));

        await Assert.ThrowsAsync<OcrClientException>(() =>
            client.RecognizeAsync([PngPage(1)], TestContext.Current.CancellationToken));

        Assert.Equal([TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500)], delay.Delays);
    }

    [Fact]
    public async Task Cancellation_during_retry_delay_returns_promptly_and_prevents_next_attempt()
    {
        var provider = new FakeProvider((_, _) => throw new AliyunOcrProviderException("Throttling", "sanitized"));
        var delay = new BlockingDelay();
        var client = Create(provider, delay, new FixedJitter(0));
        using var cancellation = new CancellationTokenSource();

        var call = client.RecognizeAsync([PngPage(1)], cancellation.Token);
        await delay.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Single(provider.Pages);
    }

    [Fact]
    public async Task Maps_adapter_normalized_timeout()
    {
        var provider = new FakeProvider((_, _) => throw new AliyunOcrProviderException(
            "ProviderError", "sanitized", isTimeout: true));
        var exception = await Assert.ThrowsAsync<OcrClientException>(() =>
            Create(provider).RecognizeAsync([PngPage(1)], TestContext.Current.CancellationToken));
        Assert.Equal(OcrClientError.Timeout, exception.Error);
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

    [Theory]
    [MemberData(nameof(SupportedImages))]
    public async Task Accepts_each_supported_image_signature(byte[] image)
    {
        var provider = new FakeProvider();
        await Create(provider).RecognizeAsync([new OcrRenderedPage(1, image, 100, 100)], TestContext.Current.CancellationToken);
        Assert.Single(provider.Pages);
    }

    public static TheoryData<byte[]> SupportedImages => new()
    {
        new byte[] { 0x89, 0x50, 0x4e, 0x47 },
        new byte[] { 0xff, 0xd8, 0xff, 0xe0 },
        new byte[] { 0xff, 0xd8, 0xff, 0xe1 },
        "BM00"u8.ToArray(),
        "GIF89a"u8.ToArray(),
        new byte[] { 0x49, 0x49, 0x2a, 0x00 },
        new byte[] { 0x4d, 0x4d, 0x00, 0x2a },
        "RIFF0000WEBP"u8.ToArray()
    };

    private static AliyunOcrClient Create(
        IAliyunOcrProvider provider,
        IAliyunOcrDelay? delay = null,
        IAliyunOcrJitter? jitter = null) =>
        new(provider, new AliyunOcrOptions(), NullLogger<AliyunOcrClient>.Instance,
            delay ?? new RecordingDelay(), jitter ?? new FixedJitter(0));

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

    private sealed class RecordingDelay : IAliyunOcrDelay
    {
        public List<TimeSpan> Delays { get; } = [];
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delays.Add(delay);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedJitter(double value) : IAliyunOcrJitter
    {
        public double NextDouble() => value;
    }

    private sealed class BlockingDelay : IAliyunOcrDelay
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }
}
