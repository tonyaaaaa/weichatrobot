using System.Text.Json.Serialization;

namespace WechatRobot.Application.WorkTool;

public sealed class WorkToolCallbackDto
{
    [JsonPropertyName("spoken")]
    public string? Spoken { get; init; }

    [JsonPropertyName("rawSpoken")]
    public string? RawSpoken { get; init; }

    [JsonPropertyName("receivedName")]
    public string? ReceivedName { get; init; }

    [JsonPropertyName("groupName")]
    public string? GroupName { get; init; }

    [JsonPropertyName("groupRemark")]
    public string? GroupRemark { get; init; }

    [JsonPropertyName("roomType")]
    public int? RoomType { get; init; }

    [JsonPropertyName("atMe")]
    public bool? AtMe { get; init; }

    [JsonPropertyName("textType")]
    public int? TextType { get; init; }

    [JsonPropertyName("messageId")]
    public string? MessageId { get; init; }

    public bool IsSupportedGroupText(out string reason)
    {
        if (RoomType != 1)
        {
            reason = "unsupported-room-type";
            return false;
        }

        if (TextType != 1)
        {
            reason = "unsupported-text-type";
            return false;
        }

        if (string.IsNullOrWhiteSpace(GroupName) || string.IsNullOrWhiteSpace(ReceivedName) || string.IsNullOrWhiteSpace(Spoken))
        {
            reason = "missing-required-group-or-text-field";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}
