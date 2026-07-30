using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using WechatRobot.Application.Security;
using WechatRobot.Infrastructure.Agents;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.UnitTests.Agents;

public sealed class OpenAiCompatibleAgentChatClientFactoryTests
{
    [Theory]
    [InlineData("", "/v1/chat/completions")]
    [InlineData("/api/coding/paas/v4", "/api/coding/paas/v4/chat/completions")]
    [InlineData("/api/coding/paas/v4/chat/completions", "/api/coding/paas/v4/chat/completions")]
    public async Task Factory_preserves_compatible_endpoint_and_omits_authorization_without_key(
        string configuredPath,
        string expectedPath)
    {
        await using var server = new SingleRequestServer();
        await using var database = Database();
        var model = Model($"http://127.0.0.1:{server.Port}{configuredPath}", encryptedApiKey: null);
        database.ModelConfigs.Add(model);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        var factory = new OpenAiCompatibleAgentChatClientFactory(database, new PassThroughProtector());
        using var client = await factory.CreateAsync(model.Id, TestContext.Current.CancellationToken);

        var responseTask = client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            cancellationToken: TestContext.Current.CancellationToken);
        var request = await server.ReceiveAndReplyAsync(TestContext.Current.CancellationToken);
        var response = await responseTask;

        Assert.Equal("ok", response.Text);
        Assert.Equal(expectedPath, request.Path);
        Assert.False(request.Headers.ContainsKey("Authorization"));
    }

    private static WechatRobotDbContext Database() =>
        new(new DbContextOptionsBuilder<WechatRobotDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private static ModelConfigEntity Model(string baseUrl, string? encryptedApiKey) =>
        new()
        {
            Name = "agent-test",
            NormalizedName = "AGENT-TEST",
            Provider = "OpenAI compatible",
            ConfigurationType = "chat",
            BaseUrl = baseUrl,
            Model = "test-model",
            EncryptedApiKey = encryptedApiKey,
            TimeoutSeconds = 10
        };

    private sealed class PassThroughProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }

    private sealed class SingleRequestServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);

        public SingleRequestServer()
        {
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        public int Port { get; }

        public async Task<CapturedRequest> ReceiveAndReplyAsync(CancellationToken cancellationToken)
        {
            using var socket = await listener.AcceptTcpClientAsync(cancellationToken);
            await using var stream = socket.GetStream();
            using var reader = new StreamReader(
                stream,
                Encoding.ASCII,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 4096,
                leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(cancellationToken)
                ?? throw new InvalidDataException("Missing HTTP request line.");
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (await reader.ReadLineAsync(cancellationToken) is { Length: > 0 } line)
            {
                var separator = line.IndexOf(':');
                if (separator > 0)
                {
                    headers[line[..separator]] = line[(separator + 1)..].Trim();
                }
            }
            if (headers.TryGetValue("Content-Length", out var value)
                && int.TryParse(value, out var contentLength)
                && contentLength > 0)
            {
                var body = new char[contentLength];
                await reader.ReadBlockAsync(body, cancellationToken);
            }

            const string json =
                """{"id":"test","object":"chat.completion","created":1,"model":"test-model","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""";
            var bytes = Encoding.UTF8.GetBytes(json);
            var responseHeaders = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(responseHeaders, cancellationToken);
            await stream.WriteAsync(bytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);

            var target = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1];
            return new CapturedRequest(new Uri($"http://127.0.0.1{target}").AbsolutePath, headers);
        }

        public ValueTask DisposeAsync()
        {
            listener.Stop();
            return ValueTask.CompletedTask;
        }
    }

    private sealed record CapturedRequest(string Path, IReadOnlyDictionary<string, string> Headers);
}
