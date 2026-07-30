using System.Text.Json;
using WechatRobot.Application.WorkTool;

namespace WechatRobot.ContractTests.WorkTool;

public static class RecordedCallbackSamples
{
    // Sanitized from the approved no-at external-group callback shape. No real robot, group, or member identifiers remain.
    public const string NoAtGroupTextJson =
        """
        {
          "spoken": "How do I reset my password?",
          "rawSpoken": "How do I reset my password?",
          "receivedName": "Recorded User",
          "groupName": "Recorded Support Group",
          "groupRemark": "Recorded Support Group",
          "roomType": 1,
          "atMe": false,
          "textType": 1,
          "messageId": "recorded-message-001"
        }
        """;

    public const string OfficialStringAtMeJson =
        """
        {
          "spoken": "你好",
          "rawSpoken": "@管家 你好",
          "receivedName": "仑哥",
          "groupName": "测试群1",
          "groupRemark": "测试群1备注名",
          "roomType": 1,
          "atMe": "true",
          "textType": 1
        }
        """;

    public static WorkToolCallbackDto NoAtGroupText() =>
        JsonSerializer.Deserialize<WorkToolCallbackDto>(NoAtGroupTextJson)
        ?? throw new InvalidOperationException("The recorded callback sample must deserialize.");
}

public sealed class RecordedCallbackSampleTests
{
    [Fact]
    public void Sanitized_no_at_sample_preserves_the_strict_supported_group_text_contract()
    {
        var sample = RecordedCallbackSamples.NoAtGroupText();

        Assert.False(sample.AtMe);
        Assert.Equal(WorkToolCallbackDisposition.Process, sample.Classify().Disposition);
        Assert.Equal("recorded-message-001", sample.MessageId);
        Assert.Equal("Recorded Support Group", sample.GroupRemark);
    }

    [Fact]
    public void Recorded_shape_does_not_weaken_callback_size_validation()
    {
        var sample = new WorkToolCallbackDto
        {
            Spoken = new string('x', WorkToolCallbackDto.MaxTextLength + 1),
            ReceivedName = "Recorded User",
            GroupName = "Recorded Support Group",
            RoomType = 1,
            TextType = 1,
            AtMe = false
        };

        var classification = sample.Classify();
        Assert.Equal(WorkToolCallbackDisposition.Reject, classification.Disposition);
        Assert.Equal("callback-field-too-large", classification.Reason);
    }

    [Fact]
    public void Official_string_atMe_sample_deserializes_as_a_supported_group_text()
    {
        var sample = JsonSerializer.Deserialize<WorkToolCallbackDto>(
            RecordedCallbackSamples.OfficialStringAtMeJson);

        Assert.NotNull(sample);
        Assert.True(sample.AtMe);
        Assert.Equal(WorkToolCallbackDisposition.Process, sample.Classify().Disposition);
    }

    [Theory]
    [InlineData(3, 1)]
    [InlineData(1, 2)]
    [InlineData(1, 3)]
    [InlineData(1, 9)]
    public void Official_but_unsupported_message_kinds_are_ignored(int roomType, int textType)
    {
        var sample = new WorkToolCallbackDto
        {
            Spoken = "内容",
            ReceivedName = "成员甲",
            GroupName = "测试群",
            RoomType = roomType,
            TextType = textType
        };

        var classification = sample.Classify();

        Assert.Equal(WorkToolCallbackDisposition.Ignore, classification.Disposition);
        Assert.Equal("unsupported-message-kind", classification.Reason);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void Official_private_text_message_kinds_are_processed(int roomType)
    {
        var sample = new WorkToolCallbackDto
        {
            Spoken = "私聊问题",
            ReceivedName = "成员甲",
            RoomType = roomType,
            TextType = 1
        };

        var classification = sample.Classify();

        Assert.Equal(WorkToolCallbackDisposition.Process, classification.Disposition);
        Assert.Equal(string.Empty, classification.Reason);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(5, 1)]
    [InlineData(1, 4)]
    [InlineData(1, 99)]
    public void Unknown_message_kinds_are_rejected(int roomType, int textType)
    {
        var sample = new WorkToolCallbackDto
        {
            Spoken = "内容",
            ReceivedName = "成员甲",
            GroupName = "测试群",
            RoomType = roomType,
            TextType = textType
        };

        Assert.Equal(WorkToolCallbackDisposition.Reject, sample.Classify().Disposition);
    }

    [Fact]
    public void Unknown_atMe_string_is_rejected()
    {
        const string json =
            """{"spoken":"你好","receivedName":"仑哥","groupName":"测试群1","roomType":1,"atMe":"yes","textType":1}""";

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<WorkToolCallbackDto>(json));
    }
}
