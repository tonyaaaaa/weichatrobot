using WechatRobot.Application.Models;
using WechatRobot.Infrastructure.Models;

namespace WechatRobot.UnitTests.Models;

public sealed class RequestCachingEmbeddingClientTests
{
    [Fact]
    public async Task Same_configuration_and_input_is_embedded_once_per_scope()
    {
        var inner = new CountingEmbeddingClient();
        var cache = new RequestCachingEmbeddingClient(inner);
        var configuration = new ModelProviderConfiguration(
            "https://embedding.test/v1",
            "embedding-model",
            "protected-key",
            TimeSpan.FromSeconds(10),
            0);
        var request = new EmbeddingBatchRequest(["日本三年签证"]);

        var first = await cache.CreateEmbeddingsAsync(
            configuration,
            request,
            TestContext.Current.CancellationToken);
        var second = await cache.CreateEmbeddingsAsync(
            configuration,
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, inner.CallCount);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task Different_input_is_not_reused()
    {
        var inner = new CountingEmbeddingClient();
        var cache = new RequestCachingEmbeddingClient(inner);
        var configuration = new ModelProviderConfiguration(
            "https://embedding.test/v1",
            "embedding-model",
            null,
            TimeSpan.FromSeconds(10),
            0);

        await cache.CreateEmbeddingsAsync(
            configuration,
            new(["问题一"]),
            TestContext.Current.CancellationToken);
        await cache.CreateEmbeddingsAsync(
            configuration,
            new(["问题二"]),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, inner.CallCount);
    }

    private sealed class CountingEmbeddingClient : IEmbeddingClient
    {
        public int CallCount { get; private set; }

        public Task<EmbeddingBatchResponse> CreateEmbeddingsAsync(
            ModelProviderConfiguration configuration,
            EmbeddingBatchRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult<EmbeddingBatchResponse>(new([[1f, 2f]]));
        }
    }
}
