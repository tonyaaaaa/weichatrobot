using WechatRobot.Infrastructure.Knowledge.Ocr;

namespace WechatRobot.IntegrationTests.Knowledge;

public sealed class AliyunOcrAcceptanceTests
{
    public static bool IsEnabled =>
        Environment.GetEnvironmentVariable(AliyunOcrOptions.RealTestEnvironmentVariable) == "1" &&
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AliyunOcrOptions.AccessKeyIdEnvironmentVariable)) &&
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AliyunOcrOptions.AccessKeySecretEnvironmentVariable));

    [Fact(Skip = "Requires explicit Alibaba Cloud OCR opt-in and credentials.", SkipUnless = nameof(IsEnabled))]
    public async Task RecognizeGeneral_accepts_one_small_binary_fixture()
    {
        var options = new AliyunOcrOptions();
        var provider = new AlibabaSdkOcrProvider(
            options,
            Environment.GetEnvironmentVariable(AliyunOcrOptions.AccessKeyIdEnvironmentVariable)!,
            Environment.GetEnvironmentVariable(AliyunOcrOptions.AccessKeySecretEnvironmentVariable)!);
        await using var image = new MemoryStream(CreateFixtureBmp(), writable: false);

        var response = await provider.RecognizeGeneralAsync(image, 1, TestContext.Current.CancellationToken);

        Assert.False(string.IsNullOrWhiteSpace(response.Data));
        Assert.False(string.IsNullOrWhiteSpace(response.RequestId));
        Assert.NotEmpty(AliyunOcrResponseParser.Parse(response.Data));
    }

    private static byte[] CreateFixtureBmp()
    {
        const int width = 320, height = 96, header = 54, stride = width * 3;
        var bytes = new byte[header + stride * height];
        bytes.AsSpan(header).Fill(255);
        "BM"u8.CopyTo(bytes);
        BitConverter.GetBytes(bytes.Length).CopyTo(bytes, 2);
        BitConverter.GetBytes(header).CopyTo(bytes, 10);
        BitConverter.GetBytes(40).CopyTo(bytes, 14);
        BitConverter.GetBytes(width).CopyTo(bytes, 18);
        BitConverter.GetBytes(height).CopyTo(bytes, 22);
        BitConverter.GetBytes((short)1).CopyTo(bytes, 26);
        BitConverter.GetBytes((short)24).CopyTo(bytes, 28);
        BitConverter.GetBytes(stride * height).CopyTo(bytes, 34);
        var glyphs = new Dictionary<char, string[]>
        {
            ['O'] = ["01110","10001","10001","10001","10001","10001","01110"],
            ['C'] = ["01111","10000","10000","10000","10000","10000","01111"],
            ['R'] = ["11110","10001","10001","11110","10100","10010","10001"],
            ['T'] = ["11111","00100","00100","00100","00100","00100","00100"],
            ['E'] = ["11111","10000","10000","11110","10000","10000","11111"],
            ['S'] = ["01111","10000","10000","01110","00001","00001","11110"]
        };
        const int scale = 8;
        var x = 16;
        foreach (var character in "OCR TEST")
        {
            if (character == ' ') { x += scale * 3; continue; }
            foreach (var (row, y) in glyphs[character].Select((row, y) => (row, y)))
            foreach (var (pixel, column) in row.Select((pixel, column) => (pixel, column)))
            if (pixel == '1')
                for (var dy = 0; dy < scale; dy++)
                for (var dx = 0; dx < scale; dx++)
                {
                    var offset = header + (height - 1 - (20 + y * scale + dy)) * stride + (x + column * scale + dx) * 3;
                    bytes[offset] = bytes[offset + 1] = bytes[offset + 2] = 0;
                }
            x += scale * 6;
        }
        return bytes;
    }
}
