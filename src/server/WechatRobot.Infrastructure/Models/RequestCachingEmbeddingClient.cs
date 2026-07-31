using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WechatRobot.Application.Models;

namespace WechatRobot.Infrastructure.Models;

public sealed class RequestCachingEmbeddingClient(IEmbeddingClient inner)
    : IEmbeddingClient
{
    private readonly ConcurrentDictionary<CacheKey, Lazy<Task<EmbeddingBatchResponse>>>
        _cache = new();

    public async Task<EmbeddingBatchResponse> CreateEmbeddingsAsync(
        ModelProviderConfiguration configuration,
        EmbeddingBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        var key = CacheKey.Create(configuration, request.Inputs);
        var pending = _cache.GetOrAdd(
            key,
            _ => new Lazy<Task<EmbeddingBatchResponse>>(
                () => inner.CreateEmbeddingsAsync(
                    configuration,
                    request,
                    cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await pending.Value;
        }
        catch
        {
            _cache.TryRemove(new KeyValuePair<CacheKey, Lazy<Task<EmbeddingBatchResponse>>>(
                key,
                pending));
            throw;
        }
    }

    private sealed record CacheKey(
        string BaseUrl,
        string Model,
        TimeSpan Timeout,
        int MaxRetries,
        string WebSearchMode,
        string SensitiveInputsHash)
    {
        public static CacheKey Create(
            ModelProviderConfiguration configuration,
            IReadOnlyList<string> inputs)
        {
            var payload = JsonSerializer.Serialize(new
            {
                configuration.EncryptedApiKey,
                Inputs = inputs
            });
            var hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
            return new(
                configuration.BaseUrl,
                configuration.Model,
                configuration.Timeout,
                configuration.MaxRetries,
                configuration.WebSearchMode,
                hash);
        }
    }
}
