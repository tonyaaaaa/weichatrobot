namespace WechatRobot.Application.WorkTool;

public sealed class WorkToolCommandResultProcessor(
    IWorkToolCommandResultStore store,
    TimeProvider timeProvider)
{
    public async Task<WorkToolResultApplyOutcome> ProcessAsync(
        Guid robotConfigId,
        WorkToolCommandResultDto dto,
        CancellationToken token)
    {
        if (!dto.IsValid(out var reason))
            throw new ArgumentException(reason, nameof(dto));

        var result = new WorkToolExecutionResult(
            dto.MessageId!.Trim(),
            FinalStatus(dto),
            dto.ErrorCode!.Value,
            timeProvider.GetUtcNow().UtcDateTime,
            Normalize(dto.SuccessList),
            Normalize(dto.FailList));
        var target = await store.FindAsync(robotConfigId, result.WorkToolMessageId, token);
        if (target is null)
        {
            await store.RecordOrphanAsync(robotConfigId, result, token);
            return WorkToolResultApplyOutcome.Applied;
        }

        return await store.ApplyAsync(target, result, token);
    }

    private static string FinalStatus(WorkToolCommandResultDto dto)
    {
        if (dto.ErrorCode != 0) return WorkToolCommandStatuses.ExecutedFailed;
        var successes = dto.SuccessList?.Count ?? 0;
        var failures = dto.FailList?.Count ?? 0;
        if (successes > 0 && failures > 0) return WorkToolCommandStatuses.ExecutedPartially;
        if (failures > 0) return WorkToolCommandStatuses.ExecutedFailed;
        return WorkToolCommandStatuses.ExecutedSucceeded;
    }

    private static IReadOnlyList<string> Normalize(IReadOnlyList<string>? values) =>
        values?.Select(value => value.Trim()).ToArray() ?? [];
}
