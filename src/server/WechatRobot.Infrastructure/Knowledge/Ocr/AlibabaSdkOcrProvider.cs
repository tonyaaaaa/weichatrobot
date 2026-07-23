using AlibabaCloud.SDK.Ocr_api20210707;
using AlibabaCloud.SDK.Ocr_api20210707.Models;
using AlibabaCloud.TeaUtil.Models;
using AlibabaCloud.OpenApiClient.Models;

namespace WechatRobot.Infrastructure.Knowledge.Ocr;

public sealed class AlibabaSdkOcrProvider : IAliyunOcrProvider
{
    private readonly Client _client;
    private readonly int _timeoutMilliseconds;

    public AlibabaSdkOcrProvider(AliyunOcrOptions options, string accessKeyId, string accessKeySecret)
    {
        _client = new Client(new Config
        {
            AccessKeyId = accessKeyId,
            AccessKeySecret = accessKeySecret,
            Endpoint = options.Endpoint
        });
        _timeoutMilliseconds = checked((int)options.Timeout.TotalMilliseconds);
    }

    public async Task<AliyunOcrProviderResult> RecognizeGeneralAsync(Stream body, int pageNumber, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var response = await _client.RecognizeGeneralWithOptionsAsync(
                new RecognizeGeneralRequest { Body = body },
                new RuntimeOptions { ReadTimeout = _timeoutMilliseconds, ConnectTimeout = _timeoutMilliseconds })
                .WaitAsync(cancellationToken);
            return new AliyunOcrProviderResult(response.Body?.Data, response.Body?.RequestId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var type = exception.GetType();
            var code = type.GetProperty("Code")?.GetValue(exception)?.ToString() ?? "ProviderError";
            var requestId = type.GetProperty("RequestId")?.GetValue(exception)?.ToString();
            throw new AliyunOcrProviderException(code, "Alibaba Cloud OCR SDK request failed.", requestId, exception);
        }
    }
}
