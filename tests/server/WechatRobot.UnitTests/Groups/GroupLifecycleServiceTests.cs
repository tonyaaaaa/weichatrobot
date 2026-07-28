using WechatRobot.Application.Groups;

namespace WechatRobot.UnitTests.Groups;

public sealed class GroupLifecycleServiceTests
{
    [Fact]
    public async Task Supports_disable_enable_archive_and_restore_with_independent_state_versions()
    {
        var store = new FakeStore(new(Guid.NewGuid(), "enabled", true, null, 0));
        var service = new GroupLifecycleService(store, TimeProvider.System);

        var disabled = await service.DisableAsync(store.State.Id, 0, CancellationToken.None);
        var enabled = await service.EnableAsync(store.State.Id, 1, CancellationToken.None);
        await service.DisableAsync(store.State.Id, 2, CancellationToken.None);
        var archived = await service.ArchiveAsync(store.State.Id, 3, CancellationToken.None);
        var restored = await service.RestoreAsync(store.State.Id, 4, CancellationToken.None);

        Assert.Equal("disabled", disabled.State?.State);
        Assert.Equal("enabled", enabled.State?.State);
        Assert.Equal("archived", archived.State?.State);
        Assert.Equal("disabled", restored.State?.State);
        Assert.False(restored.State?.IsEnabled);
        Assert.Null(restored.State?.ArchivedAtUtc);
        Assert.Equal(5, restored.State?.StateVersion);
    }

    [Fact]
    public async Task Same_target_is_idempotent_without_incrementing_state_version()
    {
        var state = new GroupLifecycleState(Guid.NewGuid(), "disabled", false, null, 7);
        var store = new FakeStore(state);
        var service = new GroupLifecycleService(store, TimeProvider.System);

        var result = await service.DisableAsync(state.Id, 7, CancellationToken.None);

        Assert.Equal(GroupLifecycleResult.Success, result.Status);
        Assert.Equal(7, result.State?.StateVersion);
        Assert.Equal(0, store.UpdateCalls);
    }

    [Fact]
    public async Task Rejects_stale_versions_enabled_archive_and_active_work()
    {
        var state = new GroupLifecycleState(Guid.NewGuid(), "enabled", true, null, 3);
        var store = new FakeStore(state);
        var service = new GroupLifecycleService(store, TimeProvider.System);

        var stale = await service.DisableAsync(state.Id, 2, CancellationToken.None);
        var enabledArchive = await service.ArchiveAsync(state.Id, 3, CancellationToken.None);
        store.State = state with { State = "disabled", IsEnabled = false };
        store.Blockers = new(2, 1, 3);
        var blocked = await service.ArchiveAsync(state.Id, 3, CancellationToken.None);

        Assert.Equal(GroupLifecycleResult.Conflict, stale.Status);
        Assert.Equal("group-state-conflict", stale.ErrorCode);
        Assert.Equal("group-must-be-disabled", enabledArchive.ErrorCode);
        Assert.Equal("group-active-work", blocked.ErrorCode);
        Assert.Equal(new GroupLifecycleBlockers(2, 1, 3), blocked.Blockers);
    }

    private sealed class FakeStore(GroupLifecycleState state) : IGroupLifecycleStore
    {
        public GroupLifecycleState State { get; set; } = state;
        public GroupLifecycleBlockers Blockers { get; set; } = new(0, 0, 0);
        public int UpdateCalls { get; private set; }

        public Task<GroupLifecycleState?> FindAsync(Guid id, CancellationToken token) =>
            Task.FromResult<GroupLifecycleState?>(id == State.Id ? State : null);

        public Task<GroupLifecycleBlockers> CountArchiveBlockersAsync(Guid id, CancellationToken token) =>
            Task.FromResult(Blockers);

        public Task<GroupLifecycleStoreUpdate> TryUpdateAsync(
            Guid id,
            int expectedStateVersion,
            bool isEnabled,
            DateTime? archivedAtUtc,
            CancellationToken token)
        {
            UpdateCalls++;
            if (State.StateVersion != expectedStateVersion)
                return Task.FromResult(GroupLifecycleStoreUpdate.Conflict);
            State = new(
                State.Id,
                archivedAtUtc is not null ? "archived" : isEnabled ? "enabled" : "disabled",
                isEnabled,
                archivedAtUtc,
                State.StateVersion + 1);
            return Task.FromResult(new GroupLifecycleStoreUpdate(true, State));
        }
    }
}
