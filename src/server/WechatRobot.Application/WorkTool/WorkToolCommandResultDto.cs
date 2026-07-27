using System.Text.Json.Serialization;

namespace WechatRobot.Application.WorkTool;

public sealed class WorkToolCommandResultDto
{
    private static readonly HashSet<int> SupportedCommandTypes = [203, 206, 207];

    public const int MaximumMessageIdLength = 128;
    public const int MaximumListEntries = 100;
    public const int MaximumDisplayNameLength = 128;

    [JsonPropertyName("messageId")]
    public string? MessageId { get; init; }

    [JsonPropertyName("errorCode")]
    public int? ErrorCode { get; init; }

    [JsonPropertyName("errorReason")]
    public string? ErrorReason { get; init; }

    [JsonPropertyName("runTime")]
    public long? RunTime { get; init; }

    [JsonPropertyName("timeCost")]
    public double? TimeCost { get; init; }

    [JsonPropertyName("type")]
    public int? Type { get; init; }

    [JsonPropertyName("successList")]
    public IReadOnlyList<string>? SuccessList { get; init; }

    [JsonPropertyName("failList")]
    public IReadOnlyList<string>? FailList { get; init; }

    public bool IsValid(out string reason)
    {
        if (string.IsNullOrWhiteSpace(MessageId))
        {
            reason = "missing-message-id";
            return false;
        }

        if (MessageId.Length > MaximumMessageIdLength)
        {
            reason = "message-id-too-large";
            return false;
        }

        if (ErrorCode is null)
        {
            reason = "missing-result-code";
            return false;
        }

        if (Type is null || !SupportedCommandTypes.Contains(Type.Value))
        {
            reason = "unsupported-result-type";
            return false;
        }

        if ((SuccessList?.Count ?? 0) + (FailList?.Count ?? 0) > MaximumListEntries)
        {
            reason = "result-list-too-large";
            return false;
        }

        if (HasInvalidName(SuccessList) || HasInvalidName(FailList))
        {
            reason = "result-name-too-large";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool HasInvalidName(IReadOnlyList<string>? names) =>
        names?.Any(name => name is null || name.Length > MaximumDisplayNameLength) == true;
}
