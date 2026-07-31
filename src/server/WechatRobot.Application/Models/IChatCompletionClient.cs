using WechatRobot.Application.Conversations;

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
    int MaxRetries,
    string WebSearchMode = "None");

public sealed record ChatMessage(string Role, string Content);
public sealed record WebSearchOptions(
    int ResultCount,
    string Recency,
    string? DomainFilter,
    string ContentSize,
    bool IncludeSources);
public sealed record ChatSource(
    string Title,
    Uri Url,
    string? Site = null,
    string? PublishedAt = null,
    string? Summary = null,
    int? Index = null);
public sealed record ChatCompletionRequest(
    IReadOnlyList<ChatMessage> Messages,
    WebSearchOptions? WebSearch = null,
    IReadOnlyList<RetrievalEvidence>? ControlledEvidence = null);
public sealed record ChatCompletionResponse(
    string Content,
    IReadOnlyList<ChatSource>? Sources = null);
public sealed class ModelUnavailableException(string message, Exception? innerException = null) : Exception(message, innerException);
