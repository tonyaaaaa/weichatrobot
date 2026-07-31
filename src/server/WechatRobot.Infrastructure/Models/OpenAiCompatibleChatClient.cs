using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using WechatRobot.Application.Models;
using WechatRobot.Application.Security;

namespace WechatRobot.Infrastructure.Models;

public sealed class OpenAiCompatibleChatClient(HttpClient httpClient, ISecretProtector secretProtector) : IChatCompletionClient
{
    public async Task<ChatCompletionResponse> CompleteAsync(ModelProviderConfiguration configuration, ChatCompletionRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var body = new Dictionary<string, object?>
            {
                ["model"] = configuration.Model,
                ["messages"] = request.Messages.Select(message => new
                {
                    role = message.Role,
                    content = message.Content
                })
            };
            OpenAiCompatibleRequestTuning.Apply(
                body,
                configuration.BaseUrl,
                configuration.Model);
            if (request.WebSearch is not null)
            {
                if (!string.Equals(
                    configuration.WebSearchMode,
                    "ZaiChatCompletions",
                    StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "The configured model does not support the requested Web Search mode.");
                var webSearch = new Dictionary<string, string?>
                {
                    ["enable"] = "True",
                    ["search_engine"] = "search-prime",
                    ["search_result"] = request.WebSearch.IncludeSources ? "True" : "False",
                    ["count"] = request.WebSearch.ResultCount.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    ["search_recency_filter"] = request.WebSearch.Recency,
                    ["content_size"] = request.WebSearch.ContentSize
                };
                if (!string.IsNullOrWhiteSpace(request.WebSearch.DomainFilter))
                    webSearch["search_domain_filter"] = request.WebSearch.DomainFilter;
                body["tools"] = new[]
                {
                    new
                    {
                        type = "web_search",
                        web_search = webSearch
                    }
                };
            }

            using var response = await SendAsync(configuration, body, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            var content = document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            if (string.IsNullOrWhiteSpace(content)) throw new InvalidDataException("Chat response content is empty.");
            return new ChatCompletionResponse(
                content,
                request.WebSearch is null ? null : ParseSources(document.RootElement));
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ModelUnavailableException("Chat provider timed out.", exception);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidDataException or InvalidOperationException)
        {
            throw new ModelUnavailableException("Chat provider response is unavailable or invalid.", exception);
        }
    }

    private static IReadOnlyList<ChatSource> ParseSources(JsonElement root)
    {
        if (!root.TryGetProperty("web_search", out var sources)
            || sources.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<ChatSource>();
        foreach (var source in sources.EnumerateArray().Take(20))
        {
            var link = GetString(source, "link");
            if (!Uri.TryCreate(link, UriKind.Absolute, out var url)
                || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
                continue;
            var title = GetString(source, "title");
            if (string.IsNullOrWhiteSpace(title)) title = url.Host;
            var refer = GetString(source, "refer");
            result.Add(new ChatSource(
                Bound(title, 256)!,
                url,
                Bound(GetString(source, "media"), 128),
                Bound(GetString(source, "publish_date"), 64),
                Bound(GetString(source, "content"), 1000),
                TryParseIndex(refer)));
        }
        return result;
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? Bound(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maximumLength ? trimmed : trimmed[..maximumLength];
    }

    private static int? TryParseIndex(string? refer)
    {
        if (string.IsNullOrWhiteSpace(refer)) return null;
        var digits = new string(refer.Where(char.IsDigit).ToArray());
        return int.TryParse(
            digits,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var index)
            ? index
            : null;
    }

    private async Task<HttpResponseMessage> SendAsync(ModelProviderConfiguration configuration, object body, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                OpenAiCompatibleEndpointResolver.Resolve(configuration.BaseUrl, "chat/completions"))
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrWhiteSpace(configuration.EncryptedApiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Bearer",
                    secretProtector.Unprotect(configuration.EncryptedApiKey));
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(configuration.Timeout);
            var response = await httpClient.SendAsync(request, timeout.Token);
            if ((int)response.StatusCode < 500 || attempt >= configuration.MaxRetries)
            {
                return response;
            }

            response.Dispose();
        }
    }

}
