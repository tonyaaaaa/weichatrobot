using WechatRobot.Application.PrivateChat;

namespace WechatRobot.UnitTests.PrivateChat;

public sealed class PrivateChatCommandParserTests
{
    [Fact]
    public void Internal_private_chat_with_exact_first_line_creates_ingest_command()
    {
        var result = PrivateChatCommandParser.Parse(
            4,
            "#知识入库\r\n问题：签证多久下来？\r\n答案：以顾问通知为准。");

        Assert.Equal(PrivateChatMessageKind.DirectKnowledgeIngest, result.Kind);
        Assert.StartsWith("问题：", result.Body);
    }

    [Fact]
    public void Internal_private_chat_accepts_worktool_flattened_ingest_command()
    {
        var result = PrivateChatCommandParser.Parse(
            4,
            "#知识入库 问题：测试编号是什么？ 答案：KB-20260730。");

        Assert.Equal(PrivateChatMessageKind.DirectKnowledgeIngest, result.Kind);
        Assert.Equal("问题：测试编号是什么？ 答案：KB-20260730。", result.Body);
    }

    [Fact]
    public void External_private_chat_cannot_ingest()
    {
        var result = PrivateChatCommandParser.Parse(2, "#知识入库\n内容");

        Assert.Equal(PrivateChatMessageKind.UnsupportedIngest, result.Kind);
    }

    [Theory]
    [InlineData("普通问题中提到 #知识入库 怎么办")]
    [InlineData("前缀#知识入库\n正文")]
    [InlineData("#知识入库扩展\n正文")]
    [InlineData("#知识入库")]
    public void Non_exact_or_empty_marker_is_a_question(string text)
    {
        Assert.Equal(
            PrivateChatMessageKind.Question,
            PrivateChatCommandParser.Parse(4, text).Kind);
    }
}
