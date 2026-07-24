using Microsoft.EntityFrameworkCore;
using WechatRobot.Infrastructure.Knowledge;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.IntegrationTests.Knowledge;

public sealed class KnowledgeTagManagerTests
{
    [Fact]
    public async Task List_filters_by_text_state_and_scope_with_stable_order()
    {
        await using var database = NewDatabase();
        var firstProduct = new KnowledgeTagEntity
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000002"),
            Name = "产品",
            NormalizedName = "产品",
            IsEnabled = true
        };
        var secondProduct = new KnowledgeTagEntity
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            Name = "产品",
            NormalizedName = "产品-副本",
            IsEnabled = true
        };
        database.KnowledgeTags.AddRange(
            firstProduct,
            secondProduct,
            new KnowledgeTagEntity
            {
                Name = "售后",
                NormalizedName = "售后",
                IsEnabled = false
            },
            new KnowledgeTagEntity
            {
                Name = "公开知识",
                NormalizedName = "公开知识",
                IsEnabled = true,
                IsGlobalPublic = true
            });
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        var manager = new KnowledgeTagManager(database);

        var scoped = await manager.ListAsync(
            "产",
            isEnabled: true,
            isGlobalPublic: false,
            page: 1,
            pageSize: 20,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, scoped.Total);
        Assert.Equal([secondProduct.Id, firstProduct.Id], scoped.Items.Select(item => item.Id));
        Assert.All(scoped.Items, item => Assert.False(item.IsGlobalPublic));

        var global = await manager.ListAsync(
            "知",
            isEnabled: true,
            isGlobalPublic: true,
            page: 1,
            pageSize: 20,
            TestContext.Current.CancellationToken);
        var tag = Assert.Single(global.Items);
        Assert.Equal("公开知识", tag.Name);
        Assert.True(tag.IsEnabled);
        Assert.True(tag.IsGlobalPublic);
    }

    [Fact]
    public async Task List_clamps_page_boundaries_and_returns_requested_slice()
    {
        await using var database = NewDatabase();
        database.KnowledgeTags.AddRange(Enumerable.Range(1, 3).Select(index => new KnowledgeTagEntity
        {
            Id = Guid.Parse($"00000000-0000-0000-0000-{index:D12}"),
            Name = $"Tag {index}",
            NormalizedName = $"TAG {index}"
        }));
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        var manager = new KnowledgeTagManager(database);

        var minimum = await manager.ListAsync(
            null,
            null,
            null,
            page: 0,
            pageSize: 0,
            TestContext.Current.CancellationToken);
        var maximum = await manager.ListAsync(
            null,
            null,
            null,
            page: 1,
            pageSize: 1000,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, minimum.Page);
        Assert.Equal(1, minimum.PageSize);
        Assert.Single(minimum.Items);
        Assert.Equal(3, minimum.Total);
        Assert.Equal(100, maximum.PageSize);
        Assert.Equal(3, maximum.Items.Count);
    }

    [Fact]
    public async Task Options_returns_only_enabled_tags_in_stable_order()
    {
        await using var database = NewDatabase();
        database.KnowledgeTags.AddRange(
            new KnowledgeTagEntity
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000002"),
                Name = "Enabled",
                NormalizedName = "ENABLED",
                IsEnabled = true
            },
            new KnowledgeTagEntity
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
                Name = "Enabled",
                NormalizedName = "ENABLED-SECOND",
                IsEnabled = true,
                IsGlobalPublic = true
            },
            new KnowledgeTagEntity
            {
                Name = "Disabled",
                NormalizedName = "DISABLED",
                IsEnabled = false
            });
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);

        var options = await new KnowledgeTagManager(database).ListOptionsAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                Guid.Parse("00000000-0000-0000-0000-000000000001"),
                Guid.Parse("00000000-0000-0000-0000-000000000002")
            ],
            options.Select(item => item.Id));
        Assert.True(options[0].IsGlobalPublic);
        Assert.DoesNotContain(options, item => item.Name == "Disabled");
    }

    [Theory]
    [InlineData(" Product ", "PRODUCT")]
    [InlineData("售后", "售后")]
    public void NormalizeName_trims_and_uses_invariant_uppercase(string input, string expected)
    {
        Assert.Equal(expected, KnowledgeTagManager.NormalizeName(input));
    }

    private static WechatRobotDbContext NewDatabase()
    {
        var options = new DbContextOptionsBuilder<WechatRobotDbContext>()
            .UseInMemoryDatabase($"knowledge-tags-{Guid.NewGuid():N}")
            .Options;
        return new WechatRobotDbContext(options);
    }
}
