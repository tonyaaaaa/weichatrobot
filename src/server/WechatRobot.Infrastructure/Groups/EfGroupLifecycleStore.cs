using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Groups;
using WechatRobot.Infrastructure.Persistence;

namespace WechatRobot.Infrastructure.Groups;

public sealed class EfGroupLifecycleStore(
    WechatRobotDbContext database,
    TimeProvider timeProvider) : IGroupLifecycleStore
{
    private static readonly string[] ActiveStatuses = ["pending", "retrying", "leased", "dispatching"];
    private static readonly string[] ActiveInboundStates = ["pending", "retrying", "leased", "processing"];
    private static readonly string[] MemoryJobTypes =
        ["ExtractConversationMemory", "MaintainLongTermMemory", "IndexMemoryEntry", "RemoveMemoryEntryFromIndex"];

    public Task<GroupLifecycleState?> FindAsync(Guid id, CancellationToken token) =>
        database.GroupProfiles.AsNoTracking()
            .Where(group => group.Id == id)
            .Select(group => new GroupLifecycleState(
                group.Id,
                group.ArchivedAtUtc != null ? "archived" : group.IsEnabled ? "enabled" : "disabled",
                group.IsEnabled,
                group.ArchivedAtUtc,
                group.StateVersion))
            .SingleOrDefaultAsync(token);

    public async Task<GroupLifecycleBlockers> CountArchiveBlockersAsync(Guid id, CancellationToken token)
    {
        var activeSendCommands = await database.SendCommands.AsNoTracking()
            .CountAsync(command =>
                command.GroupProfileId == id &&
                ActiveStatuses.Contains(command.Status), token);
        var activeInboundMessages = await database.ConversationMessages.AsNoTracking()
            .CountAsync(message =>
                message.GroupProfileId == id &&
                message.Direction == "inbound" &&
                ActiveInboundStates.Contains(message.ProcessingState), token);
        var activeMemoryJobs = await database.DurableJobs.AsNoTracking()
            .CountAsync(job =>
                job.GroupProfileId == id &&
                MemoryJobTypes.Contains(job.JobType) &&
                ActiveStatuses.Contains(job.Status), token);
        return new(activeSendCommands, activeInboundMessages, activeMemoryJobs);
    }

    public async Task<GroupLifecycleStoreUpdate> TryUpdateAsync(
        Guid id,
        int expectedStateVersion,
        bool isEnabled,
        DateTime? archivedAtUtc,
        CancellationToken token)
    {
        var group = await database.GroupProfiles.SingleOrDefaultAsync(item => item.Id == id, token);
        if (group is null || group.StateVersion != expectedStateVersion)
            return GroupLifecycleStoreUpdate.Conflict;

        group.IsEnabled = isEnabled;
        group.ArchivedAtUtc = archivedAtUtc;
        group.StateVersion++;
        group.UpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        try
        {
            await database.SaveChangesAsync(token);
            if (!isEnabled)
            {
                await database.DurableJobs
                    .Where(job => job.GroupProfileId == id &&
                                  MemoryJobTypes.Contains(job.JobType) &&
                                  (job.Status == "pending" || job.Status == "retrying"))
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(job => job.Status, "cancelled")
                        .SetProperty(job => job.LeaseOwner, (string?)null)
                        .SetProperty(job => job.LeaseExpiresAtUtc, (DateTime?)null)
                        .SetProperty(job => job.Version, job => job.Version + 1)
                        .SetProperty(job => job.UpdatedAtUtc, timeProvider.GetUtcNow().UtcDateTime), token);
            }
        }
        catch (DbUpdateConcurrencyException)
        {
            return GroupLifecycleStoreUpdate.Conflict;
        }

        var state = new GroupLifecycleState(
            group.Id,
            archivedAtUtc is not null ? "archived" : isEnabled ? "enabled" : "disabled",
            isEnabled,
            archivedAtUtc,
            group.StateVersion);
        return new(true, state);
    }
}
