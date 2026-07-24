using Aliyun.OSS;
using Microsoft.Extensions.Options;
using WechatRobot.Application.Storage;

namespace WechatRobot.Infrastructure.Storage;

public interface IOssTransport
{
    bool IsConfigured { get; }
    void Put(string bucket, string key, Stream content, string contentType);
    void Delete(string bucket, string key);
}

public sealed class AliyunOssTransport : IOssTransport
{
    private readonly OssClient? _client;
    public AliyunOssTransport(IOptions<OssOptions> options)
    {
        var value = options.Value;
        if (!string.IsNullOrWhiteSpace(value.Endpoint) && !string.IsNullOrWhiteSpace(value.ResolveAccessKeyId()) && !string.IsNullOrWhiteSpace(value.ResolveAccessKeySecret()))
        {
            var endpoint = value.Endpoint.Contains(".aliyuncs.com", StringComparison.OrdinalIgnoreCase)
                ? value.Endpoint : $"{value.Endpoint.TrimEnd('/')}.aliyuncs.com";
            _client = new OssClient(endpoint, value.ResolveAccessKeyId(), value.ResolveAccessKeySecret());
        }
    }
    public bool IsConfigured => _client is not null;
    public void Put(string bucket, string key, Stream content, string contentType)
    {
        var metadata = new ObjectMetadata { ContentType = contentType };
        _client!.PutObject(bucket, key, content, metadata);
    }
    public void Delete(string bucket, string key) => _client!.DeleteObject(bucket, key);
}

public sealed class AliyunOssStorage : IObjectStorage
{
    private readonly OssOptions _options;
    private readonly IOssTransport _transport;

    public AliyunOssStorage(IOptions<OssOptions> options) : this(options, new AliyunOssTransport(options)) { }
    public AliyunOssStorage(IOptions<OssOptions> options, IOssTransport transport)
    {
        _options = options.Value;
        _transport = transport;
    }

    public Task<StoredObject> PutAsync(string objectKey, Stream content, string contentType, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        ValidateKey(objectKey);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            _transport.Put(_options.Bucket, objectKey, content, contentType);
            return new StoredObject(objectKey, BuildPublicUrl(objectKey));
        }, cancellationToken);
    }

    public Task DeleteAsync(string objectKey, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        ValidateKey(objectKey);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            _transport.Delete(_options.Bucket, objectKey);
        }, cancellationToken);
    }

    public Uri BuildPublicUrl(string objectKey)
    {
        ValidateKey(objectKey);
        if (!string.IsNullOrWhiteSpace(_options.PublicBaseUrl) &&
            (!Uri.TryCreate(_options.PublicBaseUrl, UriKind.Absolute, out var configuredBase) || configuredBase.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("The OSS public base URL must use HTTPS.");
        var encoded = string.Join('/', objectKey.Split('/').Select(Uri.EscapeDataString));
        var baseUrl = !string.IsNullOrWhiteSpace(_options.PublicBaseUrl)
            ? _options.PublicBaseUrl.TrimEnd('/')
            : $"https://{_options.Bucket}.{NormalizeEndpoint(_options.Endpoint)}";
        return new Uri($"{baseUrl}/{encoded}");
    }

    private void EnsureConfigured()
    {
        if (!_options.PublicReadRiskAccepted || string.IsNullOrWhiteSpace(_options.Bucket) || !_transport.IsConfigured)
            throw new InvalidOperationException("OSS storage is not configured and explicitly accepted for public-read use.");
    }

    private static string NormalizeEndpoint(string endpoint) => endpoint.Contains(".aliyuncs.com", StringComparison.OrdinalIgnoreCase)
        ? endpoint.TrimEnd('/') : $"{endpoint.TrimEnd('/')}.aliyuncs.com";
    private static void ValidateKey(string key)
    {
        if (!key.StartsWith("wechatrobot/", StringComparison.Ordinal) || key.Contains("..", StringComparison.Ordinal) || key.Contains('\\'))
            throw new ArgumentException("Object keys must remain below the wechatrobot/ prefix.", nameof(key));
    }
}
