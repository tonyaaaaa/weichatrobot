using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Knowledge;
using WechatRobot.Infrastructure.Knowledge;
using WechatRobot.Infrastructure.Persistence;
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

    private WechatRobotDbContext CreateDatabase() => new(
        new DbContextOptionsBuilder<WechatRobotDbContext>()
            .UseMySQL(fixture.ConnectionString)
            .Options);
}
