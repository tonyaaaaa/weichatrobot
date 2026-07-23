using WechatRobot.Application.Knowledge.Ocr;
using WechatRobot.Infrastructure.Knowledge.Ocr;

namespace WechatRobot.ContractTests.Knowledge;

public sealed class OcrClientContractTests
{
    [Fact]
    public void Parses_ordered_words_and_percentage_confidence()
    {
        const string data = """{"content":"第一行\n第二行","prism_wordsInfo":[{"word":"第一行","prob":98},{"word":"第二行","prob":75}]}""";

        var blocks = AliyunOcrResponseParser.Parse(data);

        Assert.Collection(blocks,
            block => { Assert.Equal(0, block.Order); Assert.Equal("第一行", block.Text); Assert.Equal(.98, block.Confidence); },
            block => { Assert.Equal(1, block.Order); Assert.Equal("第二行", block.Text); Assert.Equal(.75, block.Confidence); });
    }

    [Theory]
    [InlineData("""{"content":"整页正文"}""", "整页正文", 1)]
    [InlineData("""{"content":"","prism_wordsInfo":[]}""", null, 0)]
    [InlineData("""{"content":"正文","prism_wordsInfo":null}""", "正文", 1)]
    public void Handles_content_fallback_and_missing_word_data(string data, string? expected, int count)
    {
        var blocks = AliyunOcrResponseParser.Parse(data);
        Assert.Equal(count, blocks.Count);
        if (expected is not null) Assert.Equal(expected, Assert.Single(blocks).Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{not-json")]
    [InlineData("null")]
    public void Rejects_empty_or_malformed_data_safely(string data)
    {
        var exception = Assert.Throws<OcrClientException>(() => AliyunOcrResponseParser.Parse(data));
        Assert.Equal(OcrClientError.InvalidResponse, exception.Error);
        if (data.Length > 0) Assert.DoesNotContain(data, exception.Message, StringComparison.Ordinal);
    }
}
