namespace WechatRobot.Application.WorkTool;

public sealed record WorkToolRawCommandResult(
    string? RawMessage,
    int RawSuccess,
    string? ErrorReason,
    string? RunTimeRaw,
    int ApiSend,
    int Type,
    string MessageId,
    string? SuccessListRaw,
    string? FailListRaw,
    decimal? TimeCost);
