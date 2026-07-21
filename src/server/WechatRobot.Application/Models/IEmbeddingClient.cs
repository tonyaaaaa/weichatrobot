namespace WechatRobot.Application.Models;

public interface IEmbeddingClient
{
    Task<EmbeddingResponse> CreateEmbeddingAsync(
        ModelProviderConfiguration configuration,
        EmbeddingRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record EmbeddingRequest(string Input);
public sealed record EmbeddingResponse(IReadOnlyList<float> Vector);
