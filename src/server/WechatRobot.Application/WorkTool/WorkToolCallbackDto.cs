using System.Text.Json.Serialization;

namespace WechatRobot.Application.WorkTool;

public sealed class WorkToolCallbackDto
{
    public const int MaxIdentifierLength = 128;
    public const int MaxTextLength = 8192;
    [JsonPropertyName("spoken")]
    public string? Spoken { get; init; }

    [JsonPropertyName("rawSpoken")]
    public string? RawSpoken { get; init; }

    [JsonPropertyName("receivedName")]
    public string? ReceivedName { get; init; }

    // Connector extension only. The official public WorkTool callback does not supply a stable sender identifier.
    [JsonPropertyName("connectorStableSenderId")]
    public string? ConnectorStableSenderId { get; init; }

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

        if (!IsWithinLength(MessageId, MaxIdentifierLength)
            || !IsWithinLength(ReceivedName, MaxIdentifierLength)
            || !IsWithinLength(ConnectorStableSenderId, MaxIdentifierLength)
            || !IsWithinLength(GroupName, MaxIdentifierLength)
            || !IsWithinLength(GroupRemark, MaxIdentifierLength)
            || !IsWithinLength(Spoken, MaxTextLength)
            || !IsWithinLength(RawSpoken, MaxTextLength))
        {
            reason = "callback-field-too-large";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public static bool IsIdentifierWithinLimit(string? value) => IsWithinLength(value, MaxIdentifierLength);

    private static bool IsWithinLength(string? value, int maximumLength) => value is null || value.Length <= maximumLength;
}
