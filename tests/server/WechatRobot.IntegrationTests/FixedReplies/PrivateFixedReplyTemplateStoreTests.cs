using Microsoft.EntityFrameworkCore;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.IntegrationTests.FixedReplies;

public sealed class PrivateFixedReplyTemplateStoreTests
{
    [Fact]
    public async Task Private_scope_lists_every_enabled_template_without_applying_group_rules()
    {
        await using var database = Database();
        var groupId = Guid.NewGuid();
        var inactiveGroupTemplate = Template("停用群专用", "SelectedGroups", 40);
        var selectedTemplate = Template("指定群", "SelectedGroups", 30);
        var globalTemplate = Template("全局", "Global", 20);
        var excludedGlobalTemplate = Template("被群排除的全局", "Global", 10);
        var disabledTemplate = Template("停用", "Global", 50, isEnabled: false);
        var deletedTemplate = Template(
            "已删除",
            "SelectedGroups",
            60,
            deletedAtUtc: DateTime.UtcNow);
        database.FixedReplyTemplates.AddRange(
            inactiveGroupTemplate,
            selectedTemplate,
            globalTemplate,
            excludedGlobalTemplate,
            disabledTemplate,
            deletedTemplate);
        database.FixedReplyTemplateGroupRules.AddRange(
            new FixedReplyTemplateGroupRuleEntity
            {
                TemplateId = inactiveGroupTemplate.Id,
                GroupProfileId = Guid.NewGuid(),
                Effect = "Include"
            },
            new FixedReplyTemplateGroupRuleEntity
            {
                TemplateId = selectedTemplate.Id,
                GroupProfileId = groupId,
                Effect = "Include"
            },
            new FixedReplyTemplateGroupRuleEntity
            {
                TemplateId = excludedGlobalTemplate.Id,
                GroupProfileId = groupId,
                Effect = "Exclude"
            });
        foreach (var template in database.FixedReplyTemplates.Local.ToArray())
        {
            database.FixedReplyTemplateExamples.AddRange(
                Example(template.Id, "示例一"),
                Example(template.Id, "示例二"));
        }
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        var store = new FixedReplyTemplateStore(database);

        var candidates = await store.ListEffectiveForPrivateAsync(
            64,
            1,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                inactiveGroupTemplate.Id,
                selectedTemplate.Id,
                globalTemplate.Id,
                excludedGlobalTemplate.Id
            ],
            candidates.Select(item => item.Id));
        Assert.All(candidates, item => Assert.Single(item.Examples));
        Assert.DoesNotContain(candidates, item => item.Id == disabledTemplate.Id);
        Assert.DoesNotContain(candidates, item => item.Id == deletedTemplate.Id);

        Assert.NotNull(await store.ResolveForPrivateAsync(
            selectedTemplate.Id,
            selectedTemplate.Version,
            TestContext.Current.CancellationToken));
        Assert.Null(await store.ResolveForPrivateAsync(
            selectedTemplate.Id,
            selectedTemplate.Version + 1,
            TestContext.Current.CancellationToken));
        Assert.Null(await store.ResolveForPrivateAsync(
            disabledTemplate.Id,
            disabledTemplate.Version,
            TestContext.Current.CancellationToken));
        Assert.Null(await store.ResolveForPrivateAsync(
            deletedTemplate.Id,
            deletedTemplate.Version,
            TestContext.Current.CancellationToken));
    }

    private static FixedReplyTemplateEntity Template(
        string name,
        string scope,
        int priority,
        bool isEnabled = true,
        DateTime? deletedAtUtc = null) =>
        new()
        {
            Name = name,
            NormalizedName = name.ToUpperInvariant(),
            IntentDescription = $"{name}意图",
            ReplyText = $"{name}回复",
            ScopeType = scope,
            Priority = priority,
            IsEnabled = isEnabled,
            Version = 3,
            DeletedAtUtc = deletedAtUtc
        };

    private static FixedReplyTemplateExampleEntity Example(
        Guid templateId,
        string text) =>
        new()
        {
            TemplateId = templateId,
            ExampleText = text,
            NormalizedText = $"{text}-{Guid.NewGuid():N}"
        };

    private static WechatRobotDbContext Database() =>
        new(new DbContextOptionsBuilder<WechatRobotDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
}
