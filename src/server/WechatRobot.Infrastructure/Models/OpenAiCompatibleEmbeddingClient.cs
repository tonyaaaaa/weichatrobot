using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using WechatRobot.Application.Models;
using WechatRobot.Application.Security;

namespace WechatRobot.Infrastructure.Models;

public sealed class OpenAiCompatibleEmbeddingClient(HttpClient httpClient, ISecretProtector secretProtector) : IEmbeddingClient
{
    public async Task<EmbeddingResponse> CreateEmbeddingAsync(ModelProviderConfiguration configuration, EmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(configuration, new { model = configuration.Model, input = request.Input }, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var vector = document.RootElement.GetProperty("data")[0].GetProperty("embedding").EnumerateArray().Select(element => element.GetSingle()).ToArray();
        return new EmbeddingResponse(vector);
    }

    private async Task<HttpResponseMessage> SendAsync(ModelProviderConfiguration configuration, object body, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(EnsureTrailingSlash(configuration.BaseUrl)), "v1/embeddings"))
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
