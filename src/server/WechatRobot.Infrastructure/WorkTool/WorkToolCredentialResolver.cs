using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Security;
using WechatRobot.Application.WorkTool;
using WechatRobot.Infrastructure.Persistence;

namespace WechatRobot.Infrastructure.WorkTool;

public sealed class WorkToolCredentialResolver(
    WechatRobotDbContext database,
    ISecretProtector protector) : IWorkToolCredentialResolver
{
    public Task<string> ResolveRobotIdAsync(
        Guid robotConfigId,
        CancellationToken cancellationToken) =>
        ResolveEnabledRobotIdAsync(robotConfigId, cancellationToken);

    public async Task<string> ResolveEnabledRobotIdAsync(
        Guid robotConfigId,
        CancellationToken cancellationToken)
    {
        var encrypted = await database.RobotConfigs.AsNoTracking()
            .Where(robot => robot.Id == robotConfigId && robot.IsEnabled)
            .Select(robot => robot.EncryptedWorkToolRobotId)
            .SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(encrypted))
            throw new WorkToolCredentialUnavailableException(
                "Enabled WorkTool robot credential is unavailable.");
        return protector.Unprotect(encrypted);
    }

    public async Task<string> ResolveConfiguredRobotIdAsync(
        Guid robotConfigId,
        CancellationToken cancellationToken)
    {
        var encrypted = await database.RobotConfigs.AsNoTracking()
            .Where(robot => robot.Id == robotConfigId)
            .Select(robot => robot.EncryptedWorkToolRobotId)
            .SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(encrypted))
            throw new WorkToolCredentialUnavailableException(
                "WorkTool robot credential is unavailable.");
        return protector.Unprotect(encrypted);
    }
}
