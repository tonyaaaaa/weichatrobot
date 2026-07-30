using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using WechatRobot.Application.Knowledge;
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

    [Fact]
    public async Task Create_normalizes_name_and_writes_one_sanitized_audit()
    {
        await using var database = NewDatabase();
        var manager = new KnowledgeTagManager(database);

        var result = await manager.CreateAsync(
            "knowledge-operator",
            new KnowledgeTagDraft("  Product  ", false),
            TestContext.Current.CancellationToken);

        Assert.Equal(KnowledgeTagMutationStatus.Succeeded, result.Status);
        Assert.Equal("Product", result.Tag!.Name);
        Assert.Equal(0, result.Tag.Version);
        var tag = await database.KnowledgeTags.SingleAsync(
            item => item.NormalizedName == "PRODUCT",
            TestContext.Current.CancellationToken);
        var audit = await database.AdministrationAudits.SingleAsync(
            item => item.TargetId == tag.Id.ToString("D"),
            TestContext.Current.CancellationToken);
        Assert.Equal("knowledge-operator", audit.Actor);
        Assert.Equal("knowledge-tag.create", audit.Action);
        Assert.Equal("knowledge-tag", audit.TargetType);
        using var detail = JsonDocument.Parse(audit.SanitizedDetailJson);
        Assert.Equal("Product", detail.RootElement.GetProperty("after").GetProperty("name").GetString());
        Assert.DoesNotContain(
            "authorization",
            audit.SanitizedDetailJson,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_rejects_invalid_name_without_writing_tag_or_audit(string name)
    {
        await using var database = NewDatabase();

        var result = await new KnowledgeTagManager(database).CreateAsync(
            "knowledge-operator",
            new KnowledgeTagDraft(name, false),
            TestContext.Current.CancellationToken);

        Assert.Equal(KnowledgeTagMutationStatus.InvalidInput, result.Status);
        Assert.Equal("knowledge-tag-name-invalid", result.Error);
        Assert.Empty(database.KnowledgeTags);
        Assert.Empty(database.AdministrationAudits);
    }

    [Fact]
    public async Task Create_and_update_reject_normalized_name_conflicts()
    {
        await using var database = NewDatabase();
        var existing = new KnowledgeTagEntity
        {
            Name = "Product",
            NormalizedName = "PRODUCT"
        };
        var other = new KnowledgeTagEntity
        {
            Name = "Support",
            NormalizedName = "SUPPORT"
        };
        database.KnowledgeTags.AddRange(existing, other);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        var manager = new KnowledgeTagManager(database);

        var create = await manager.CreateAsync(
            "knowledge-operator",
            new KnowledgeTagDraft(" product ", false),
            TestContext.Current.CancellationToken);
        var update = await manager.UpdateAsync(
            other.Id,
            "knowledge-operator",
            new KnowledgeTagUpdate("PRODUCT", false, other.Version),
            TestContext.Current.CancellationToken);

        Assert.Equal(KnowledgeTagMutationStatus.NameConflict, create.Status);
        Assert.Equal(existing.Id, create.Tag!.Id);
        Assert.Equal(KnowledgeTagMutationStatus.NameConflict, update.Status);
        Assert.Equal(existing.Id, update.Tag!.Id);
        Assert.Empty(database.AdministrationAudits);
    }

    [Fact]
    public async Task Create_rejects_reserved_global_display_name_as_canonical_conflict()
    {
        await using var database = NewDatabase();
        var canonical = GlobalKnowledgeTag.Create(DateTime.UtcNow);
        database.KnowledgeTags.Add(canonical);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await new KnowledgeTagManager(database).CreateAsync(
            "knowledge-operator",
            new KnowledgeTagDraft(" 全局知识 ", false),
            TestContext.Current.CancellationToken);

        Assert.Equal(KnowledgeTagMutationStatus.NameConflict, result.Status);
        Assert.Equal(canonical.Id, result.Tag!.Id);
        Assert.Single(database.KnowledgeTags);
        Assert.Empty(database.AdministrationAudits);
    }

    [Fact]
    public async Task Delete_rejects_system_global_tag_even_when_it_has_no_references()
    {
        await using var database = NewDatabase();
        var canonical = GlobalKnowledgeTag.Create(DateTime.UtcNow);
        database.KnowledgeTags.Add(canonical);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await new KnowledgeTagManager(database).DeleteAsync(
            canonical.Id,
            "administrator",
            canonical.Version,
            TestContext.Current.CancellationToken);

        Assert.Equal(KnowledgeTagMutationStatus.InvalidInput, result.Status);
        Assert.Equal("knowledge-tag-system-managed", result.Error);
        Assert.Single(database.KnowledgeTags);
        Assert.Empty(database.AdministrationAudits);
    }

    [Fact]
    public async Task Update_requires_current_version_and_audits_before_and_after()
    {
        await using var database = NewDatabase();
        var tag = new KnowledgeTagEntity
        {
            Name = "Product",
            NormalizedName = "PRODUCT",
            Version = 3
        };
        database.KnowledgeTags.Add(tag);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        var manager = new KnowledgeTagManager(database);

        var stale = await manager.UpdateAsync(
            tag.Id,
            "knowledge-operator",
            new KnowledgeTagUpdate("Changed", true, ExpectedVersion: 2),
            TestContext.Current.CancellationToken);
        var saved = await manager.UpdateAsync(
            tag.Id,
            "knowledge-operator",
            new KnowledgeTagUpdate("Changed", true, ExpectedVersion: 3),
            TestContext.Current.CancellationToken);

        Assert.Equal(KnowledgeTagMutationStatus.ConcurrencyConflict, stale.Status);
        Assert.Equal(3, stale.Tag!.Version);
        Assert.Equal("Product", stale.Tag.Name);
        Assert.Equal(KnowledgeTagMutationStatus.Succeeded, saved.Status);
        Assert.Equal(4, saved.Tag!.Version);
        Assert.Equal("Changed", saved.Tag.Name);
        Assert.True(saved.Tag.IsGlobalPublic);
        var audit = Assert.Single(database.AdministrationAudits);
        Assert.Equal("knowledge-tag.update", audit.Action);
        using var detail = JsonDocument.Parse(audit.SanitizedDetailJson);
        Assert.Equal("Product", detail.RootElement.GetProperty("before").GetProperty("name").GetString());
        Assert.Equal("Changed", detail.RootElement.GetProperty("after").GetProperty("name").GetString());
    }

    [Fact]
    public async Task State_change_is_versioned_audited_and_idempotent_for_same_state()
    {
        await using var database = NewDatabase();
        var tag = new KnowledgeTagEntity
        {
            Name = "Product",
            NormalizedName = "PRODUCT",
            IsEnabled = true
        };
        database.KnowledgeTags.Add(tag);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        var manager = new KnowledgeTagManager(database);

        var noOp = await manager.SetEnabledAsync(
            tag.Id,
            "knowledge-operator",
            new KnowledgeTagStateUpdate(true, ExpectedVersion: 0),
            TestContext.Current.CancellationToken);
        var disabled = await manager.SetEnabledAsync(
            tag.Id,
            "knowledge-operator",
            new KnowledgeTagStateUpdate(false, ExpectedVersion: 0),
            TestContext.Current.CancellationToken);
        var stale = await manager.SetEnabledAsync(
            tag.Id,
            "knowledge-operator",
            new KnowledgeTagStateUpdate(true, ExpectedVersion: 0),
            TestContext.Current.CancellationToken);

        Assert.Equal(KnowledgeTagMutationStatus.Succeeded, noOp.Status);
        Assert.Equal(0, noOp.Tag!.Version);
        Assert.Equal(KnowledgeTagMutationStatus.Succeeded, disabled.Status);
        Assert.False(disabled.Tag!.IsEnabled);
        Assert.Equal(1, disabled.Tag.Version);
        Assert.Equal(KnowledgeTagMutationStatus.ConcurrencyConflict, stale.Status);
        var audit = Assert.Single(database.AdministrationAudits);
        Assert.Equal("knowledge-tag.disable", audit.Action);
    }

    [Fact]
    public async Task Mutations_return_not_found_without_audit()
    {
        await using var database = NewDatabase();
        var manager = new KnowledgeTagManager(database);
        var id = Guid.NewGuid();

        var update = await manager.UpdateAsync(
            id,
            "knowledge-operator",
            new KnowledgeTagUpdate("Product", false, 0),
            TestContext.Current.CancellationToken);
        var state = await manager.SetEnabledAsync(
            id,
            "knowledge-operator",
            new KnowledgeTagStateUpdate(false, 0),
            TestContext.Current.CancellationToken);

        Assert.Equal(KnowledgeTagMutationStatus.NotFound, update.Status);
        Assert.Equal(KnowledgeTagMutationStatus.NotFound, state.Status);
        Assert.Empty(database.AdministrationAudits);
    }

    private static WechatRobotDbContext NewDatabase()
    {
        var options = new DbContextOptionsBuilder<WechatRobotDbContext>()
            .UseInMemoryDatabase($"knowledge-tags-{Guid.NewGuid():N}")
            .Options;
        return new WechatRobotDbContext(options);
    }
}
