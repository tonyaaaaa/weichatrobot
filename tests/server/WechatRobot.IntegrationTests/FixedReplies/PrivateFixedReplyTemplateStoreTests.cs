using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using WechatRobot.Application.FixedReplies;
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

    [Fact]
    public async Task Group_scope_loads_examples_without_runtime_guid_contains()
    {
        await using var database = Database();
        var group = new GroupProfileEntity { Name = "价格咨询群" };
        var selected = Template("群价格回复", "SelectedGroups", 30);
        var global = Template("全局价格回复", "Global", 20);
        database.AddRange(group, selected, global);
        database.FixedReplyTemplateGroupRules.Add(
            new FixedReplyTemplateGroupRuleEntity
            {
                TemplateId = selected.Id,
                GroupProfileId = group.Id,
                Effect = "Include"
            });
        database.FixedReplyTemplateExamples.AddRange(
            Example(selected.Id, "签证多少钱？"),
            Example(global.Id, "费用是多少？"));
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        var store = new FixedReplyTemplateStore(database);

        var candidates = await store.ListEffectiveAsync(
            group.Id,
            64,
            1,
            TestContext.Current.CancellationToken);

        Assert.Equal([selected.Id, global.Id], candidates.Select(item => item.Id));
        Assert.Equal("签证多少钱？", candidates[0].Examples.Single());
        Assert.Equal("费用是多少？", candidates[1].Examples.Single());
    }

    [Fact]
    public async Task Administration_list_loads_examples_and_rules_without_runtime_guid_contains()
    {
        await using var database = Database();
        var groupId = Guid.NewGuid();
        var selected = Template("指定群价格回复", "SelectedGroups", 30);
        var global = Template("全局价格回复", "Global", 20);
        database.FixedReplyTemplates.AddRange(selected, global);
        database.FixedReplyTemplateExamples.AddRange(
            Example(selected.Id, "签证多少钱？"),
            Example(global.Id, "费用是多少？"));
        database.FixedReplyTemplateGroupRules.Add(
            new FixedReplyTemplateGroupRuleEntity
            {
                TemplateId = selected.Id,
                GroupProfileId = groupId,
                Effect = "Include"
            });
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        var store = new FixedReplyTemplateStore(database);

        var views = await store.ListAsync(
            new FixedReplyTemplateQuery(),
            TestContext.Current.CancellationToken);

        Assert.Equal([selected.Id, global.Id], views.Select(item => item.Id));
        Assert.Equal("签证多少钱？", views[0].Examples.Single());
        var rule = Assert.Single(views[0].GroupRules);
        Assert.Equal(groupId, rule.GroupProfileId);
        Assert.Equal(FixedReplyGroupEffect.Include, rule.Effect);
        Assert.Equal("费用是多少？", views[1].Examples.Single());
        Assert.Empty(views[1].GroupRules);
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
            .AddInterceptors(new RejectRuntimeGuidContainsInterceptor())
            .Options);

    private sealed class RejectRuntimeGuidContainsInterceptor
        : IQueryExpressionInterceptor
    {
        public Expression QueryCompilationStarting(
            Expression queryExpression,
            QueryExpressionEventData eventData)
        {
            new RuntimeGuidContainsVisitor().Visit(queryExpression);
            return queryExpression;
        }

        private sealed class RuntimeGuidContainsVisitor : ExpressionVisitor
        {
            protected override Expression VisitMethodCall(MethodCallExpression node)
            {
                if (node.Method.Name == nameof(Enumerable.Contains)
                    && node.Method.IsGenericMethod
                    && node.Method.GetGenericArguments() is [var elementType]
                    && elementType == typeof(Guid))
                {
                    throw new InvalidOperationException(
                        "runtime_guid_contains_not_supported");
                }

                return base.VisitMethodCall(node);
            }
        }
    }
}
