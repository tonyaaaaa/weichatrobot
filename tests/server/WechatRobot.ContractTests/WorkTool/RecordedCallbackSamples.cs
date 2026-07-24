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
        Assert.True(sample.IsSupportedGroupText(out var reason), reason);
        Assert.Equal("recorded-message-001", sample.MessageId);
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

        Assert.False(sample.IsSupportedGroupText(out var reason));
        Assert.Equal("callback-field-too-large", reason);
    }
}
