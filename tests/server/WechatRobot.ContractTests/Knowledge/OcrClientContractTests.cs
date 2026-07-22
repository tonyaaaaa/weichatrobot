using System.Net;
using System.Text;
using WechatRobot.Application.Knowledge.Ocr;
using WechatRobot.Infrastructure.Knowledge.Ocr;

namespace WechatRobot.ContractTests.Knowledge;

public sealed class OcrClientContractTests
{
    [Fact]
    public async Task Sends_exact_private_OCR_contract_and_preserves_order()
    {
        string? json = null;
        var handler = new DelegateHandler(async request =>
        {
            Assert.Equal(new Uri("http://ocr:8000/v1/ocr/pages"), request.RequestUri);
            json = await request.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"pages":[{"pageNumber":2,"status":"completed","blocks":[{"order":0,"text":"正文","confidence":0.98}],"error":null}]}""", Encoding.UTF8, "application/json")
            };
        });
        var client = CreateClient(handler, TimeSpan.FromSeconds(1));

        var result = await client.RecognizeAsync([new OcrRenderedPage(2, [1, 2, 3], 10, 20)], TestContext.Current.CancellationToken);

        Assert.Equal("{\"pages\":[{\"pageNumber\":2,\"imageBase64\":\"AQID\",\"width\":10,\"height\":20}]}", json);
        Assert.Equal("正文", Assert.Single(Assert.Single(result).Blocks).Text);
        Assert.Equal(0.98, Assert.Single(Assert.Single(result).Blocks).Confidence);
    }

    [Fact]
    public async Task Maps_private_HTTP_timeout_but_preserves_caller_cancellation()
    {
        var handler = new DelegateHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = CreateClient(handler, TimeSpan.FromMilliseconds(20));
        var timeout = await Assert.ThrowsAsync<OcrClientException>(() => client.RecognizeAsync([new OcrRenderedPage(1, [1], 1, 1)], CancellationToken.None));
        Assert.Equal(OcrClientError.Timeout, timeout.Error);

        using var caller = new CancellationTokenSource();
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.RecognizeAsync([new OcrRenderedPage(1, [1], 1, 1)], caller.Token));
    }

    private static HttpOcrClient CreateClient(HttpMessageHandler handler, TimeSpan timeout) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://ocr:8000/") }, new OcrClientOptions { Timeout = timeout, MaximumResponseBytes = 4096 });

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;
        public DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) => _handler = (request, _) => handler(request);
        public DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _handler(request, cancellationToken);
    }
}
