using System.Net;
using System.Net.Http.Headers;
using WechatRobot.Application.Models;
using WechatRobot.Application.Security;
using WechatRobot.Infrastructure.Models;

namespace WechatRobot.UnitTests.Models;

public sealed class OpenAiCompatibleModelClientTests
{
    [Fact]
    public async Task Chat_request_omits_authorization_when_api_key_is_null()
    {
        var handler = new RecordingHttpHandler(
            """{"choices":[{"message":{"content":"ok"}}]}""");
        var client = new OpenAiCompatibleChatClient(
            new HttpClient(handler),
            new ThrowingProtector());

        var result = await client.CompleteAsync(
            new ModelProviderConfiguration(
                "https://local.test",
                "local-chat",
                null!,
                TimeSpan.FromSeconds(5),
                0),
            new ChatCompletionRequest([new ChatMessage("user", "ping")]),
            TestContext.Current.CancellationToken);

        Assert.Equal("ok", result.Content);
        Assert.Null(handler.Authorization);
    }

    [Fact]
    public async Task Embedding_request_omits_authorization_when_api_key_is_null()
    {
        var handler = new RecordingHttpHandler(
            """{"data":[{"index":0,"embedding":[0.1,0.2]}]}""");
        var client = new OpenAiCompatibleEmbeddingClient(
            new HttpClient(handler),
            new ThrowingProtector());

        var result = await client.CreateEmbeddingsAsync(
            new ModelProviderConfiguration(
                "https://local.test",
                "local-embedding",
                null!,
                TimeSpan.FromSeconds(5),
                0),
            new EmbeddingBatchRequest(["ping"]),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Vectors.Single().Count);
        Assert.Null(handler.Authorization);
    }

    private sealed class RecordingHttpHandler(string responseJson) : HttpMessageHandler
    {
        public AuthenticationHeaderValue? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson)
            });
        }
    }

    private sealed class ThrowingProtector : ISecretProtector
    {
        public string Protect(string plaintext) =>
            throw new InvalidOperationException("Protect should not be called.");

        public string Unprotect(string protectedValue) =>
            throw new InvalidOperationException("Unprotect should not be called.");
    }
}
