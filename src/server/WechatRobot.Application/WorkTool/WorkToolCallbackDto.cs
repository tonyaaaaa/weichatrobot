using System.Text.Json.Serialization;

namespace WechatRobot.Application.WorkTool;

public sealed class WorkToolCallbackDto
{
    public const int MaxIdentifierLength = 128;
    public const int MaxTextLength = 8192;
    public const int MaxFileBase64Length = 8 * 1024 * 1024;

    private static readonly HashSet<int?> OfficialTextTypes =
        [0, 1, 2, 3, 5, 7, 8, 9, 13, 15];

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
    [JsonConverter(typeof(FlexibleNullableBooleanJsonConverter))]
    public bool? AtMe { get; init; }

    [JsonPropertyName("textType")]
    public int? TextType { get; init; }

    [JsonPropertyName("messageId")]
    public string? MessageId { get; init; }

    [JsonPropertyName("fileBase64")]
    public string? FileBase64 { get; init; }

    public WorkToolCallbackClassification Classify()
    {
        if (!IsWithinLength(MessageId, MaxIdentifierLength)
            || !IsWithinLength(ReceivedName, MaxIdentifierLength)
            || !IsWithinLength(ConnectorStableSenderId, MaxIdentifierLength)
            || !IsWithinLength(GroupName, MaxIdentifierLength)
            || !IsWithinLength(GroupRemark, MaxIdentifierLength)
            || !IsWithinLength(Spoken, MaxTextLength)
            || !IsWithinLength(RawSpoken, MaxTextLength)
            || !IsWithinLength(FileBase64, MaxFileBase64Length))
            return new(WorkToolCallbackDisposition.Reject, "callback-field-too-large");

        if (RoomType is < 1 or > 4)
            return new(WorkToolCallbackDisposition.Reject, "unknown-room-type");

        if (!OfficialTextTypes.Contains(TextType))
            return new(WorkToolCallbackDisposition.Reject, "unknown-text-type");

        if (TextType != 1 || RoomType == 3)
            return new(WorkToolCallbackDisposition.Ignore, "unsupported-message-kind");

        if (string.IsNullOrWhiteSpace(ReceivedName)
            || string.IsNullOrWhiteSpace(Spoken))
            return new(WorkToolCallbackDisposition.Reject, "missing-required-text-field");
        if (RoomType == 1 && string.IsNullOrWhiteSpace(GroupName))
            return new(WorkToolCallbackDisposition.Reject, "missing-required-group-text-field");

        return new(WorkToolCallbackDisposition.Process, string.Empty);
    }

    public static bool IsIdentifierWithinLimit(string? value) => IsWithinLength(value, MaxIdentifierLength);

    private static bool IsWithinLength(string? value, int maximumLength) => value is null || value.Length <= maximumLength;
}

public enum WorkToolCallbackDisposition
{
    Process,
    Ignore,
    Reject
}

public sealed record WorkToolCallbackClassification(
    WorkToolCallbackDisposition Disposition,
    string Reason);
