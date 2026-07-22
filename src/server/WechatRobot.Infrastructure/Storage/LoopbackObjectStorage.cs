using Microsoft.Extensions.Options;
using WechatRobot.Application.Storage;

namespace WechatRobot.Infrastructure.Storage;

public sealed class LoopbackObjectStorageOptions
{
    public const string SectionName = "LoopbackObjectStorage";
    public string BaseUrl { get; set; } = string.Empty;
}

/// <summary>
/// Development-only HTTP object storage used by deterministic local smoke tests.
/// It deliberately rejects every non-loopback destination.
/// </summary>
public sealed class LoopbackObjectStorage : IObjectStorage
{
    private readonly HttpClient _httpClient;
    private readonly Uri _baseUrl;

    public LoopbackObjectStorage(HttpClient httpClient, IOptions<LoopbackObjectStorageOptions> options)
    {
        _httpClient = httpClient;
        if (!Uri.TryCreate(options.Value.BaseUrl, UriKind.Absolute, out var baseUrl) ||
            baseUrl.Scheme != Uri.UriSchemeHttp || !baseUrl.IsLoopback)
        {
            throw new InvalidOperationException("Loopback object storage must use an HTTP loopback URL.");
        }

        _baseUrl = new Uri(baseUrl.AbsoluteUri.TrimEnd('/') + "/");
    }

    public async Task<StoredObject> PutAsync(string objectKey, Stream content, string contentType, CancellationToken cancellationToken)
    {
        var objectUrl = BuildObjectUrl(objectKey);
        using var request = new HttpRequestMessage(HttpMethod.Put, objectUrl)
        {
            Content = new StreamContent(content)
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return new StoredObject(objectKey, objectUrl);
    }

    public async Task DeleteAsync(string objectKey, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.DeleteAsync(BuildObjectUrl(objectKey), cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private Uri BuildObjectUrl(string objectKey)
    {
        if (!objectKey.StartsWith("wechatrobot/", StringComparison.Ordinal) || objectKey.Contains("..", StringComparison.Ordinal) || objectKey.Contains('\\'))
            throw new ArgumentException("Object keys must remain below the wechatrobot/ prefix.", nameof(objectKey));
        var encoded = string.Join('/', objectKey.Split('/').Select(Uri.EscapeDataString));
        return new Uri(_baseUrl, encoded);
    }
}
