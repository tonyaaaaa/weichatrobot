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
        using var response = await SendAsync(configuration, "v1/chat/completions", new
        {
            model = configuration.Model,
            messages = request.Messages.Select(message => new { role = message.Role, content = message.Content })
        }, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var content = document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        return new ChatCompletionResponse(content ?? string.Empty);
    }

    private async Task<HttpResponseMessage> SendAsync(ModelProviderConfiguration configuration, string relativePath, object body, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(EnsureTrailingSlash(configuration.BaseUrl)), relativePath))
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secretProtector.Unprotect(configuration.EncryptedApiKey));
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

    private static string EnsureTrailingSlash(string baseUrl) => baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
}
