namespace WechatRobot.Application.Groups;

public sealed record GroupLifecycleState(
    Guid Id,
    string State,
    bool IsEnabled,
    DateTime? ArchivedAtUtc,
    int StateVersion);

public sealed record GroupLifecycleBlockers(
    int ActiveSendCommands,
    int ActiveInboundMessages,
    int ActiveMemoryJobs)
{
    public bool HasAny => ActiveSendCommands > 0 || ActiveInboundMessages > 0 || ActiveMemoryJobs > 0;
}

public sealed record GroupLifecycleStoreUpdate(bool Updated, GroupLifecycleState? State)
{
    public static GroupLifecycleStoreUpdate Conflict { get; } = new(false, null);
}

public interface IGroupLifecycleStore
{
    Task<GroupLifecycleState?> FindAsync(Guid id, CancellationToken token);
    Task<GroupLifecycleBlockers> CountArchiveBlockersAsync(Guid id, CancellationToken token);
    Task<GroupLifecycleStoreUpdate> TryUpdateAsync(
        Guid id,
        int expectedStateVersion,
        bool isEnabled,
        DateTime? archivedAtUtc,
        CancellationToken token);
}
