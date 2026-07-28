namespace WechatRobot.Application.Groups;

public sealed record GroupLifecycleResult(
    string Status,
    GroupLifecycleState? State = null,
    string? ErrorCode = null,
    GroupLifecycleBlockers? Blockers = null)
{
    public const string Success = "success";
    public const string NotFound = "not-found";
    public const string Conflict = "conflict";
}

public sealed class GroupLifecycleService(
    IGroupLifecycleStore store,
    TimeProvider timeProvider)
{
    public Task<GroupLifecycleResult> DisableAsync(Guid id, int expectedVersion, CancellationToken token) =>
        ChangeAsync(id, expectedVersion, GroupLifecycleAction.Disable, token);

    public Task<GroupLifecycleResult> EnableAsync(Guid id, int expectedVersion, CancellationToken token) =>
        ChangeAsync(id, expectedVersion, GroupLifecycleAction.Enable, token);

    public Task<GroupLifecycleResult> ArchiveAsync(Guid id, int expectedVersion, CancellationToken token) =>
        ChangeAsync(id, expectedVersion, GroupLifecycleAction.Archive, token);

    public Task<GroupLifecycleResult> RestoreAsync(Guid id, int expectedVersion, CancellationToken token) =>
        ChangeAsync(id, expectedVersion, GroupLifecycleAction.Restore, token);

    private async Task<GroupLifecycleResult> ChangeAsync(
        Guid id,
        int expectedVersion,
        GroupLifecycleAction action,
        CancellationToken token)
    {
        var current = await store.FindAsync(id, token);
        if (current is null)
            return new(GroupLifecycleResult.NotFound, ErrorCode: "group-not-found");
        if (current.StateVersion != expectedVersion)
            return new(GroupLifecycleResult.Conflict, current, "group-state-conflict");

        var target = Target(current, action);
        if (target.ErrorCode is not null)
            return new(GroupLifecycleResult.Conflict, current, target.ErrorCode);
        if (target.IsNoOp)
            return new(GroupLifecycleResult.Success, current);

        GroupLifecycleBlockers? blockers = null;
        if (action == GroupLifecycleAction.Archive)
        {
            blockers = await store.CountArchiveBlockersAsync(id, token);
            if (blockers.HasAny)
                return new(GroupLifecycleResult.Conflict, current, "group-active-work", blockers);
        }

        var update = await store.TryUpdateAsync(
            id,
            expectedVersion,
            target.IsEnabled,
            target.Archived ? timeProvider.GetUtcNow().UtcDateTime : null,
            token);
        return update.Updated
            ? new(GroupLifecycleResult.Success, update.State)
            : new(GroupLifecycleResult.Conflict, ErrorCode: "group-state-conflict");
    }

    private static LifecycleTarget Target(GroupLifecycleState current, GroupLifecycleAction action) =>
        action switch
        {
            GroupLifecycleAction.Disable when current.ArchivedAtUtc is not null =>
                new(false, false, false, "group-is-archived"),
            GroupLifecycleAction.Disable when !current.IsEnabled =>
                new(false, false, true, null),
            GroupLifecycleAction.Disable => new(false, false, false, null),

            GroupLifecycleAction.Enable when current.ArchivedAtUtc is not null =>
                new(false, false, false, "group-is-archived"),
            GroupLifecycleAction.Enable when current.IsEnabled =>
                new(true, false, true, null),
            GroupLifecycleAction.Enable => new(true, false, false, null),

            GroupLifecycleAction.Archive when current.ArchivedAtUtc is not null =>
                new(false, true, true, null),
            GroupLifecycleAction.Archive when current.IsEnabled =>
                new(false, false, false, "group-must-be-disabled"),
            GroupLifecycleAction.Archive => new(false, true, false, null),

            GroupLifecycleAction.Restore when current.ArchivedAtUtc is null =>
                new(current.IsEnabled, false, true, null),
            GroupLifecycleAction.Restore => new(false, false, false, null),
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };

    private enum GroupLifecycleAction { Disable, Enable, Archive, Restore }
    private sealed record LifecycleTarget(bool IsEnabled, bool Archived, bool IsNoOp, string? ErrorCode);
}
