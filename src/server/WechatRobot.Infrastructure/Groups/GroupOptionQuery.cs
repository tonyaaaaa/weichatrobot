using Microsoft.EntityFrameworkCore;
using WechatRobot.Infrastructure.Persistence;

namespace WechatRobot.Infrastructure.Groups;

public sealed record GroupOption(
    Guid Id,
    string Name,
    string? WorkToolGroupRemark,
    string RobotName,
    string State,
    bool IsEnabled);

public sealed class GroupOptionQuery(WechatRobotDbContext database)
{
    public Task<GroupOption[]> ListAsync(CancellationToken token) =>
        (
            from profile in database.GroupProfiles.AsNoTracking()
            join robot in database.RobotConfigs.AsNoTracking()
                on profile.RobotConfigId equals robot.Id
            orderby profile.Name, profile.Id
            select new GroupOption(
                profile.Id,
                profile.Name,
                profile.WorkToolGroupRemark,
                robot.Name,
                profile.ArchivedAtUtc != null
                    ? "archived"
                    : profile.IsEnabled ? "enabled" : "disabled",
                profile.IsEnabled)
        ).ToArrayAsync(token);
}
