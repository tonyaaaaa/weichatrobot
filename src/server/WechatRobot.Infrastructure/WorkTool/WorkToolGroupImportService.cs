using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.WorkTool;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.Infrastructure.WorkTool;

public sealed class WorkToolGroupImportService(
    IDbContextFactory<WechatRobotDbContext> databaseFactory,
    IWorkToolClient workTool,
    TimeProvider timeProvider)
{
    public async Task<RemoteGroupPage> DiscoverAsync(
        Guid robotConfigId,
        string? query,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var remote = await workTool.ListGroupsAsync(
            robotConfigId,
            query,
            page,
            pageSize,
            cancellationToken);
        await using var database = await databaseFactory.CreateDbContextAsync(
            cancellationToken);
        var localNames = await database.GroupProfiles.AsNoTracking()
            .Where(group => group.RobotConfigId == robotConfigId)
            .Select(group => group.Name)
            .ToArrayAsync(cancellationToken);
        var counts = localNames
            .GroupBy(name => name.Trim(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        return new(
            remote.PageNumber,
            remote.PageSize,
            remote.TotalPages,
            remote.Total,
            remote.Items.Select(group => new RemoteGroupItem(
                group.GroupName,
                group.MasterName,
                group.MembersCount,
                group.GroupAnnouncement,
                counts.GetValueOrDefault(group.GroupName.Trim()) switch
                {
                    0 => "Available",
                    1 => "Imported",
                    _ => "Conflict"
                })).ToArray());
    }

    public async Task<IReadOnlyList<GroupImportResult>> ImportAsync(
        Guid robotConfigId,
        IReadOnlyList<GroupImportSelection> selections,
        string actor,
        CancellationToken cancellationToken)
    {
        if (selections.Count is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(selections));
        var normalized = selections
            .Select(selection => selection with { GroupName = selection.GroupName.Trim() })
            .ToArray();
        if (normalized.Any(selection =>
                selection.GroupName.Length is < 1 or > 256
                || selection.ExpectedImportState != "Available")
            || normalized.Select(selection => selection.GroupName)
                .Distinct(StringComparer.Ordinal).Count() != normalized.Length)
            throw new ArgumentException("Group import selections are invalid.", nameof(selections));

        var verified = new Dictionary<string, WorkToolGroupSummary>(StringComparer.Ordinal);
        var preflightResults = new Dictionary<string, GroupImportResult>(StringComparer.Ordinal);
        foreach (var selection in normalized)
        {
            var page = await workTool.ListGroupsAsync(
                robotConfigId,
                selection.GroupName,
                1,
                100,
                cancellationToken);
            var matches = page.Items
                .Where(group => group.GroupName.Trim() == selection.GroupName)
                .ToArray();
            if (matches.Length != 1)
            {
                preflightResults[selection.GroupName] = new(
                    selection.GroupName,
                    "Conflict",
                    null,
                    matches.Length == 0
                        ? "worktool-group-not-found"
                        : "worktool-group-ambiguous");
                continue;
            }
            verified[selection.GroupName] = matches[0];
        }

        var results = new List<GroupImportResult>(normalized.Length);
        foreach (var selection in normalized)
        {
            if (preflightResults.TryGetValue(selection.GroupName, out var preflight))
            {
                results.Add(preflight);
                continue;
            }
            results.Add(await ImportOneAsync(
                robotConfigId,
                verified[selection.GroupName],
                actor,
                cancellationToken));
        }
        return results;
    }

    private async Task<GroupImportResult> ImportOneAsync(
        Guid robotConfigId,
        WorkToolGroupSummary remote,
        string actor,
        CancellationToken cancellationToken)
    {
        await using var database = await databaseFactory.CreateDbContextAsync(
            cancellationToken);
        var isMySql = string.Equals(
            database.Database.ProviderName,
            "MySql.EntityFrameworkCore",
            StringComparison.Ordinal);
        await using var transaction = isMySql
            ? await database.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken)
            : null;
        var name = remote.GroupName.Trim();
        var matches = await database.GroupProfiles
            .Where(group => group.RobotConfigId == robotConfigId && group.Name == name)
            .ToArrayAsync(cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (matches.Length > 1)
        {
            if (transaction is not null)
                await transaction.RollbackAsync(cancellationToken);
            return new(name, "Conflict", null, "local-group-ambiguous");
        }
        if (matches.Length == 1)
        {
            matches[0].WorkToolLastSeenAtUtc = now;
            matches[0].UpdatedAtUtc = now;
            await database.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
            return new(name, "Imported", matches[0].Id, null);
        }

        var entity = new GroupProfileEntity
        {
            RobotConfigId = robotConfigId,
            Name = name,
            WorkToolGroupRemark = name,
            RegistrationSource = "WorkToolImport",
            WorkToolImportedAtUtc = now,
            WorkToolLastSeenAtUtc = now,
            IsEnabled = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        database.GroupProfiles.Add(entity);
        database.AdministrationAudits.Add(new AdministrationAuditEntity
        {
            Actor = actor,
            Action = "worktool_group_imported",
            TargetType = "GroupProfile",
            TargetId = entity.Id.ToString("D"),
            SanitizedDetailJson = JsonSerializer.Serialize(new
            {
                groupProfileId = entity.Id,
                groupName = name
            }),
            CreatedAtUtc = now
        });
        await database.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        return new(name, "Imported", entity.Id, null);
    }
}

public sealed record GroupImportSelection(
    string GroupName,
    string ExpectedImportState);

public sealed record GroupImportResult(
    string GroupName,
    string Status,
    Guid? GroupProfileId,
    string? ErrorCode);

public sealed record RemoteGroupPage(
    int PageNumber,
    int PageSize,
    int TotalPages,
    int Total,
    IReadOnlyList<RemoteGroupItem> Items);

public sealed record RemoteGroupItem(
    string GroupName,
    string? MasterName,
    int MembersCount,
    string? GroupAnnouncement,
    string ImportState);
