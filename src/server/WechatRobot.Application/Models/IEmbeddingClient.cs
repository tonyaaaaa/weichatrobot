namespace WechatRobot.Application.Models;

public interface IEmbeddingClient
{
    Task<EmbeddingBatchResponse> CreateEmbeddingsAsync(
        ModelProviderConfiguration configuration,
        EmbeddingBatchRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record EmbeddingBatchRequest(IReadOnlyList<string> Inputs);
public sealed record EmbeddingBatchResponse(IReadOnlyList<IReadOnlyList<float>> Vectors);
