using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.WorkTool;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.Infrastructure.WorkTool;

public sealed class EfWorkToolCommandResultStore(WechatRobotDbContext database) : IWorkToolCommandResultStore
{
    private static readonly HashSet<string> TerminalStatuses = new(StringComparer.Ordinal)
    {
        WorkToolCommandStatuses.ExecutedSucceeded,
        WorkToolCommandStatuses.ExecutedPartially,
        WorkToolCommandStatuses.ExecutedFailed,
        WorkToolCommandStatuses.Rejected,
        WorkToolCommandStatuses.ResultTimeout,
        WorkToolCommandStatuses.DeadLetter
    };

    public async Task<WorkToolResultTarget?> FindAsync(
        Guid robotConfigId,
        string workToolMessageId,
        CancellationToken token)
    {
        var sendId = await database.SendCommands.AsNoTracking()
            .Where(command => command.RobotConfigId == robotConfigId &&
                              command.WorkToolCommandMessageId == workToolMessageId)
            .Select(command => (Guid?)command.Id)
            .SingleOrDefaultAsync(token);
        var operationId = await database.WorkToolOperationAudits.AsNoTracking()
            .Where(audit => audit.RobotConfigId == robotConfigId &&
                            audit.WorkToolCommandMessageId == workToolMessageId)
            .Select(audit => (Guid?)audit.Id)
            .SingleOrDefaultAsync(token);
        if (sendId is not null && operationId is not null)
            throw new InvalidOperationException("A WorkTool message ID belongs to multiple local commands.");
        if (sendId is not null)
            return new(WorkToolResultTargetKind.SendCommand, sendId.Value, robotConfigId, workToolMessageId);
        return operationId is null
            ? null
            : new(WorkToolResultTargetKind.GroupOperation, operationId.Value, robotConfigId, workToolMessageId);
    }

    public Task<WorkToolResultApplyOutcome> ApplyAsync(
        WorkToolResultTarget target,
        WorkToolExecutionResult result,
        CancellationToken token) =>
        ApplyAsync(target, result, retryOnConcurrency: true, token);

    public async Task RecordOrphanAsync(
        Guid robotConfigId,
        WorkToolExecutionResult result,
        CancellationToken token)
    {
        database.AdministrationAudits.Add(CreateAudit(
            "worktool.command-result.orphan",
            "RobotConfig",
            robotConfigId.ToString("D"),
            result,
            null));
        await database.SaveChangesAsync(token);
    }

    private async Task<WorkToolResultApplyOutcome> ApplyAsync(
        WorkToolResultTarget target,
        WorkToolExecutionResult result,
        bool retryOnConcurrency,
        CancellationToken token)
    {
        try
        {
            return target.Kind == WorkToolResultTargetKind.SendCommand
                ? await ApplyToSendAsync(target, result, token)
                : await ApplyToOperationAsync(target, result, token);
        }
        catch (DbUpdateConcurrencyException) when (retryOnConcurrency)
        {
            database.ChangeTracker.Clear();
            return await ApplyAsync(target, result, retryOnConcurrency: false, token);
        }
    }

    private async Task<WorkToolResultApplyOutcome> ApplyToSendAsync(
        WorkToolResultTarget target,
        WorkToolExecutionResult result,
        CancellationToken token)
    {
        var command = await database.SendCommands.SingleAsync(
            value => value.Id == target.Id &&
                     value.RobotConfigId == target.RobotConfigId &&
                     value.WorkToolCommandMessageId == target.WorkToolMessageId,
            token);
        var existing = ExistingOutcome(
            command.Status,
            command.WorkToolResultCode,
            command.WorkToolSuccessListJson,
            command.WorkToolFailListJson,
            command.WorkToolResultAtUtc,
            result);
        if (existing is not null)
        {
            if (existing == WorkToolResultApplyOutcome.Conflict)
                await RecordConflictAsync(target, result, token);
            return existing.Value;
        }

        if (!string.Equals(command.Status, WorkToolCommandStatuses.Accepted, StringComparison.Ordinal))
        {
            command.ReconciliationReason = "command_result_out_of_order";
            database.AdministrationAudits.Add(CreateAudit(
                "worktool.command-result.out-of-order",
                "SendCommand",
                command.Id.ToString("D"),
                result,
                command.Status));
        }

        ApplyResult(command, result);
        await database.SaveChangesAsync(token);
        return WorkToolResultApplyOutcome.Applied;
    }

    private async Task<WorkToolResultApplyOutcome> ApplyToOperationAsync(
        WorkToolResultTarget target,
        WorkToolExecutionResult result,
        CancellationToken token)
    {
        var audit = await database.WorkToolOperationAudits.SingleAsync(
            value => value.Id == target.Id &&
                     value.RobotConfigId == target.RobotConfigId &&
                     value.WorkToolCommandMessageId == target.WorkToolMessageId,
            token);
        var existing = ExistingOutcome(
            audit.Status,
            audit.WorkToolResultCode,
            audit.WorkToolSuccessListJson,
            audit.WorkToolFailListJson,
            audit.WorkToolResultAtUtc,
            result);
        if (existing is not null)
        {
            if (existing == WorkToolResultApplyOutcome.Conflict)
                await RecordConflictAsync(target, result, token);
            return existing.Value;
        }

        if (!string.Equals(audit.Status, WorkToolCommandStatuses.Accepted, StringComparison.Ordinal))
        {
            database.AdministrationAudits.Add(CreateAudit(
                "worktool.command-result.out-of-order",
                "WorkToolOperationAudit",
                audit.Id.ToString("D"),
                result,
                audit.Status));
        }

        ApplyResult(audit, result);
        await database.SaveChangesAsync(token);
        return WorkToolResultApplyOutcome.Applied;
    }

    private async Task RecordConflictAsync(
        WorkToolResultTarget target,
        WorkToolExecutionResult result,
        CancellationToken token)
    {
        database.ChangeTracker.Clear();
        database.AdministrationAudits.Add(CreateAudit(
            "worktool.command-result.conflict",
            target.Kind.ToString(),
            target.Id.ToString("D"),
            result,
            null));
        await database.SaveChangesAsync(token);
    }

    private static WorkToolResultApplyOutcome? ExistingOutcome(
        string status,
        int? resultCode,
        string? successJson,
        string? failJson,
        DateTime? resultAtUtc,
        WorkToolExecutionResult incoming)
    {
        if (resultAtUtc is not null)
        {
            return resultCode == incoming.ErrorCode &&
                   string.Equals(status, incoming.FinalStatus, StringComparison.Ordinal) &&
                   JsonEqual(successJson, incoming.SuccessList) &&
                   JsonEqual(failJson, incoming.FailList)
                ? WorkToolResultApplyOutcome.AlreadyApplied
                : WorkToolResultApplyOutcome.Conflict;
        }

        return TerminalStatuses.Contains(status)
            ? WorkToolResultApplyOutcome.Conflict
            : null;
    }

    private static bool JsonEqual(string? existingJson, IReadOnlyList<string> incoming)
    {
        if (existingJson is null) return incoming.Count == 0;
        try
        {
            return (JsonSerializer.Deserialize<string[]>(existingJson) ?? [])
                .SequenceEqual(incoming, StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void ApplyResult(SendCommandEntity command, WorkToolExecutionResult result)
    {
        command.Status = result.FinalStatus;
        command.WorkToolResultCode = result.ErrorCode;
        command.WorkToolResultAtUtc = result.ResultAtUtc;
        command.WorkToolSuccessListJson = JsonSerializer.Serialize(result.SuccessList);
        command.WorkToolFailListJson = JsonSerializer.Serialize(result.FailList);
        command.CompletedAtUtc = result.ResultAtUtc;
        command.LeaseOwner = null;
        command.LeaseExpiresAtUtc = null;
        command.Version++;
    }

    private static void ApplyResult(WorkToolOperationAuditEntity audit, WorkToolExecutionResult result)
    {
        audit.Status = result.FinalStatus;
        audit.WorkToolResultCode = result.ErrorCode;
        audit.WorkToolResultAtUtc = result.ResultAtUtc;
        audit.WorkToolSuccessListJson = JsonSerializer.Serialize(result.SuccessList);
        audit.WorkToolFailListJson = JsonSerializer.Serialize(result.FailList);
        audit.CompletedAtUtc = result.ResultAtUtc;
        audit.Result = $"worktool_code_{result.ErrorCode}";
        audit.LeaseOwner = null;
        audit.LeaseExpiresAtUtc = null;
        audit.Version++;
    }

    private static AdministrationAuditEntity CreateAudit(
        string action,
        string targetType,
        string targetId,
        WorkToolExecutionResult result,
        string? previousStatus) =>
        new()
        {
            Actor = "worktool-callback",
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            SanitizedDetailJson = JsonSerializer.Serialize(new
            {
                result.ErrorCode,
                result.FinalStatus,
                SuccessCount = result.SuccessList.Count,
                FailCount = result.FailList.Count,
                PreviousStatus = previousStatus
            }),
            CreatedAtUtc = result.ResultAtUtc
        };
}
