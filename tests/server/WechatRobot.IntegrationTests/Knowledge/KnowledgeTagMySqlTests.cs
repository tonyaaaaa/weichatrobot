using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Knowledge;
using WechatRobot.Infrastructure.Knowledge;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.IntegrationTests.Infrastructure;

namespace WechatRobot.IntegrationTests.Knowledge;

public sealed class KnowledgeTagMySqlTests(MySqlFixture fixture) : IClassFixture<MySqlFixture>
{
    [Fact]
    public async Task Concurrent_equivalent_names_leave_one_tag_and_one_create_audit()
    {
        await using (var setup = CreateDatabase())
        {
            await setup.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }

        await using var firstDatabase = CreateDatabase();
        await using var secondDatabase = CreateDatabase();
        var displayName = $"Race Product {Guid.NewGuid():N}";
        var results = await Task.WhenAll(
            new KnowledgeTagManager(firstDatabase).CreateAsync(
                "first-operator",
                new KnowledgeTagDraft(displayName, false),
                TestContext.Current.CancellationToken),
            new KnowledgeTagManager(secondDatabase).CreateAsync(
                "second-operator",
                new KnowledgeTagDraft($" {displayName.ToLowerInvariant()} ", true),
                TestContext.Current.CancellationToken));

        Assert.Single(results, result => result.Status == KnowledgeTagMutationStatus.Succeeded);
        Assert.Single(results, result => result.Status == KnowledgeTagMutationStatus.NameConflict);

        await using var verify = CreateDatabase();
        var normalized = KnowledgeTagManager.NormalizeName(displayName);
        var tags = await verify.KnowledgeTags.AsNoTracking()
            .Where(tag => tag.NormalizedName == normalized)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        var saved = Assert.Single(tags);
        Assert.Equal(
            1,
            await verify.AdministrationAudits.CountAsync(
                audit => audit.Action == "knowledge-tag.create" &&
                         audit.TargetId == saved.Id.ToString("D"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Global_tag_repair_preserves_chunk_binding_under_mysql_constraints()
    {
        await using var database = CreateDatabase();
        await database.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var canonical = await database.KnowledgeTags.SingleAsync(
            tag => tag.SystemKind == GlobalKnowledgeTag.SystemKind,
            TestContext.Current.CancellationToken);
        var duplicate = new KnowledgeTagEntity
        {
            Name = GlobalKnowledgeTag.DisplayName,
            NormalizedName = GlobalKnowledgeTag.DisplayName
        };
        var document = new KnowledgeDocumentEntity
        {
            Title = "全局标签归并测试"
        };
        var version = new KnowledgeDocumentVersionEntity
        {
            KnowledgeDocumentId = document.Id,
            Version = 1,
            OriginalFileName = "global-tag-repair.txt",
            SafeFileName = $"{Guid.NewGuid():N}.txt",
            ContentType = "text/plain",
            Sha256 = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            ObjectKey = $"tests/{Guid.NewGuid():N}"
        };
        var chunk = new KnowledgeChunkEntity
        {
            KnowledgeDocumentVersionId = version.Id,
            Sequence = 1,
            Text = "全局标签归并测试"
        };
        database.KnowledgeTags.Add(duplicate);
        database.KnowledgeDocuments.Add(document);
        database.KnowledgeDocumentVersions.Add(version);
        database.KnowledgeChunks.Add(chunk);
        database.KnowledgeChunkTags.AddRange(
            new KnowledgeChunkTagEntity
            {
                KnowledgeChunkId = chunk.Id,
                KnowledgeTagId = canonical.Id
            },
            new KnowledgeChunkTagEntity
            {
                KnowledgeChunkId = chunk.Id,
                KnowledgeTagId = duplicate.Id
            });
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await new GlobalKnowledgeTagRepairService(
                database,
                TimeProvider.System)
            .RepairAsync(TestContext.Current.CancellationToken);

        database.ChangeTracker.Clear();
        Assert.True(result.Changed);
        Assert.False(await database.KnowledgeTags.AnyAsync(
            tag => tag.Id == duplicate.Id,
            TestContext.Current.CancellationToken));
        Assert.Equal(
            [canonical.Id],
            await database.KnowledgeChunkTags.AsNoTracking()
                .Where(binding => binding.KnowledgeChunkId == chunk.Id)
                .Select(binding => binding.KnowledgeTagId)
                .ToArrayAsync(TestContext.Current.CancellationToken));
    }

    private WechatRobotDbContext CreateDatabase() => new(
        new DbContextOptionsBuilder<WechatRobotDbContext>()
            .UseMySQL(fixture.ConnectionString)
            .Options);
}
