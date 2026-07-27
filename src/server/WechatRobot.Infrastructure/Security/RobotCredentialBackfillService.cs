using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Security;
using WechatRobot.Infrastructure.Persistence;

namespace WechatRobot.Infrastructure.Security;

public sealed class RobotCredentialBackfillService(
    WechatRobotDbContext database,
    ISecretProtector protector)
{
    public async Task BackfillAsync(CancellationToken cancellationToken = default)
    {
        await using var transaction = database.Database.IsRelational()
            ? await database.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var robots = await database.RobotConfigs
            .Where(robot => robot.EncryptedWorkToolRobotId == null || robot.CallbackRouteCode == null)
            .ToArrayAsync(cancellationToken);
        var callbackRotationCandidates = await database.RobotConfigs
            .Where(robot => robot.EncryptedCallbackSecret == null)
            .Select(robot => robot.Id)
            .ToArrayAsync(cancellationToken);
        var alreadyFlagged = (await database.AdministrationAudits.AsNoTracking()
                .Where(audit => audit.Action == "worktool.callback-credential.rotation-required")
                .Select(audit => audit.TargetId)
                .ToArrayAsync(cancellationToken))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var robot in robots)
        {
#pragma warning disable CS0618
            if (string.IsNullOrWhiteSpace(robot.EncryptedWorkToolRobotId))
            {
                if (string.IsNullOrWhiteSpace(robot.WorkToolRobotId))
                    throw new InvalidOperationException("Robot credential backfill cannot continue without a legacy robot ID.");
                robot.EncryptedWorkToolRobotId = protector.Protect(robot.WorkToolRobotId);
            }
#pragma warning restore CS0618
            robot.CallbackRouteCode ??= Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        }
        foreach (var robotId in callbackRotationCandidates)
        {
            if (alreadyFlagged.Add(robotId.ToString("D")))
            {
                database.AdministrationAudits.Add(new()
                {
                    Actor = "system",
                    Action = "worktool.callback-credential.rotation-required",
                    TargetType = "RobotConfig",
                    TargetId = robotId.ToString("D"),
                    SanitizedDetailJson = "{}"
                });
            }
        }
        await database.SaveChangesAsync(cancellationToken);
        foreach (var robot in robots)
        {
            _ = protector.Unprotect(robot.EncryptedWorkToolRobotId!);
#pragma warning disable CS0618
            robot.WorkToolRobotId = $"migrated-{robot.Id:N}";
#pragma warning restore CS0618
        }
        await database.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
    }
}
