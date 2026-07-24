using Microsoft.Extensions.Options;
using WechatRobot.Infrastructure.Storage;

namespace WechatRobot.ContractTests.Storage;

public sealed class AliyunOssStorageContractTests
{
    [Fact]
    public void Public_url_uses_bucket_endpoint_and_encodes_each_key_segment()
    {
        var storage = Create(new OssOptions { Bucket = "newsplatform", Endpoint = "oss-cn-shenzhen" });
        var url = storage.BuildPublicUrl("wechatrobot/knowledge/id/1/source/中文 source.txt");
        Assert.Equal("https://newsplatform.oss-cn-shenzhen.aliyuncs.com/wechatrobot/knowledge/id/1/source/%E4%B8%AD%E6%96%87%20source.txt", url.AbsoluteUri);
    }

    [Fact]
    public void Public_base_url_must_be_https()
    {
        var storage = Create(new OssOptions { Bucket = "newsplatform", Endpoint = "oss-cn-shenzhen", PublicBaseUrl = "http://objects.example.test" });
        Assert.Throws<InvalidOperationException>(() => storage.BuildPublicUrl("wechatrobot/knowledge/id/1/source/source.txt"));
    }

    [Fact]
    public void Object_key_cannot_escape_the_wechatrobot_prefix()
    {
        var storage = Create(new OssOptions { Bucket = "newsplatform", Endpoint = "oss-cn-shenzhen" });
        Assert.Throws<ArgumentException>(() => storage.BuildPublicUrl("other/source.txt"));
        Assert.Throws<ArgumentException>(() => storage.BuildPublicUrl("wechatrobot/../secret.txt"));
    }

    [Fact]
    public async Task Put_contract_forwards_bucket_key_content_type_and_bytes_without_live_credentials()
    {
        var transport = new RecordingOssTransport();
        var options = new OssOptions
        {
            Bucket = "newsplatform", Endpoint = "oss-cn-shenzhen", PublicReadRiskAccepted = true,
            AccessKeyId = "test-only", AccessKeySecret = "test-only"
        };
        var storage = new AliyunOssStorage(Options.Create(options), transport);
        await using var content = new MemoryStream("contract"u8.ToArray());

        var stored = await storage.PutAsync("wechatrobot/knowledge/id/1/source/source.txt", content, "text/plain", TestContext.Current.CancellationToken);

        Assert.Equal("newsplatform", transport.Bucket);
        Assert.Equal("wechatrobot/knowledge/id/1/source/source.txt", transport.Key);
        Assert.Equal("text/plain", transport.ContentType);
        Assert.Equal("contract", System.Text.Encoding.UTF8.GetString(transport.Content));
        Assert.Equal("https://newsplatform.oss-cn-shenzhen.aliyuncs.com/wechatrobot/knowledge/id/1/source/source.txt", stored.PublicUrl.AbsoluteUri);
    }

    private static AliyunOssStorage Create(OssOptions options) => new(Options.Create(options));

    private sealed class RecordingOssTransport : IOssTransport
    {
        public bool IsConfigured => true;
        public string Bucket { get; private set; } = string.Empty;
        public string Key { get; private set; } = string.Empty;
        public string ContentType { get; private set; } = string.Empty;
        public byte[] Content { get; private set; } = [];
        public void Put(string bucket, string key, Stream content, string contentType)
        {
            Bucket = bucket; Key = key; ContentType = contentType;
            using var buffer = new MemoryStream(); content.CopyTo(buffer); Content = buffer.ToArray();
        }
        public void Delete(string bucket, string key) { }
    }
}
