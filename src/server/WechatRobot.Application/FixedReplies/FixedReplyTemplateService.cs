namespace WechatRobot.Application.FixedReplies;

public sealed class FixedReplyTemplateService(
    IFixedReplyTemplateStore store,
    TimeProvider timeProvider)
{
    public Task<IReadOnlyList<FixedReplyTemplateView>> ListAsync(
        FixedReplyTemplateQuery query,
        CancellationToken cancellationToken = default) =>
        store.ListAsync(query with
        {
            Skip = Math.Max(0, query.Skip),
            Take = Math.Clamp(query.Take, 1, 200)
        }, cancellationToken);

    public Task<FixedReplyTemplateView?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        store.GetAsync(id, cancellationToken);

    public Task<FixedReplyTemplateView> CreateAsync(
        FixedReplyTemplateDraft draft,
        Guid actorUserId,
        CancellationToken cancellationToken = default) =>
        store.CreateAsync(
            Validate(draft),
            actorUserId,
            timeProvider.GetUtcNow().UtcDateTime,
            cancellationToken);

    public Task<FixedReplyTemplateView> UpdateAsync(
        Guid id,
        int expectedVersion,
        FixedReplyTemplateDraft draft,
        Guid actorUserId,
        CancellationToken cancellationToken = default) =>
        store.UpdateAsync(
            id,
            expectedVersion,
            Validate(draft),
            actorUserId,
            timeProvider.GetUtcNow().UtcDateTime,
            cancellationToken);

    public Task<FixedReplyTemplateView> SetEnabledAsync(
        Guid id,
        int expectedVersion,
        bool enabled,
        Guid actorUserId,
        CancellationToken cancellationToken = default) =>
        store.SetEnabledAsync(
            id,
            expectedVersion,
            enabled,
            actorUserId,
            timeProvider.GetUtcNow().UtcDateTime,
            cancellationToken);

    public Task DeleteAsync(
        Guid id,
        int expectedVersion,
        Guid actorUserId,
        CancellationToken cancellationToken = default) =>
        store.DeleteAsync(
            id,
            expectedVersion,
            actorUserId,
            timeProvider.GetUtcNow().UtcDateTime,
            cancellationToken);

    public Task<IReadOnlyList<EffectiveFixedReply>> ListEffectiveAsync(
        Guid groupProfileId,
        int maximumCandidates = 24,
        int examplesPerTemplate = 5,
        CancellationToken cancellationToken = default) =>
        store.ListEffectiveAsync(
            groupProfileId,
            Math.Clamp(maximumCandidates, 1, 64),
            Math.Clamp(examplesPerTemplate, 1, 10),
            cancellationToken);

    public Task<ResolvedFixedReply?> ResolveAsync(
        Guid templateId,
        int expectedVersion,
        Guid groupProfileId,
        CancellationToken cancellationToken = default) =>
        store.ResolveAsync(
            templateId,
            expectedVersion,
            groupProfileId,
            cancellationToken);

    private static ValidatedFixedReplyTemplate Validate(FixedReplyTemplateDraft draft)
    {
        var name = Required(draft.Name, 128, "fixed_reply_name_required");
        var intent = Required(
            draft.IntentDescription,
            1000,
            "fixed_reply_intent_required");
        var reply = Required(draft.ReplyText, 4000, "fixed_reply_text_required");
        if (draft.Priority is < -1000 or > 1000)
        {
            throw new FixedReplyValidationException(
                "fixed_reply_priority_invalid",
                "Priority must be between -1000 and 1000.");
        }

        var examples = draft.Examples
            .Select(NormalizeWhitespace)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (examples.Length == 0)
        {
            throw new FixedReplyValidationException(
                "fixed_reply_example_required",
                "At least one example is required.");
        }
        if (examples.Length > 20 || examples.Any(value => value.Length > 500))
        {
            throw new FixedReplyValidationException(
                "fixed_reply_examples_invalid",
                "Examples exceed the supported limits.");
        }

        var rules = draft.GroupRules
            .DistinctBy(rule => rule.GroupProfileId)
            .ToArray();
        if (rules.Length != draft.GroupRules.Count)
        {
            throw new FixedReplyValidationException(
                "fixed_reply_group_duplicate",
                "A group can be listed only once.");
        }
        if (draft.ScopeType == FixedReplyScopeType.Global
            && rules.Any(rule => rule.Effect != FixedReplyGroupEffect.Exclude))
        {
            throw new FixedReplyValidationException(
                "fixed_reply_global_include_forbidden",
                "Global templates accept only excluded groups.");
        }
        if (draft.ScopeType == FixedReplyScopeType.SelectedGroups
            && (rules.Length == 0
                || rules.Any(rule => rule.Effect != FixedReplyGroupEffect.Include)))
        {
            throw new FixedReplyValidationException(
                "fixed_reply_group_required",
                "Selected-group templates require one or more included groups.");
        }

        return new ValidatedFixedReplyTemplate(
            name,
            intent,
            reply,
            draft.ScopeType,
            draft.Priority,
            draft.IsEnabled,
            examples,
            rules);
    }

    private static string Required(string value, int maximum, string code)
    {
        var normalized = NormalizeWhitespace(value);
        if (normalized.Length == 0 || normalized.Length > maximum)
        {
            throw new FixedReplyValidationException(code, "Required value is invalid.");
        }
        return normalized;
    }

    private static string NormalizeWhitespace(string value) =>
        string.Join(
            ' ',
            (value ?? string.Empty).Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries));
}
