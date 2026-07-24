using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using WechatRobot.Infrastructure.Storage;

namespace WechatRobot.ContractTests.Storage;

public sealed class LoopbackObjectStorageContractTests
{
    [Fact]
    public async Task Put_uses_loopback_http_endpoint_and_returns_retrievable_public_url()
    {
        var handler = new RecordingHandler();
        var storage = new LoopbackObjectStorage(new HttpClient(handler), Options.Create(new LoopbackObjectStorageOptions
        {
            BaseUrl = "http://127.0.0.1:5591/objects/"
        }));
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("checkpoint"));

        var stored = await storage.PutAsync("wechatrobot/knowledge/id/1/source/中文 source.txt", content, "text/plain", TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Put, handler.Request!.Method);
        Assert.Equal("http://127.0.0.1:5591/objects/wechatrobot/knowledge/id/1/source/%E4%B8%AD%E6%96%87%20source.txt", handler.Request.RequestUri!.AbsoluteUri);
        Assert.Equal("text/plain", handler.ContentType);
        Assert.Equal("checkpoint", handler.Body);
        Assert.Equal(handler.Request.RequestUri, stored.PublicUrl);
    }

    [Fact]
    public void Constructor_rejects_non_loopback_or_https_endpoints()
    {
        Assert.Throws<InvalidOperationException>(() => Create("https://objects.example.test/"));
        Assert.Throws<InvalidOperationException>(() => Create("http://192.0.2.1/"));
        Assert.Throws<InvalidOperationException>(() => Create("http://[::1]:5591/"));
        Assert.Throws<InvalidOperationException>(() => Create("http://user@127.0.0.1:5591/"));
        Assert.Throws<InvalidOperationException>(() => Create("http://127.0.0.1:5591/objects/../private/"));
        Assert.Throws<InvalidOperationException>(() => Create("http://127.0.0.1:5591/objects/%252e%252e/private/"));
        Assert.Throws<InvalidOperationException>(() => Create("http://127.0.0.1:5591/objects\\..\\private/"));
        Assert.Throws<InvalidOperationException>(() => Create("http://127.1:5591/"));
    }

    [Theory]
    [InlineData("https://objects.example.test/secret")]
    [InlineData("http://127.0.0.1:5592/other")]
    public async Task Redirect_responses_are_failures_and_are_not_followed(string location)
    {
        var handler = new RedirectHandler(location);
        var storage = new LoopbackObjectStorage(new HttpClient(handler), Options.Create(new LoopbackObjectStorageOptions
        {
            BaseUrl = "http://127.0.0.1:5591/objects/"
        }));

        await Assert.ThrowsAsync<HttpRequestException>(() => storage.PutAsync("wechatrobot/source.txt",
            new MemoryStream("source"u8.ToArray()), "text/plain", TestContext.Current.CancellationToken));

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void Production_guard_and_primary_handler_disable_redirects()
    {
        Assert.Throws<InvalidOperationException>(() => LoopbackHttpPolicy.EnsureDevelopmentOnly(true, "Production"));
        using var handler = LoopbackHttpPolicy.CreatePrimaryHandler();
        Assert.False(handler.AllowAutoRedirect);
    }

    private static LoopbackObjectStorage Create(string baseUrl) => new(new HttpClient(new RecordingHandler()),
        Options.Create(new LoopbackObjectStorageOptions { BaseUrl = baseUrl }));

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? ContentType { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            ContentType = request.Content?.Headers.ContentType?.MediaType;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class RedirectHandler(string location) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)
            {
                Headers = { Location = new Uri(location) }
            });
        }
    }
}
