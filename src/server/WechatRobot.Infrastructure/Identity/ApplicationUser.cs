using Microsoft.AspNetCore.Identity;

namespace WechatRobot.Infrastructure.Identity;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public string? WorkToolDisplayName { get; set; }
    public DateTime? WorkToolDisplayNameUpdatedAtUtc { get; set; }
}
