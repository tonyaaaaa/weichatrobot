using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Security;
using WechatRobot.Application.WorkTool;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.Infrastructure.WorkTool;

public sealed class RobotCallbackConfigurationService(
    WechatRobotDbContext database,
    ISecretProtector protector,
    IWorkToolClient workTool,
    TimeProvider timeProvider)
{
    private static readonly TimeSpan PreviousSecretGrace = TimeSpan.FromMinutes(10);

    public async Task<RobotCallbackConfigurationOutcome> ConfigureMessageCallbackAsync(
        Guid robotConfigId,
        Uri publicBaseUri,
        bool replyAll,
        string actor,
        CancellationToken cancellationToken)
    {
        var credential = await GetOrCreateCredentialAsync(robotConfigId, actor, cancellationToken);
        var callbackUrl = BuildCallbackUri(
            publicBaseUri,
            $"/api/worktool/callback/{credential.RouteCode}",
            credential.Plaintext);

        try
        {
            var result = await workTool.ConfigureMessageCallbackAsync(
                robotConfigId,
                new WorkToolMessageCallbackRequest(true, replyAll, callbackUrl),
                cancellationToken);
            if (!result.Configured)
            {
                await RestoreIfRotatedAsync(credential, actor, result.FailureCode, cancellationToken);
                return new(false, result.FailureCode);
            }

            await RecordAuditAsync(
                actor,
                "worktool.message-callback.configured",
                robotConfigId,
                new { replyAll },
                cancellationToken);
            return new(true, null);
        }
        catch
        {
            await RestoreIfRotatedAsync(
                credential,
                actor,
                "worktool_transport_failure",
                CancellationToken.None);
            throw;
        }
    }

    public async Task<RobotCallbackConfigurationOutcome> ConfigureCommandResultCallbackAsync(
        Guid robotConfigId,
        Uri publicBaseUri,
        string actor,
        CancellationToken cancellationToken)
    {
        var credential = await GetOrCreateCredentialAsync(robotConfigId, actor, cancellationToken);
        var callbackUrl = BuildCallbackUri(
            publicBaseUri,
            $"/api/worktool/command-results/{credential.RouteCode}",
            credential.Plaintext);

        try
        {
            var result = await workTool.BindEventCallbackAsync(
                robotConfigId,
                1,
                callbackUrl,
                cancellationToken);
            if (!result.Succeeded)
            {
                await RestoreIfRotatedAsync(credential, actor, result.FailureCode, cancellationToken);
                return new(false, result.FailureCode);
            }

            await RecordAuditAsync(
                actor,
                "worktool.command-result-callback.configured",
                robotConfigId,
                new { type = 1 },
                cancellationToken);
            return new(true, null);
        }
        catch
        {
            await RestoreIfRotatedAsync(
                credential,
                actor,
                "worktool_transport_failure",
                CancellationToken.None);
            throw;
        }
    }

    public async Task<RobotCallbackStatus> GetStatusAsync(
        Guid robotConfigId,
        CancellationToken cancellationToken)
    {
        var robot = await workTool.GetRobotAsync(robotConfigId, cancellationToken);
        var eventCallbacks = await workTool.ListEventCallbacksAsync(robotConfigId, cancellationToken);
        return new(
            robot.Reachable && robot.MessageCallbackEnabled,
            eventCallbacks.Any(callback => callback.Type == 1),
            robot.ReplyAllEnabled,
            timeProvider.GetUtcNow().UtcDateTime);
    }

    public async Task<RobotProbe> ProbeAsync(
        Guid robotConfigId,
        CancellationToken cancellationToken)
    {
        var robot = await workTool.GetRobotAsync(robotConfigId, cancellationToken);
        var online = robot.Reachable
            ? await workTool.GetOnlineAsync(robotConfigId, cancellationToken)
            : new WorkToolOnlineSnapshot(null, robot.FailureCode);
        return new(
            robot.Reachable,
            online.Online,
            robot.MessageCallbackEnabled,
            robot.ReplyAllEnabled,
            robot.FailureCode ?? online.FailureCode);
    }

    public async Task<RobotCallbackConfigurationOutcome> DeleteEventCallbackAsync(
        Guid robotConfigId,
        int type,
        string actor,
        CancellationToken cancellationToken)
    {
        if (type != 1)
        {
            return new(false, "unsupported_callback_type");
        }

        var result = await workTool.DeleteEventCallbackAsync(
            robotConfigId,
            type,
            cancellationToken);
        if (result.Succeeded)
        {
            await RecordAuditAsync(
                actor,
                "worktool.command-result-callback.deleted",
                robotConfigId,
                new { type },
                cancellationToken);
        }

        return new(result.Succeeded, result.FailureCode);
    }

    private async Task<CallbackCredentialLease> GetOrCreateCredentialAsync(
        Guid robotConfigId,
        string actor,
        CancellationToken cancellationToken)
    {
        var robot = await database.RobotConfigs.SingleOrDefaultAsync(
            item => item.Id == robotConfigId && item.IsEnabled,
            cancellationToken) ?? throw new RobotCallbackConfigurationNotFoundException();
        if (string.IsNullOrWhiteSpace(robot.CallbackRouteCode))
        {
            throw new RobotCallbackConfigurationNotFoundException();
        }

        if (!string.IsNullOrWhiteSpace(robot.EncryptedCallbackSecret))
        {
            return new(
                robot.Id,
                robot.CallbackRouteCode,
                protector.Unprotect(robot.EncryptedCallbackSecret),
                false,
                default);
        }

        var previous = new CallbackCredentialSnapshot(
            robot.EncryptedCallbackSecret,
            robot.CallbackSecretHash,
            robot.PreviousCallbackSecretHash,
            robot.PreviousCallbackSecretExpiresAtUtc);
        var plaintext = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var now = timeProvider.GetUtcNow().UtcDateTime;
        robot.PreviousCallbackSecretHash = string.IsNullOrWhiteSpace(robot.CallbackSecretHash)
            ? null
            : robot.CallbackSecretHash;
        robot.PreviousCallbackSecretExpiresAtUtc = robot.PreviousCallbackSecretHash is null
            ? null
            : now.Add(PreviousSecretGrace);
        robot.EncryptedCallbackSecret = protector.Protect(plaintext);
        robot.CallbackSecretHash = Hash(plaintext);
        robot.UpdatedAtUtc = now;
        database.AdministrationAudits.Add(NewAudit(
            actor,
            "worktool.callback-credential.rotation-started",
            robot.Id,
            new { graceMinutes = (int)PreviousSecretGrace.TotalMinutes }));
        await database.SaveChangesAsync(cancellationToken);

        return new(robot.Id, robot.CallbackRouteCode, plaintext, true, previous);
    }

    private async Task RestoreIfRotatedAsync(
        CallbackCredentialLease credential,
        string actor,
        string? failureCode,
        CancellationToken cancellationToken)
    {
        if (!credential.WasRotated)
        {
            return;
        }

        database.ChangeTracker.Clear();
        var robot = await database.RobotConfigs.SingleAsync(
            item => item.Id == credential.RobotConfigId,
            cancellationToken);
        var previous = credential.Previous!;
        robot.EncryptedCallbackSecret = previous.EncryptedCallbackSecret;
        robot.CallbackSecretHash = previous.CallbackSecretHash;
        robot.PreviousCallbackSecretHash = previous.PreviousCallbackSecretHash;
        robot.PreviousCallbackSecretExpiresAtUtc =
            previous.PreviousCallbackSecretExpiresAtUtc;
        robot.UpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        database.AdministrationAudits.Add(NewAudit(
            actor,
            "worktool.callback-credential.rotation-restored",
            robot.Id,
            new { failureCode = SafeFailureCode(failureCode) }));
        await database.SaveChangesAsync(cancellationToken);
    }

    private async Task RecordAuditAsync(
        string actor,
        string action,
        Guid robotConfigId,
        object detail,
        CancellationToken cancellationToken)
    {
        database.AdministrationAudits.Add(NewAudit(actor, action, robotConfigId, detail));
        await database.SaveChangesAsync(cancellationToken);
    }

    private static AdministrationAuditEntity NewAudit(
        string actor,
        string action,
        Guid robotConfigId,
        object detail) =>
        new()
        {
            Actor = actor,
            Action = action,
            TargetType = "RobotConfig",
            TargetId = robotConfigId.ToString("D"),
            SanitizedDetailJson = JsonSerializer.Serialize(detail)
        };

    private static Uri BuildCallbackUri(Uri publicBaseUri, string path, string secret)
    {
        var callback = new Uri(publicBaseUri, path);
        return new Uri($"{callback.AbsoluteUri}?token={Uri.EscapeDataString(secret)}");
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string SafeFailureCode(string? failureCode) =>
        string.IsNullOrWhiteSpace(failureCode) ? "worktool_failure" : failureCode;

    private sealed record CallbackCredentialLease(
        Guid RobotConfigId,
        string RouteCode,
        string Plaintext,
        bool WasRotated,
        CallbackCredentialSnapshot? Previous);

    private sealed record CallbackCredentialSnapshot(
        string? EncryptedCallbackSecret,
        string CallbackSecretHash,
        string? PreviousCallbackSecretHash,
        DateTime? PreviousCallbackSecretExpiresAtUtc);
}

public sealed record RobotCallbackConfigurationOutcome(
    bool Succeeded,
    string? FailureCode);

public sealed record RobotCallbackStatus(
    bool MessageCallbackConfigured,
    bool CommandResultCallbackConfigured,
    bool ReplyAll,
    DateTime CheckedAtUtc);

public sealed record RobotProbe(
    bool Reachable,
    bool? Online,
    bool MessageCallbackEnabled,
    bool ReplyAllEnabled,
    string? FailureCode);

public sealed class RobotCallbackConfigurationNotFoundException : Exception;
