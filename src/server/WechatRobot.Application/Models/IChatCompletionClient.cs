namespace WechatRobot.Application.Models;

public interface IChatCompletionClient
{
    Task<ChatCompletionResponse> CompleteAsync(
        ModelProviderConfiguration configuration,
        ChatCompletionRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record ModelProviderConfiguration(
    string BaseUrl,
    string Model,
    string? EncryptedApiKey,
    TimeSpan Timeout,
    int MaxRetries);

public sealed record ChatMessage(string Role, string Content);
public sealed record ChatCompletionRequest(IReadOnlyList<ChatMessage> Messages);
public sealed record ChatCompletionResponse(string Content);
public sealed class ModelUnavailableException(string message, Exception? innerException = null) : Exception(message, innerException);
