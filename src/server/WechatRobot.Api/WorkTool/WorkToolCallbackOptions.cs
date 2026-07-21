using System.ComponentModel.DataAnnotations;

namespace WechatRobot.Api.WorkTool;

public sealed class WorkToolCallbackOptions
{
    public const string SectionName = "WorkToolCallback";

    [Range(10, 2900)]
    public int IngestionDeadlineMilliseconds { get; init; } = 2500;

    [Range(1, 3600)]
    public int FallbackDeduplicationWindowSeconds { get; init; } = 300;

    public TimeSpan IngestionDeadline => TimeSpan.FromMilliseconds(IngestionDeadlineMilliseconds);
    public TimeSpan FallbackDeduplicationWindow => TimeSpan.FromSeconds(FallbackDeduplicationWindowSeconds);
}
