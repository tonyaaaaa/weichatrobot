using Microsoft.EntityFrameworkCore;
using WechatRobot.Infrastructure.Knowledge;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.IntegrationTests.Infrastructure;

namespace WechatRobot.IntegrationTests.Knowledge;

public sealed class KnowledgeDocumentAdministrationMySqlTests(MySqlFixture fixture) : IClassFixture<MySqlFixture>
{
    [Fact]
    public async Task List_and_detail_queries_translate_on_mysql()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var document = new KnowledgeDocumentEntity
        {
            Title = $"Query Translation {suffix}",
            Status = "uploaded"
        };
        var version = new KnowledgeDocumentVersionEntity
        {
            KnowledgeDocumentId = document.Id,
            Version = 1,
            OriginalFileName = "source.txt",
            SafeFileName = "source.txt",
            ContentType = "text/plain",
            Sha256 = Guid.NewGuid().ToString("N").PadRight(64, '0'),
            ObjectKey = $"test/{suffix}",
            Status = "uploaded"
        };
        await using (var setup = CreateDatabase())
        {
            await setup.Database.MigrateAsync(TestContext.Current.CancellationToken);
            setup.KnowledgeDocuments.Add(document);
            setup.KnowledgeDocumentVersions.Add(version);
            await setup.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var database = CreateDatabase();
        var query = new KnowledgeDocumentAdministrationQuery(database);
        var page = await query.ListAsync(
            suffix,
            "uploaded",
            1,
            20,
            TestContext.Current.CancellationToken);
        var detail = await query.GetAsync(
            document.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(document.Id, Assert.Single(page.Items).Id);
        Assert.Equal(version.Id, Assert.Single(detail!.Versions).Id);
    }

    private WechatRobotDbContext CreateDatabase() =>
        new(new DbContextOptionsBuilder<WechatRobotDbContext>()
            .UseMySQL(fixture.ConnectionString)
            .Options);
}
