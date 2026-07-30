using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WechatRobot.Infrastructure.Knowledge;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.IntegrationTests.Knowledge;

public sealed class GlobalKnowledgeTagRepairServiceTests
{
    [Fact]
    public async Task Repair_merges_duplicate_global_tags_and_all_known_references_idempotently()
    {
        await using var database = NewDatabase();
        var canonical = new KnowledgeTagEntity
        {
            Id = GlobalKnowledgeTag.DefaultId,
            Name = GlobalKnowledgeTag.DisplayName,
            NormalizedName = GlobalKnowledgeTag.NormalizedName,
            SystemKind = GlobalKnowledgeTag.SystemKind,
            IsEnabled = true,
            IsGlobalPublic = true
        };
        var duplicate = new KnowledgeTagEntity
        {
            Name = " 全局知识 ",
            NormalizedName = "全局知识",
            IsEnabled = true
        };
        var groupId = Guid.NewGuid();
        var chunkId = Guid.NewGuid();
        database.KnowledgeTags.AddRange(canonical, duplicate);
        database.GroupProfileTags.AddRange(
            new GroupProfileTagEntity
            {
                GroupProfileId = groupId,
                KnowledgeTagId = canonical.Id
            },
            new GroupProfileTagEntity
            {
                GroupProfileId = groupId,
                KnowledgeTagId = duplicate.Id
            });
        database.KnowledgeChunkTags.AddRange(
            new KnowledgeChunkTagEntity
            {
                KnowledgeChunkId = chunkId,
                KnowledgeTagId = canonical.Id
            },
            new KnowledgeChunkTagEntity
            {
                KnowledgeChunkId = chunkId,
                KnowledgeTagId = duplicate.Id
            });
        database.KnowledgeReviews.Add(new KnowledgeReviewEntity
        {
            TagIdsJson = JsonSerializer.Serialize(new[] { duplicate.Id, canonical.Id, duplicate.Id })
        });
        database.KnowledgeIndexJobs.Add(new KnowledgeIndexJobEntity
        {
            PendingTagIdsJson = JsonSerializer.Serialize(new[] { duplicate.Id, canonical.Id })
        });
        database.PrivateKnowledgeIngestItems.Add(new PrivateKnowledgeIngestItemEntity
        {
            ResolvedTagIdsJson = JsonSerializer.Serialize(new[] { duplicate.Id, canonical.Id })
        });
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        var service = new GlobalKnowledgeTagRepairService(database, TimeProvider.System);

        var first = await service.RepairAsync(TestContext.Current.CancellationToken);
        var second = await service.RepairAsync(TestContext.Current.CancellationToken);

        Assert.True(first.Changed);
        Assert.Equal(1, first.RemovedDuplicateCount);
        Assert.False(second.Changed);
        Assert.Equal(0, second.RemovedDuplicateCount);
        var remaining = await database.KnowledgeTags.AsNoTracking().ToArrayAsync(
            TestContext.Current.CancellationToken);
        var global = Assert.Single(remaining);
        Assert.Equal(canonical.Id, global.Id);
        Assert.Equal(GlobalKnowledgeTag.SystemKind, global.SystemKind);
        Assert.True(global.IsEnabled);
        Assert.True(global.IsGlobalPublic);
        Assert.Equal(
            [canonical.Id],
            database.GroupProfileTags.AsNoTracking().Select(item => item.KnowledgeTagId));
        Assert.Equal(
            [canonical.Id],
            database.KnowledgeChunkTags.AsNoTracking().Select(item => item.KnowledgeTagId));
        Assert.Equal(
            [canonical.Id],
            Parse(await database.KnowledgeReviews.AsNoTracking()
                .Select(item => item.TagIdsJson)
                .SingleAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(
            [canonical.Id],
            Parse(await database.KnowledgeIndexJobs.AsNoTracking()
                .Select(item => item.PendingTagIdsJson)
                .SingleAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(
            [canonical.Id],
            Parse(await database.PrivateKnowledgeIngestItems.AsNoTracking()
                .Select(item => item.ResolvedTagIdsJson)
                .SingleAsync(TestContext.Current.CancellationToken)));
        var audit = Assert.Single(database.AdministrationAudits);
        Assert.Equal("knowledge-tag.system-global.repair", audit.Action);
        Assert.DoesNotContain(duplicate.Id.ToString("D"), audit.SanitizedDetailJson);
    }

    private static Guid[] Parse(string json) =>
        JsonSerializer.Deserialize<Guid[]>(json) ?? [];

    private static WechatRobotDbContext NewDatabase()
    {
        var options = new DbContextOptionsBuilder<WechatRobotDbContext>()
            .UseInMemoryDatabase($"global-tag-repair-{Guid.NewGuid():N}")
            .Options;
        return new WechatRobotDbContext(options);
    }
}
