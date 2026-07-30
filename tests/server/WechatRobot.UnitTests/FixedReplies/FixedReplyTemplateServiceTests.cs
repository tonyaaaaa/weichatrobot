using WechatRobot.Application.FixedReplies;

namespace WechatRobot.UnitTests.FixedReplies;

public sealed class FixedReplyTemplateServiceTests
{
    [Fact]
    public async Task Global_templates_accept_only_exclusions()
    {
        var service = new FixedReplyTemplateService(new RecordingStore(), TimeProvider.System);
        var draft = Draft(
            FixedReplyScopeType.Global,
            [new FixedReplyGroupRuleInput(Guid.NewGuid(), FixedReplyGroupEffect.Include)]);

        var exception = await Assert.ThrowsAsync<FixedReplyValidationException>(() =>
            service.CreateAsync(draft, Guid.NewGuid(), TestContext.Current.CancellationToken));

        Assert.Equal("fixed_reply_global_include_forbidden", exception.Code);
    }

    [Fact]
    public async Task Selected_group_templates_require_at_least_one_include()
    {
        var service = new FixedReplyTemplateService(new RecordingStore(), TimeProvider.System);

        var exception = await Assert.ThrowsAsync<FixedReplyValidationException>(() =>
            service.CreateAsync(
                Draft(FixedReplyScopeType.SelectedGroups, []),
                Guid.NewGuid(),
                TestContext.Current.CancellationToken));

        Assert.Equal("fixed_reply_group_required", exception.Code);
    }

    [Fact]
    public async Task Create_normalizes_examples_and_delegates_validated_data()
    {
        var store = new RecordingStore();
        var service = new FixedReplyTemplateService(store, TimeProvider.System);
        var groupId = Guid.NewGuid();

        await service.CreateAsync(
            Draft(
                FixedReplyScopeType.SelectedGroups,
                [new FixedReplyGroupRuleInput(groupId, FixedReplyGroupEffect.Include)],
                ["  签证   还有多久出来？ ", "签证什么时候下来？"]),
            Guid.NewGuid(),
            TestContext.Current.CancellationToken);

        Assert.NotNull(store.Created);
        Assert.Equal(["签证 还有多久出来？", "签证什么时候下来？"], store.Created.Examples);
        Assert.Equal(groupId, Assert.Single(store.Created.GroupRules).GroupProfileId);
    }

    private static FixedReplyTemplateDraft Draft(
        FixedReplyScopeType scope,
        IReadOnlyList<FixedReplyGroupRuleInput> rules,
        IReadOnlyList<string>? examples = null) =>
        new(
            "签证进度",
            "询问已经提交的签证何时出结果",
            "签证进度请以顾问最新通知为准。",
            scope,
            100,
            true,
            examples ?? ["签证还有多久出来？"],
            rules);

    private sealed class RecordingStore : IFixedReplyTemplateStore
    {
        public ValidatedFixedReplyTemplate? Created { get; private set; }

        public Task<FixedReplyTemplateView> CreateAsync(
            ValidatedFixedReplyTemplate template,
            Guid actorUserId,
            DateTime nowUtc,
            CancellationToken cancellationToken)
        {
            Created = template;
            return Task.FromResult(View(template, actorUserId, nowUtc));
        }

        public Task<FixedReplyTemplateView?> GetAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<FixedReplyTemplateView?>(null);

        public Task<IReadOnlyList<FixedReplyTemplateView>> ListAsync(
            FixedReplyTemplateQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FixedReplyTemplateView>>([]);

        public Task<FixedReplyTemplateView> UpdateAsync(
            Guid id,
            int expectedVersion,
            ValidatedFixedReplyTemplate template,
            Guid actorUserId,
            DateTime nowUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<FixedReplyTemplateView> SetEnabledAsync(
            Guid id,
            int expectedVersion,
            bool enabled,
            Guid actorUserId,
            DateTime nowUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAsync(
            Guid id,
            int expectedVersion,
            Guid actorUserId,
            DateTime nowUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<EffectiveFixedReply>> ListEffectiveAsync(
            Guid groupProfileId,
            int maximumCandidates,
            int examplesPerTemplate,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EffectiveFixedReply>>([]);

        public Task<ResolvedFixedReply?> ResolveAsync(
            Guid templateId,
            int expectedVersion,
            Guid groupProfileId,
            CancellationToken cancellationToken) =>
            Task.FromResult<ResolvedFixedReply?>(null);

        private static FixedReplyTemplateView View(
            ValidatedFixedReplyTemplate template,
            Guid actor,
            DateTime now) =>
            new(
                Guid.NewGuid(),
                template.Name,
                template.IntentDescription,
                template.ReplyText,
                template.ScopeType,
                template.Priority,
                template.IsEnabled,
                0,
                template.Examples,
                template.GroupRules,
                actor,
                actor,
                now,
                now,
                null);
    }
}
