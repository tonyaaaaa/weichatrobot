using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
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

    [Theory]
    [InlineData("https://api.z.ai/api/coding/paas/v4", "glm-5.2")]
    [InlineData("https://open.bigmodel.cn/api/paas/v4", "GLM-5")]
    public async Task Official_zai_glm_chat_disables_thinking(
        string baseUrl,
        string model)
    {
        var handler = new RecordingHttpHandler(
            """{"choices":[{"message":{"content":"ok"}}]}""");
        var client = new OpenAiCompatibleChatClient(
            new HttpClient(handler),
            new ThrowingProtector());

        await client.CompleteAsync(
            new ModelProviderConfiguration(
                baseUrl,
                model,
                null!,
                TimeSpan.FromSeconds(5),
                0),
            new ChatCompletionRequest([new ChatMessage("user", "ping")]),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            "disabled",
            handler.Body.RootElement
                .GetProperty("thinking")
                .GetProperty("type")
                .GetString());
        Assert.Equal(2048, handler.Body.RootElement
            .GetProperty("max_tokens")
            .GetInt32());
    }

    [Fact]
    public async Task Non_Zai_chat_does_not_send_provider_specific_thinking_option()
    {
        var handler = new RecordingHttpHandler(
            """{"choices":[{"message":{"content":"ok"}}]}""");
        var client = new OpenAiCompatibleChatClient(
            new HttpClient(handler),
            new ThrowingProtector());

        await client.CompleteAsync(
            new ModelProviderConfiguration(
                "https://local.test",
                "glm-5.2",
                null!,
                TimeSpan.FromSeconds(5),
                0),
            new ChatCompletionRequest([new ChatMessage("user", "ping")]),
            TestContext.Current.CancellationToken);

        Assert.False(handler.Body.RootElement.TryGetProperty(
            "thinking",
            out _));
    }

    [Fact]
    public async Task Agent_framework_transport_disables_official_zai_thinking()
    {
        var recorder = new RecordingHttpHandler(
            """{"choices":[{"message":{"content":"ok"}}]}""");
        using var client = new HttpClient(
            new OpenAiCompatibleRequestTuningHandler(
                recorder,
                "https://api.z.ai/api/coding/paas/v4",
                "glm-5.2",
                removeAuthorization: false));
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "test-key");

        using var response = await client.PostAsync(
            "https://api.z.ai/api/coding/paas/v4/chat/completions",
            new StringContent(
                """{"model":"glm-5.2","messages":[]}""",
                System.Text.Encoding.UTF8,
                "application/json"),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            "disabled",
            recorder.Body.RootElement
                .GetProperty("thinking")
                .GetProperty("type")
                .GetString());
        Assert.Equal(2048, recorder.Body.RootElement
            .GetProperty("max_tokens")
            .GetInt32());
        Assert.Equal("Bearer", recorder.Authorization?.Scheme);
        Assert.Equal("application/json", recorder.ContentType);
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
        public JsonDocument Body { get; private set; } = null!;
        public string? ContentType { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization;
            ContentType = request.Content?.Headers.ContentType?.MediaType;
            Body = JsonDocument.Parse(
                await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson)
            };
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
