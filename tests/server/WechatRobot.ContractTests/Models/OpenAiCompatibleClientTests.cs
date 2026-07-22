using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using WechatRobot.Application.Models;
using WechatRobot.Application.Security;
using WechatRobot.Infrastructure.Models;

namespace WechatRobot.ContractTests.Models;

public sealed class OpenAiCompatibleClientTests
{
    [Fact]
    public async Task Chat_and_embedding_clients_use_their_independent_provider_configurations()
    {
        await using var chatServer = await FakeOpenAiServer.StartAsync("{\"choices\":[{\"message\":{\"content\":\"chat-result\"}}]}");
        await using var embeddingServer = await FakeOpenAiServer.StartAsync("{\"data\":[{\"index\":0,\"embedding\":[0.25,0.5]}]}");
        var protector = new PassthroughSecretProtector();
        var chat = new OpenAiCompatibleChatClient(new HttpClient(), protector);
        var embedding = new OpenAiCompatibleEmbeddingClient(new HttpClient(), protector);

        var chatResponse = await chat.CompleteAsync(
            new ModelProviderConfiguration(chatServer.BaseUrl, "chat-model", "chat-key", TimeSpan.FromSeconds(5), 0),
            new ChatCompletionRequest([new ChatMessage("user", "hello")]),
            TestContext.Current.CancellationToken);
        var embeddingResponse = await embedding.CreateEmbeddingsAsync(
            new ModelProviderConfiguration(embeddingServer.BaseUrl, "embedding-model", "embedding-key", TimeSpan.FromSeconds(5), 0),
            new EmbeddingBatchRequest(["hello"]),
            TestContext.Current.CancellationToken);

        Assert.Equal("chat-result", chatResponse.Content);
        Assert.Equal([0.25f, 0.5f], embeddingResponse.Vectors.Single());
        Assert.Equal("/v1/chat/completions", chatServer.Request.Path);
        Assert.Equal("/v1/embeddings", embeddingServer.Request.Path);
        Assert.Equal("Bearer chat-key", chatServer.Request.Authorization);
        Assert.Equal("Bearer embedding-key", embeddingServer.Request.Authorization);
        Assert.Equal("chat-model", chatServer.Request.Json.RootElement.GetProperty("model").GetString());
        Assert.Equal("embedding-model", embeddingServer.Request.Json.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task Embedding_client_sends_an_input_array_and_orders_vectors_by_response_index()
    {
        await using var server = await FakeOpenAiServer.StartAsync(
            "{\"data\":[{\"index\":1,\"embedding\":[2,2]},{\"index\":0,\"embedding\":[1,1]}]}");
        var client = new OpenAiCompatibleEmbeddingClient(new HttpClient(), new PassthroughSecretProtector());

        var response = await client.CreateEmbeddingsAsync(
            new ModelProviderConfiguration(server.BaseUrl, "embedding-model", "key", TimeSpan.FromSeconds(5), 0),
            new EmbeddingBatchRequest(["first", "second"]),
            TestContext.Current.CancellationToken);

        Assert.Equal("{\"model\":\"embedding-model\",\"input\":[\"first\",\"second\"]}", server.Request.Json.RootElement.GetRawText());
        Assert.Equal([1f, 1f], response.Vectors[0]);
        Assert.Equal([2f, 2f], response.Vectors[1]);
    }

    [Fact]
    public async Task Embedding_client_rejects_a_response_with_the_wrong_vector_count()
    {
        await using var server = await FakeOpenAiServer.StartAsync(
            "{\"data\":[{\"index\":0,\"embedding\":[1,1]}]}");
        var client = new OpenAiCompatibleEmbeddingClient(new HttpClient(), new PassthroughSecretProtector());

        await Assert.ThrowsAsync<InvalidDataException>(() => client.CreateEmbeddingsAsync(
            new ModelProviderConfiguration(server.BaseUrl, "embedding-model", "key", TimeSpan.FromSeconds(5), 0),
            new EmbeddingBatchRequest(["first", "second"]),
            TestContext.Current.CancellationToken));
    }

    private sealed class PassthroughSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }

    private sealed class FakeOpenAiServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly string _responseBody;
        private readonly Task _requestTask;

        private FakeOpenAiServer(TcpListener listener, string responseBody)
        {
            _listener = listener;
            _responseBody = responseBody;
            _requestTask = CaptureRequestAsync();
        }

        public CapturedRequest Request { get; private set; } = null!;
        public string BaseUrl => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

        public static Task<FakeOpenAiServer> StartAsync(string responseBody)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new FakeOpenAiServer(listener, responseBody));
        }

        private async Task CaptureRequestAsync()
        {
            using var client = await _listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(TestContext.Current.CancellationToken) ?? throw new InvalidOperationException("No request line.");
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(TestContext.Current.CancellationToken)))
            {
                var separator = line.IndexOf(':');
                headers[line[..separator]] = line[(separator + 1)..].Trim();
            }

            var bodyLength = int.Parse(headers["Content-Length"], System.Globalization.CultureInfo.InvariantCulture);
            var bodyChars = new char[bodyLength];
            var read = 0;
            while (read < bodyChars.Length)
            {
                read += await reader.ReadAsync(bodyChars.AsMemory(read, bodyChars.Length - read), TestContext.Current.CancellationToken);
            }

            Request = new CapturedRequest(requestLine.Split(' ')[1], headers["Authorization"], JsonDocument.Parse(new string(bodyChars)));
            var payload = Encoding.UTF8.GetBytes(_responseBody);
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n"), TestContext.Current.CancellationToken);
            await stream.WriteAsync(payload, TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            await _requestTask;
            Request.Json.Dispose();
        }
    }

    private sealed record CapturedRequest(string Path, string Authorization, JsonDocument Json);
}
