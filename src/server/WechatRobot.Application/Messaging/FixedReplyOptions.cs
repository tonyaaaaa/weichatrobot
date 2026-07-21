using System.ComponentModel.DataAnnotations;

namespace WechatRobot.Application.Messaging;

public sealed class FixedReplyOptions
{
    public const string SectionName = "FixedReply";

    [Required]
    [StringLength(2000, MinimumLength = 1)]
    public string Text { get; set; } = "已收到，正在为您处理。";

    [Range(1, 60)]
    public int SendRateLimitPerMinute { get; set; } = 50;
}
