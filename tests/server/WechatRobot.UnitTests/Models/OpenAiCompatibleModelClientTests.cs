using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
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
    public async Task Agent_framework_transport_sends_rewritten_body_with_computed_content_length()
    {
        await using var server = new SingleRequestServer();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var client = new HttpClient(
            new OpenAiCompatibleRequestTuningHandler(
                new SocketsHttpHandler(),
                "https://api.z.ai/api/coding/paas/v4",
                "glm-5.2",
                removeAuthorization: false));

        var receiveTask = server.ReceiveAndReplyAsync(timeout.Token);
        using var response = await client.PostAsync(
            $"http://127.0.0.1:{server.Port}/chat/completions",
            new StringContent(
                """{"model":"glm-5.2","messages":[]}""",
                Encoding.UTF8,
                "application/json"),
            timeout.Token);
        var captured = await receiveTask;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(captured.Body.Length, captured.ContentLength);
        Assert.Equal("application/json", captured.ContentType);
        using var json = JsonDocument.Parse(captured.Body);
        Assert.Equal(
            "disabled",
            json.RootElement.GetProperty("thinking")
                .GetProperty("type")
                .GetString());
        Assert.Equal(
            2048,
            json.RootElement.GetProperty("max_tokens").GetInt32());
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

    private sealed class SingleRequestServer : IAsyncDisposable
    {
        private static readonly byte[] HeaderTerminator = "\r\n\r\n"u8.ToArray();
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private TcpClient? activeClient;

        public SingleRequestServer()
        {
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        public int Port { get; }

        public async Task<CapturedRequest> ReceiveAndReplyAsync(
            CancellationToken cancellationToken)
        {
            activeClient = await listener.AcceptTcpClientAsync(cancellationToken);
            await using var stream = activeClient.GetStream();
            var headerBytes = await ReadHeadersAsync(stream, cancellationToken);
            var headerText = Encoding.ASCII.GetString(headerBytes);
            var headers = ParseHeaders(headerText);
            var contentLength = int.Parse(
                headers["Content-Length"],
                System.Globalization.CultureInfo.InvariantCulture);
            var body = new byte[contentLength];
            await ReadExactlyAsync(stream, body, cancellationToken);

            const string responseJson = "{}";
            var responseBody = Encoding.UTF8.GetBytes(responseJson);
            var responseHeaders = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {responseBody.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(responseHeaders, cancellationToken);
            await stream.WriteAsync(responseBody, cancellationToken);
            await stream.FlushAsync(cancellationToken);

            return new CapturedRequest(
                contentLength,
                headers["Content-Type"].Split(';', 2)[0],
                body);
        }

        private static async Task<byte[]> ReadHeadersAsync(
            Stream stream,
            CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            var current = new byte[1];
            while (buffer.Length < 64 * 1024)
            {
                var read = await stream.ReadAsync(current, cancellationToken);
                if (read == 0)
                    throw new EndOfStreamException("HTTP headers ended early.");
                buffer.WriteByte(current[0]);
                if (buffer.Length >= HeaderTerminator.Length
                    && buffer.GetBuffer().AsSpan(
                        (int)buffer.Length - HeaderTerminator.Length,
                        HeaderTerminator.Length).SequenceEqual(HeaderTerminator))
                {
                    return buffer.ToArray();
                }
            }

            throw new InvalidDataException("HTTP headers exceeded the test limit.");
        }

        private static Dictionary<string, string> ParseHeaders(string value)
        {
            var headers = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var line in value.Split("\r\n").Skip(1))
            {
                var separator = line.IndexOf(':');
                if (separator > 0)
                    headers[line[..separator]] = line[(separator + 1)..].Trim();
            }
            return headers;
        }

        private static async Task ReadExactlyAsync(
            Stream stream,
            byte[] buffer,
            CancellationToken cancellationToken)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = await stream.ReadAsync(
                    buffer.AsMemory(offset),
                    cancellationToken);
                if (read == 0)
                    throw new EndOfStreamException("HTTP body ended early.");
                offset += read;
            }
        }

        public ValueTask DisposeAsync()
        {
            activeClient?.Dispose();
            listener.Stop();
            return ValueTask.CompletedTask;
        }
    }

    private sealed record CapturedRequest(
        int ContentLength,
        string ContentType,
        byte[] Body);
}
