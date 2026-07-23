using System.Collections;
using System.Globalization;
using AlibabaCloud.SDK.Ocr_api20210707;
using AlibabaCloud.SDK.Ocr_api20210707.Models;
using AlibabaCloud.TeaUtil.Models;
using AlibabaCloud.OpenApiClient.Models;

namespace WechatRobot.Infrastructure.Knowledge.Ocr;

public sealed record AlibabaSdkRawResponse(string? Data, string? RequestId);

public interface IAlibabaOcrSdkInvoker
{
    Task<AlibabaSdkRawResponse> RecognizeGeneralAsync(Stream body);
}

public sealed class AlibabaSdkOcrProvider : IAliyunOcrProvider
{
    private readonly IAlibabaOcrSdkInvoker _invoker;

    public AlibabaSdkOcrProvider(AliyunOcrOptions options, string accessKeyId, string accessKeySecret)
        : this(options, new AlibabaOcrSdkInvoker(options, accessKeyId, accessKeySecret)) { }

    public AlibabaSdkOcrProvider(AliyunOcrOptions options, IAlibabaOcrSdkInvoker invoker)
    {
        _ = options;
        _invoker = invoker;
    }

    public async Task<AliyunOcrProviderResult> RecognizeGeneralAsync(Stream body, int pageNumber, CancellationToken cancellationToken)
    {
        _ = pageNumber;
        cancellationToken.ThrowIfCancellationRequested();

        // SDK 3.1.3 exposes no CancellationToken. Give the in-flight call its own
        // stream so caller cancellation can return immediately without disposing
        // bytes still being read by the continuing SDK operation.
        var ownedBody = new MemoryStream();
        try
        {
            await body.CopyToAsync(ownedBody, cancellationToken);
            ownedBody.Position = 0;
        }
        catch
        {
            ownedBody.Dispose();
            throw;
        }

        Task<AlibabaSdkRawResponse> sdkTask;
        try
        {
            sdkTask = _invoker.RecognizeGeneralAsync(ownedBody);
        }
        catch (Exception exception)
        {
            ownedBody.Dispose();
            throw Normalize(exception);
        }
        _ = DisposeWhenCompleteAsync(sdkTask, ownedBody);

        try
        {
            var response = await sdkTask.WaitAsync(cancellationToken);
            return new AliyunOcrProviderResult(response.Data, response.RequestId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw Normalize(exception);
        }
    }

    private static async Task DisposeWhenCompleteAsync(Task sdkTask, Stream body)
    {
        try { await sdkTask.ConfigureAwait(false); }
        catch { /* The foreground await or normalization observes the SDK failure. */ }
        finally { body.Dispose(); }
    }

    private static AliyunOcrProviderException Normalize(Exception exception)
    {
        var code = ReadString(exception, "Code") ?? "ProviderError";
        var dataResult = ReadMember(exception, "DataResult");
        var statusCode = ReadInt(exception, "StatusCode") ?? ReadInt(exception, "Status") ??
            FindDictionaryInt(dataResult, "StatusCode");
        var requestId = ReadString(exception, "RequestId") ??
            FindValue(dataResult, "RequestId") ??
            FindValue(exception.Data, "RequestId");
        var algorithmTimeout = code.Contains("AlgorithmTimeOut", StringComparison.OrdinalIgnoreCase);
        var isTimeout = !algorithmTimeout && (exception is TimeoutException or TaskCanceledException ||
            statusCode is 408 or 504 ||
            code.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase));
        return new AliyunOcrProviderException(code, "Alibaba Cloud OCR SDK request failed.",
            requestId, exception, statusCode, isTimeout);
    }

    private static object? ReadMember(object source, string name) =>
        source.GetType().GetProperty(name)?.GetValue(source);

    private static string? ReadString(object source, string name) =>
        ReadMember(source, name)?.ToString();

    private static int? ReadInt(object source, string name)
    {
        var value = ReadMember(source, name);
        if (value is null) return null;
        return value is int number ? number :
            int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static string? FindValue(object? source, string key)
    {
        if (source is null) return null;
        var propertyValue = ReadMember(source, key)?.ToString();
        if (!string.IsNullOrWhiteSpace(propertyValue)) return propertyValue;
        if (source is IDictionary dictionary)
            foreach (DictionaryEntry entry in dictionary)
                if (string.Equals(entry.Key?.ToString(), key, StringComparison.OrdinalIgnoreCase))
                    return entry.Value?.ToString();
        return null;
    }

    private static int? FindDictionaryInt(object? source, string key) =>
        int.TryParse(FindValue(source, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : null;

    private sealed class AlibabaOcrSdkInvoker : IAlibabaOcrSdkInvoker
    {
        private readonly Client _client;
        private readonly int _timeoutMilliseconds;

        public AlibabaOcrSdkInvoker(AliyunOcrOptions options, string accessKeyId, string accessKeySecret)
        {
            _client = new Client(new Config
            {
                AccessKeyId = accessKeyId,
                AccessKeySecret = accessKeySecret,
                Endpoint = options.Endpoint
            });
            _timeoutMilliseconds = checked((int)options.Timeout.TotalMilliseconds);
        }

        public async Task<AlibabaSdkRawResponse> RecognizeGeneralAsync(Stream body)
        {
            var response = await _client.RecognizeGeneralWithOptionsAsync(
                new RecognizeGeneralRequest { Body = body },
                new RuntimeOptions { ReadTimeout = _timeoutMilliseconds, ConnectTimeout = _timeoutMilliseconds });
            return new AlibabaSdkRawResponse(response.Body?.Data, response.Body?.RequestId);
        }
    }
}
