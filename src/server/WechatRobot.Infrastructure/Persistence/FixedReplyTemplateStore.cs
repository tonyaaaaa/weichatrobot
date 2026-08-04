using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.FixedReplies;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.Infrastructure.Persistence;

public sealed class FixedReplyTemplateStore(WechatRobotDbContext database)
    : IFixedReplyTemplateStore
{
    public async Task<IReadOnlyList<FixedReplyTemplateView>> ListAsync(
        FixedReplyTemplateQuery query,
        CancellationToken cancellationToken)
    {
        var templates = database.FixedReplyTemplates
            .AsNoTracking()
            .Where(item => item.DeletedAtUtc == null);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            templates = templates.Where(item =>
                item.Name.Contains(search)
                || item.IntentDescription.Contains(search));
        }
        if (query.ScopeType is { } scope)
        {
            var value = Scope(scope);
            templates = templates.Where(item => item.ScopeType == value);
        }
        if (query.IsEnabled is { } enabled)
        {
            templates = templates.Where(item => item.IsEnabled == enabled);
        }
        if (query.GroupProfileId is { } groupId)
        {
            var globalScope = Scope(FixedReplyScopeType.Global);
            templates = templates.Where(item =>
                item.ScopeType == globalScope
                || database.FixedReplyTemplateGroupRules.Any(rule =>
                    rule.TemplateId == item.Id
                    && rule.GroupProfileId == groupId));
        }

        var entities = await templates
            .OrderByDescending(item => item.Priority)
            .ThenBy(item => item.Id)
            .Skip(query.Skip)
            .Take(query.Take)
            .ToListAsync(cancellationToken);
        return await ViewsAsync(entities, cancellationToken);
    }

    public async Task<FixedReplyTemplateView?> GetAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var entity = await database.FixedReplyTemplates
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.Id == id && item.DeletedAtUtc == null,
                cancellationToken);
        return entity is null
            ? null
            : (await ViewsAsync([entity], cancellationToken))[0];
    }

    public async Task<FixedReplyTemplateView> CreateAsync(
        ValidatedFixedReplyTemplate template,
        Guid actorUserId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var entity = new FixedReplyTemplateEntity
        {
            Name = template.Name,
            NormalizedName = Normalize(template.Name),
            IntentDescription = template.IntentDescription,
            ReplyText = template.ReplyText,
            ScopeType = Scope(template.ScopeType),
            Priority = template.Priority,
            IsEnabled = template.IsEnabled,
            CreatedByUserId = actorUserId,
            UpdatedByUserId = actorUserId,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc
        };
        database.FixedReplyTemplates.Add(entity);
        ReplaceChildren(entity.Id, template, actorUserId, nowUtc);
        Audit("fixed_reply_template.created", entity.Id, actorUserId, nowUtc);
        await database.SaveChangesAsync(cancellationToken);
        return (await ViewsAsync([entity], cancellationToken))[0];
    }

    public async Task<FixedReplyTemplateView> UpdateAsync(
        Guid id,
        int expectedVersion,
        ValidatedFixedReplyTemplate template,
        Guid actorUserId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var entity = await RequiredAsync(id, cancellationToken);
        VerifyVersion(entity, expectedVersion);
        entity.Name = template.Name;
        entity.NormalizedName = Normalize(template.Name);
        entity.IntentDescription = template.IntentDescription;
        entity.ReplyText = template.ReplyText;
        entity.ScopeType = Scope(template.ScopeType);
        entity.Priority = template.Priority;
        entity.IsEnabled = template.IsEnabled;
        entity.UpdatedByUserId = actorUserId;
        entity.UpdatedAtUtc = nowUtc;
        entity.Version++;
        var examples = await database.FixedReplyTemplateExamples
            .Where(item => item.TemplateId == id)
            .ToListAsync(cancellationToken);
        var rules = await database.FixedReplyTemplateGroupRules
            .Where(item => item.TemplateId == id)
            .ToListAsync(cancellationToken);
        database.RemoveRange(examples);
        database.RemoveRange(rules);
        ReplaceChildren(id, template, actorUserId, nowUtc);
        Audit("fixed_reply_template.updated", id, actorUserId, nowUtc);
        await SaveConcurrencyAsync(cancellationToken);
        return (await ViewsAsync([entity], cancellationToken))[0];
    }

    public async Task<FixedReplyTemplateView> SetEnabledAsync(
        Guid id,
        int expectedVersion,
        bool enabled,
        Guid actorUserId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var entity = await RequiredAsync(id, cancellationToken);
        VerifyVersion(entity, expectedVersion);
        entity.IsEnabled = enabled;
        entity.Version++;
        entity.UpdatedByUserId = actorUserId;
        entity.UpdatedAtUtc = nowUtc;
        Audit(
            enabled
                ? "fixed_reply_template.enabled"
                : "fixed_reply_template.disabled",
            id,
            actorUserId,
            nowUtc);
        await SaveConcurrencyAsync(cancellationToken);
        return (await ViewsAsync([entity], cancellationToken))[0];
    }

    public async Task DeleteAsync(
        Guid id,
        int expectedVersion,
        Guid actorUserId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var entity = await RequiredAsync(id, cancellationToken);
        VerifyVersion(entity, expectedVersion);
        entity.IsEnabled = false;
        entity.DeletedAtUtc = nowUtc;
        entity.Version++;
        entity.UpdatedByUserId = actorUserId;
        entity.UpdatedAtUtc = nowUtc;
        Audit("fixed_reply_template.deleted", id, actorUserId, nowUtc);
        await SaveConcurrencyAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<EffectiveFixedReply>> ListEffectiveAsync(
        Guid groupProfileId,
        int maximumCandidates,
        int examplesPerTemplate,
        CancellationToken cancellationToken)
    {
        if (!await GroupIsActiveAsync(groupProfileId, cancellationToken))
        {
            return [];
        }
        var rules = database.FixedReplyTemplateGroupRules;
        var templates = await database.FixedReplyTemplates
            .AsNoTracking()
            .Where(item =>
                item.IsEnabled
                && item.DeletedAtUtc == null
                && ((item.ScopeType == "SelectedGroups"
                     && rules.Any(rule =>
                         rule.TemplateId == item.Id
                         && rule.GroupProfileId == groupProfileId
                         && rule.Effect == "Include"))
                    || (item.ScopeType == "Global"
                        && !rules.Any(rule =>
                            rule.TemplateId == item.Id
                            && rule.GroupProfileId == groupProfileId
                            && rule.Effect == "Exclude"))))
            .OrderByDescending(item => item.ScopeType == "SelectedGroups")
            .ThenByDescending(item => item.Priority)
            .ThenBy(item => item.Id)
            .Take(maximumCandidates)
            .ToListAsync(cancellationToken);
        var ids = templates.Select(item => item.Id).ToArray();
        var examples = await LoadExamplesAsync(ids, cancellationToken);
        return templates.Select(item => new EffectiveFixedReply(
            item.Id,
            item.Version,
            item.Name,
            item.IntentDescription,
            examples.Where(example => example.TemplateId == item.Id)
                .Take(examplesPerTemplate)
                .Select(example => example.ExampleText)
                .ToArray(),
            item.Priority,
            item.ScopeType == "SelectedGroups")).ToArray();
    }

    public async Task<ResolvedFixedReply?> ResolveAsync(
        Guid templateId,
        int expectedVersion,
        Guid groupProfileId,
        CancellationToken cancellationToken)
    {
        if (!await GroupIsActiveAsync(groupProfileId, cancellationToken))
        {
            return null;
        }
        var candidate = (await ListEffectiveAsync(
                groupProfileId,
                64,
                1,
                cancellationToken))
            .SingleOrDefault(item => item.Id == templateId);
        if (candidate is null || candidate.Version != expectedVersion)
        {
            return null;
        }
        var entity = await database.FixedReplyTemplates
            .AsNoTracking()
            .SingleAsync(item => item.Id == templateId, cancellationToken);
        return new ResolvedFixedReply(
            entity.Id,
            entity.Version,
            entity.Name,
            entity.ReplyText,
            entity.ScopeType == "SelectedGroups");
    }

    public async Task<IReadOnlyList<EffectiveFixedReply>> ListEffectiveForPrivateAsync(
        int maximumCandidates,
        int examplesPerTemplate,
        CancellationToken cancellationToken)
    {
        var templates = await database.FixedReplyTemplates
            .AsNoTracking()
            .Where(item => item.IsEnabled && item.DeletedAtUtc == null)
            .OrderByDescending(item => item.Priority)
            .ThenBy(item => item.Id)
            .Take(maximumCandidates)
            .ToListAsync(cancellationToken);
        var ids = templates.Select(item => item.Id).ToArray();
        var examples = await LoadExamplesAsync(ids, cancellationToken);
        return templates.Select(item => new EffectiveFixedReply(
            item.Id,
            item.Version,
            item.Name,
            item.IntentDescription,
            examples.Where(example => example.TemplateId == item.Id)
                .Take(examplesPerTemplate)
                .Select(example => example.ExampleText)
                .ToArray(),
            item.Priority,
            item.ScopeType == "SelectedGroups")).ToArray();
    }

    public async Task<ResolvedFixedReply?> ResolveForPrivateAsync(
        Guid templateId,
        int expectedVersion,
        CancellationToken cancellationToken)
    {
        var entity = await database.FixedReplyTemplates
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item =>
                    item.Id == templateId
                    && item.Version == expectedVersion
                    && item.IsEnabled
                    && item.DeletedAtUtc == null,
                cancellationToken);
        return entity is null
            ? null
            : new ResolvedFixedReply(
                entity.Id,
                entity.Version,
                entity.Name,
                entity.ReplyText,
                entity.ScopeType == "SelectedGroups");
    }

    private async Task<IReadOnlyList<FixedReplyTemplateView>> ViewsAsync(
        IReadOnlyList<FixedReplyTemplateEntity> entities,
        CancellationToken cancellationToken)
    {
        if (entities.Count == 0)
        {
            return [];
        }
        var ids = entities.Select(item => item.Id).ToArray();
        var examples = await LoadExamplesAsync(ids, cancellationToken);
        var rules = await LoadGroupRulesAsync(ids, cancellationToken);
        return entities.Select(item => new FixedReplyTemplateView(
            item.Id,
            item.Name,
            item.IntentDescription,
            item.ReplyText,
            ParseScope(item.ScopeType),
            item.Priority,
            item.IsEnabled,
            item.Version,
            examples.Where(example => example.TemplateId == item.Id)
                .Select(example => example.ExampleText)
                .ToArray(),
            rules.Where(rule => rule.TemplateId == item.Id)
                .Select(rule => new FixedReplyGroupRuleInput(
                    rule.GroupProfileId,
                    Enum.Parse<FixedReplyGroupEffect>(rule.Effect)))
                .ToArray(),
            item.CreatedByUserId,
            item.UpdatedByUserId,
            item.CreatedAtUtc,
            item.UpdatedAtUtc,
            item.DeletedAtUtc)).ToArray();
    }

    private async Task<IReadOnlyList<FixedReplyTemplateExampleEntity>>
        LoadExamplesAsync(
            IReadOnlyCollection<Guid> templateIds,
            CancellationToken cancellationToken)
    {
        var examples = new List<FixedReplyTemplateExampleEntity>();
        foreach (var batch in GuidBatchQuery.CreateBatches(templateIds))
        {
            var predicate = GuidBatchQuery
                .BuildPredicate<FixedReplyTemplateExampleEntity>(
                    batch,
                    item => item.TemplateId);
            examples.AddRange(await database.FixedReplyTemplateExamples
                .AsNoTracking()
                .Where(predicate)
                .OrderBy(item => item.Id)
                .ToArrayAsync(cancellationToken));
        }

        return examples.OrderBy(item => item.Id).ToArray();
    }

    private async Task<IReadOnlyList<FixedReplyTemplateGroupRuleEntity>>
        LoadGroupRulesAsync(
            IReadOnlyCollection<Guid> templateIds,
            CancellationToken cancellationToken)
    {
        var rules = new List<FixedReplyTemplateGroupRuleEntity>();
        foreach (var batch in GuidBatchQuery.CreateBatches(templateIds))
        {
            var predicate = GuidBatchQuery
                .BuildPredicate<FixedReplyTemplateGroupRuleEntity>(
                    batch,
                    item => item.TemplateId);
            rules.AddRange(await database.FixedReplyTemplateGroupRules
                .AsNoTracking()
                .Where(predicate)
                .OrderBy(item => item.GroupProfileId)
                .ToArrayAsync(cancellationToken));
        }

        return rules.OrderBy(item => item.GroupProfileId).ToArray();
    }

    private void ReplaceChildren(
        Guid templateId,
        ValidatedFixedReplyTemplate template,
        Guid actorUserId,
        DateTime nowUtc)
    {
        database.FixedReplyTemplateExamples.AddRange(
            template.Examples.Select(example => new FixedReplyTemplateExampleEntity
            {
                TemplateId = templateId,
                ExampleText = example,
                NormalizedText = Normalize(example),
                CreatedAtUtc = nowUtc
            }));
        database.FixedReplyTemplateGroupRules.AddRange(
            template.GroupRules.Select(rule => new FixedReplyTemplateGroupRuleEntity
            {
                TemplateId = templateId,
                GroupProfileId = rule.GroupProfileId,
                Effect = rule.Effect.ToString(),
                CreatedByUserId = actorUserId,
                CreatedAtUtc = nowUtc
            }));
    }

    private async Task<FixedReplyTemplateEntity> RequiredAsync(
        Guid id,
        CancellationToken cancellationToken) =>
        await database.FixedReplyTemplates.SingleOrDefaultAsync(
            item => item.Id == id && item.DeletedAtUtc == null,
            cancellationToken)
        ?? throw new KeyNotFoundException("Fixed reply template was not found.");

    private async Task<bool> GroupIsActiveAsync(
        Guid id,
        CancellationToken cancellationToken) =>
        await database.GroupProfiles.AsNoTracking().AnyAsync(
            group => group.Id == id
                     && group.IsEnabled
                     && group.ArchivedAtUtc == null,
            cancellationToken);

    private static void VerifyVersion(
        FixedReplyTemplateEntity entity,
        int expectedVersion)
    {
        if (entity.Version != expectedVersion)
        {
            throw new FixedReplyConcurrencyException();
        }
    }

    private async Task SaveConcurrencyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new FixedReplyConcurrencyException();
        }
    }

    private void Audit(
        string action,
        Guid id,
        Guid actorUserId,
        DateTime nowUtc) =>
        database.AdministrationAudits.Add(new AdministrationAuditEntity
        {
            Actor = actorUserId.ToString("D"),
            Action = action,
            TargetType = "fixed_reply_template",
            TargetId = id.ToString("D"),
            SanitizedDetailJson = "{}",
            CreatedAtUtc = nowUtc
        });

    private static string Normalize(string value) =>
        value.Trim().ToUpperInvariant();

    private static string Scope(FixedReplyScopeType value) => value.ToString();

    private static FixedReplyScopeType ParseScope(string value) =>
        Enum.Parse<FixedReplyScopeType>(value);
}
