using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using WechatRobot.Application.Models;
using WechatRobot.Application.Security;

namespace WechatRobot.Infrastructure.Models;

public sealed class OpenAiCompatibleEmbeddingClient(HttpClient httpClient, ISecretProtector secretProtector) : IEmbeddingClient
{
    public async Task<EmbeddingBatchResponse> CreateEmbeddingsAsync(ModelProviderConfiguration configuration, EmbeddingBatchRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await SendAsync(configuration, new { model = configuration.Model, input = request.Inputs }, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            var data = document.RootElement.GetProperty("data").EnumerateArray().ToArray();
            if (data.Length != request.Inputs.Count)
                throw new InvalidDataException($"Embedding response count mismatch. Expected {request.Inputs.Count}, received {data.Length}.");

            var vectors = new IReadOnlyList<float>?[request.Inputs.Count];
            foreach (var item in data)
            {
                var index = item.GetProperty("index").GetInt32();
                if (index < 0 || index >= vectors.Length || vectors[index] is not null)
                    throw new InvalidDataException("Embedding response contains an invalid or duplicate index.");
                vectors[index] = item.GetProperty("embedding").EnumerateArray().Select(element => element.GetSingle()).ToArray();
            }

            if (vectors.Any(vector => vector is null))
                throw new InvalidDataException("Embedding response does not contain every requested input index.");
            return new EmbeddingBatchResponse(vectors.Select(vector => vector!).ToArray());
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ModelUnavailableException("Embedding provider timed out.", exception);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or KeyNotFoundException or InvalidDataException or InvalidOperationException)
        {
            throw new ModelUnavailableException("Embedding provider response is unavailable or invalid.", exception);
        }
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
