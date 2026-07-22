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
}
