using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Security;
using WechatRobot.Application.WorkTool;
using WechatRobot.Infrastructure.Persistence;

namespace WechatRobot.Infrastructure.WorkTool;

public sealed class WorkToolCredentialResolver(
    WechatRobotDbContext database,
    ISecretProtector protector) : IWorkToolCredentialResolver
{
    public async Task<string> ResolveRobotIdAsync(Guid robotConfigId, CancellationToken cancellationToken)
    {
        var encrypted = await database.RobotConfigs.AsNoTracking()
            .Where(robot => robot.Id == robotConfigId && robot.IsEnabled)
            .Select(robot => robot.EncryptedWorkToolRobotId)
            .SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(encrypted))
            throw new InvalidOperationException("Enabled WorkTool robot credential is unavailable.");
        return protector.Unprotect(encrypted);
    }
}
