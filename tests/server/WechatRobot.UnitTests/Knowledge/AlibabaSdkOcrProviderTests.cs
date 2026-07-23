using WechatRobot.Infrastructure.Knowledge.Ocr;
using Microsoft.Extensions.Logging.Abstractions;
using WechatRobot.Application.Knowledge.Ocr;

namespace WechatRobot.UnitTests.Knowledge;

public sealed class AlibabaSdkOcrProviderTests
{
    [Fact]
    public async Task Caller_cancellation_returns_immediately_but_owned_stream_lives_until_sdk_finishes()
    {
        var invoker = new ControlledInvoker();
        var provider = new AlibabaSdkOcrProvider(new AliyunOcrOptions(), invoker);
        using var source = new MemoryStream([1, 2, 3], writable: false);
        using var cancellation = new CancellationTokenSource();

        var call = provider.RecognizeGeneralAsync(source, 1, cancellation.Token);
        var owned = await invoker.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        source.Dispose();
        owned.Position = 0;
        Assert.Equal(1, owned.ReadByte());

        invoker.Completion.SetResult(new AlibabaSdkRawResponse("""{"content":"ok"}""", "request"));
        await invoker.Finished.Task.WaitAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => Task.Run(() => owned.ReadByte()));
    }

    [Fact]
    public async Task Normalizes_http_status_and_request_id_from_sdk_exception_data()
    {
        var provider = CreateThrowing(new FakeSdkException("ServiceError", 503,
            new Dictionary<string, object?> { ["RequestId"] = "request-from-data" }));

        var exception = await Assert.ThrowsAsync<AliyunOcrProviderException>(() =>
            provider.RecognizeGeneralAsync(new MemoryStream([1]), 1, TestContext.Current.CancellationToken));

        Assert.Equal("ServiceError", exception.Code);
        Assert.Equal(503, exception.StatusCode);
        Assert.Equal("request-from-data", exception.RequestId);
        Assert.False(exception.IsTimeout);
    }

    [Fact]
    public async Task Normalizes_retry_after_from_sdk_exception_data()
    {
        var provider = CreateThrowing(new FakeSdkException("Throttling", 429,
            new Dictionary<string, object?> { ["Retry-After"] = "7" }));
        var exception = await Assert.ThrowsAsync<AliyunOcrProviderException>(() =>
            provider.RecognizeGeneralAsync(new MemoryStream([1]), 1, TestContext.Current.CancellationToken));
        Assert.Equal(TimeSpan.FromSeconds(7), exception.RetryAfter);
    }

    [Fact]
    public async Task Normalizes_sdk_timeout_code_as_timeout()
    {
        var provider = CreateThrowing(new FakeSdkException("OperationTimeout", 408, null));

        var exception = await Assert.ThrowsAsync<AliyunOcrProviderException>(() =>
            provider.RecognizeGeneralAsync(new MemoryStream([1]), 1, TestContext.Current.CancellationToken));

        Assert.True(exception.IsTimeout);
    }

    [Fact]
    public async Task Normalizes_runtime_timeout_exception_as_timeout()
    {
        var provider = CreateThrowing(new TimeoutException("socket timed out"));
        var exception = await Assert.ThrowsAsync<AliyunOcrProviderException>(() =>
            provider.RecognizeGeneralAsync(new MemoryStream([1]), 1, TestContext.Current.CancellationToken));
        Assert.True(exception.IsTimeout);
    }

    [Fact]
    public async Task Client_retries_adapter_normalized_http_503_three_times()
    {
        var invoker = new CountingThrowingInvoker(() => new FakeSdkException("ServiceError", 503, null));
        var provider = new AlibabaSdkOcrProvider(new AliyunOcrOptions(), invoker);
        var client = new AliyunOcrClient(provider, new AliyunOcrOptions(), NullLogger<AliyunOcrClient>.Instance);

        await Assert.ThrowsAsync<OcrClientException>(() =>
            client.RecognizeAsync([PngPage()], TestContext.Current.CancellationToken));

        Assert.Equal(3, invoker.Calls);
    }

    [Fact]
    public async Task Client_maps_adapter_normalized_runtime_timeout()
    {
        var invoker = new CountingThrowingInvoker(() => new TimeoutException("socket timed out"));
        var provider = new AlibabaSdkOcrProvider(new AliyunOcrOptions(), invoker);
        var client = new AliyunOcrClient(provider, new AliyunOcrOptions(), NullLogger<AliyunOcrClient>.Instance);

        var exception = await Assert.ThrowsAsync<OcrClientException>(() =>
            client.RecognizeAsync([PngPage()], TestContext.Current.CancellationToken));

        Assert.Equal(OcrClientError.Timeout, exception.Error);
        Assert.Equal(1, invoker.Calls);
    }

    private static AlibabaSdkOcrProvider CreateThrowing(Exception exception) =>
        new(new AliyunOcrOptions(), new ThrowingInvoker(exception));

    private sealed class ThrowingInvoker(Exception exception) : IAlibabaOcrSdkInvoker
    {
        public Task<AlibabaSdkRawResponse> RecognizeGeneralAsync(Stream body) => Task.FromException<AlibabaSdkRawResponse>(exception);
    }

    private sealed class CountingThrowingInvoker(Func<Exception> exception) : IAlibabaOcrSdkInvoker
    {
        public int Calls { get; private set; }
        public Task<AlibabaSdkRawResponse> RecognizeGeneralAsync(Stream body)
        {
            Calls++;
            return Task.FromException<AlibabaSdkRawResponse>(exception());
        }
    }

    private sealed class ControlledInvoker : IAlibabaOcrSdkInvoker
    {
        public TaskCompletionSource<Stream> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<AlibabaSdkRawResponse> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AlibabaSdkRawResponse> RecognizeGeneralAsync(Stream body)
        {
            Started.SetResult(body);
            try { return await Completion.Task; }
            finally { Finished.SetResult(); }
        }
    }

    private sealed class FakeSdkException(string code, int statusCode, object? dataResult) : Exception("sdk failure")
    {
        public string Code { get; } = code;
        public int StatusCode { get; } = statusCode;
        public object? DataResult { get; } = dataResult;
    }

    private static OcrRenderedPage PngPage() =>
        new(1, [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a], 100, 100);
}
