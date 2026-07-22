using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Groups;
using WechatRobot.Domain.Groups;
using WechatRobot.Domain.Knowledge;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.Api.Groups;

public static class GroupEndpoints
{
    private const string AnyBoundTagOrGlobalPublic = "any-bound-tag-or-global-public";
    private const int MaximumRulesPerKind = 50;
    private const int MaximumPreviewGroupNames = 100;

    public static IEndpointRouteBuilder MapGroupEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var groups = endpoints.MapGroup("/api/groups").RequireAuthorization(SystemRoles.Admin);
        groups.MapGet("{id:guid}/configuration", GetConfigurationAsync);
        groups.MapPut("{id:guid}/configuration", UpdateConfigurationAsync);
        endpoints.MapPost("/api/group-rules/preview", PreviewAsync).RequireAuthorization(SystemRoles.Admin);
        return endpoints;
    }

    private static async Task<IResult> GetConfigurationAsync(Guid id, WechatRobotDbContext database, GroupConfigurationService service, CancellationToken cancellationToken)
    {
        var group = await database.GroupProfiles.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        return group is null ? Results.NotFound() : Results.Ok(await ToResponseAsync(group, database, service, 0, cancellationToken));
    }

    private static async Task<IResult> UpdateConfigurationAsync(
        Guid id,
        UpdateGroupConfigurationRequest request,
        WechatRobotDbContext database,
        GroupConfigurationService service,
        CancellationToken cancellationToken)
    {
        if (request.IncludeRules.Count > MaximumRulesPerKind || request.ExcludeRules.Count > MaximumRulesPerKind || request.BoundTagIds.Count > 100)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["configuration"] = ["Too many rules or tags."] });
        }

        var include = ToRules(request.IncludeRules);
        var exclude = ToRules(request.ExcludeRules);
        if (include is null || exclude is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["rules"] = ["Rule pattern kind must be exact, contains, or regex."] });
        }

        var context = ToContext(request.Context);
        var validation = service.Validate(context, include, exclude);
        if (!validation.IsValid)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["configuration"] = [validation.Error!] });
        }

        var group = await database.GroupProfiles.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (group is null) return Results.NotFound();

        var selectedTagIds = request.BoundTagIds.Distinct().ToArray();
        foreach (var tagId in selectedTagIds)
        {
            if (!await database.KnowledgeTags.AnyAsync(tag => tag.IsEnabled && tag.Id == tagId, cancellationToken))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["boundTagIds"] = ["Only existing enabled tags can be bound to a group."] });
            }
        }

        var existingRules = database.GroupRules.Where(rule => rule.GroupProfileId == id);
        database.GroupRules.RemoveRange(existingRules);
        database.GroupRules.AddRange(ToEntities(id, include, GroupRuleDirection.Include).Concat(ToEntities(id, exclude, GroupRuleDirection.Exclude)));
        var existingTags = database.GroupProfileTags.Where(binding => binding.GroupProfileId == id);
        database.GroupProfileTags.RemoveRange(existingTags);
        database.GroupProfileTags.AddRange(selectedTagIds.Select(tagId => new GroupProfileTagEntity { GroupProfileId = id, KnowledgeTagId = tagId }));

        ApplyContext(group, context);
        group.UpdatedAtUtc = DateTime.UtcNow;
        var clearedMessages = 0;
        if (request.ClearContext)
        {
            var messages = await database.ConversationMessages.Where(message => message.GroupProfileId == id).ToListAsync(cancellationToken);
            clearedMessages = messages.Count;
            database.ConversationMessages.RemoveRange(messages);
        }

        await database.SaveChangesAsync(cancellationToken);
        return Results.Ok(await ToResponseAsync(group, database, service, clearedMessages, cancellationToken));
    }

    private static IResult PreviewAsync(PreviewGroupRulesRequest request, GroupConfigurationService service)
    {
        if (request.GroupNames.Count > MaximumPreviewGroupNames || request.GroupNames.Any(name => string.IsNullOrWhiteSpace(name) || name.Length > 256) ||
            request.IncludeRules.Count > MaximumRulesPerKind || request.ExcludeRules.Count > MaximumRulesPerKind)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["preview"] = ["Preview input is out of range."] });
        }

        var include = ToRules(request.IncludeRules);
        var exclude = ToRules(request.ExcludeRules);
        if (include is null || exclude is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["rules"] = ["Rule pattern kind must be exact, contains, or regex."] });
        }

        var validation = service.Validate(new GroupContextOverrides(null, null, null, null, null, null), include, exclude);
        if (!validation.IsValid)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["rules"] = [validation.Error!] });
        }

        var results = new List<GroupRulePreviewResult>(request.GroupNames.Count);
        foreach (var groupName in request.GroupNames)
        {
            var result = service.Preview(include, exclude, groupName);
            if (!result.IsValid)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["rules"] = [result.ValidationError!] });
            }

            results.Add(new GroupRulePreviewResult(groupName, result.IsMatch, result.IsExcluded));
        }

        return Results.Ok(new PreviewGroupRulesResponse(results));
    }

    private static async Task<GroupConfigurationResponse> ToResponseAsync(GroupProfileEntity group, WechatRobotDbContext database, GroupConfigurationService service, int clearedMessages, CancellationToken cancellationToken)
    {
        var rules = await database.GroupRules.AsNoTracking().Where(rule => rule.GroupProfileId == group.Id).OrderBy(rule => rule.CreatedAtUtc).ThenBy(rule => rule.Id).ToArrayAsync(cancellationToken);
        var boundTagIds = await database.GroupProfileTags.AsNoTracking().Where(binding => binding.GroupProfileId == group.Id).Select(binding => binding.KnowledgeTagId).ToArrayAsync(cancellationToken);
        var tags = await database.KnowledgeTags.AsNoTracking().OrderBy(tag => tag.Name).ToArrayAsync(cancellationToken);
        var domainTags = tags.Select(tag => new KnowledgeTag(tag.Id, tag.Name, tag.IsEnabled, tag.IsGlobalPublic)).ToArray();
        var configured = ToConfiguredContext(group);
        return new GroupConfigurationResponse(
            group.Id,
            group.Name,
            new GroupRulesResponse(
                rules.Where(rule => rule.RuleKind == (int)GroupRuleDirection.Include).Select(ToRuleResponse).ToArray(),
                rules.Where(rule => rule.RuleKind == (int)GroupRuleDirection.Exclude).Select(ToRuleResponse)
                    .Concat(rules.Where(rule => rule.RuleKind == (int)GroupRuleDirection.Include && !string.IsNullOrWhiteSpace(rule.ExcludePattern)).Select(ToLegacyExcludeResponse)).ToArray()),
            boundTagIds,
            service.ResolveVisibleTagIds(boundTagIds, domainTags).OrderBy(tagId => tagId).ToArray(),
            tags.Where(tag => tag.IsEnabled || boundTagIds.Contains(tag.Id))
                .Select(tag => new KnowledgeTagResponse(tag.Id, tag.Name, tag.IsGlobalPublic, tag.IsEnabled, boundTagIds.Contains(tag.Id))).ToArray(),
            AnyBoundTagOrGlobalPublic,
            new GroupContextResponse(configured, service.GetEffectiveContext(configured)),
            clearedMessages);
    }

    private static GroupContextOverrides ToConfiguredContext(GroupProfileEntity group) => new(group.ContextSenderIsolated, group.ContextHistoryTurns,
        group.ContextIdleTimeoutMinutes, group.ContextTokenCap, group.ContextSummaryEnabled, group.ContextIncludeBotHistory);

    private static GroupContextOverrides ToContext(ContextOverridesRequest request) => new(request.SenderIsolated, request.HistoryTurns, request.IdleTimeoutMinutes,
        request.TokenCap, request.SummaryEnabled, request.IncludeBotHistory);

    private static void ApplyContext(GroupProfileEntity group, GroupContextOverrides context)
    {
        group.ContextSenderIsolated = context.SenderIsolated;
        group.ContextHistoryTurns = context.HistoryTurns;
        group.ContextIdleTimeoutMinutes = context.IdleTimeoutMinutes;
        group.ContextTokenCap = context.TokenCap;
        group.ContextSummaryEnabled = context.SummaryEnabled;
        group.ContextIncludeBotHistory = context.IncludeBotHistory;
    }

    private static GroupPatternRule[]? ToRules(IEnumerable<RuleRequest> requests)
    {
        var rules = new List<GroupPatternRule>();
        foreach (var request in requests)
        {
            if (!Enum.TryParse<GroupRulePatternKind>(request.PatternKind, true, out var kind)) return null;
            rules.Add(new GroupPatternRule(request.Pattern, kind, request.IgnoreCase));
        }
        return rules.ToArray();
    }

    private static IEnumerable<GroupRuleEntity> ToEntities(Guid groupId, IEnumerable<GroupPatternRule> rules, GroupRuleDirection direction) => rules.Select(rule => new GroupRuleEntity
    {
        GroupProfileId = groupId,
        RuleKind = (int)direction,
        IncludePattern = rule.Pattern.Trim(),
        IncludePatternKind = (int)rule.PatternKind,
        IgnoreCase = rule.IgnoreCase,
        IsEnabled = true
    });

    private static RuleResponse ToRuleResponse(GroupRuleEntity rule) => new(rule.Id, rule.IncludePattern, ((GroupRulePatternKind)rule.IncludePatternKind).ToString().ToLowerInvariant(), rule.IgnoreCase);
    private static RuleResponse ToLegacyExcludeResponse(GroupRuleEntity rule) => new(rule.Id, rule.ExcludePattern!, ((GroupRulePatternKind)rule.ExcludePatternKind).ToString().ToLowerInvariant(), rule.IgnoreCase);

    public sealed record UpdateGroupConfigurationRequest(IReadOnlyList<RuleRequest>? IncludeRules, IReadOnlyList<RuleRequest>? ExcludeRules,
        IReadOnlyList<Guid>? BoundTagIds, ContextOverridesRequest? Context, bool ClearContext)
    {
        public IReadOnlyList<RuleRequest> IncludeRules { get; init; } = IncludeRules ?? [];
        public IReadOnlyList<RuleRequest> ExcludeRules { get; init; } = ExcludeRules ?? [];
        public IReadOnlyList<Guid> BoundTagIds { get; init; } = BoundTagIds ?? [];
        public ContextOverridesRequest Context { get; init; } = Context ?? new(null, null, null, null, null, null);
    }
    public sealed record PreviewGroupRulesRequest(IReadOnlyList<RuleRequest>? IncludeRules, IReadOnlyList<RuleRequest>? ExcludeRules, IReadOnlyList<string>? GroupNames)
    {
        public IReadOnlyList<RuleRequest> IncludeRules { get; init; } = IncludeRules ?? [];
        public IReadOnlyList<RuleRequest> ExcludeRules { get; init; } = ExcludeRules ?? [];
        public IReadOnlyList<string> GroupNames { get; init; } = GroupNames ?? [];
    }
    public sealed record RuleRequest(string Pattern, string PatternKind, bool IgnoreCase = true);
    public sealed record ContextOverridesRequest(bool? SenderIsolated, int? HistoryTurns, int? IdleTimeoutMinutes, int? TokenCap, bool? SummaryEnabled, bool? IncludeBotHistory);
    public sealed record GroupConfigurationResponse(Guid Id, string Name, GroupRulesResponse Rules, IReadOnlyList<Guid> BoundTagIds, IReadOnlyList<Guid> AllowedTagIds,
        IReadOnlyList<KnowledgeTagResponse> AvailableTags, string TagVisibility, GroupContextResponse Context, int ClearedContextMessages);
    public sealed record GroupRulesResponse(IReadOnlyList<RuleResponse> Include, IReadOnlyList<RuleResponse> Exclude);
    public sealed record RuleResponse(Guid Id, string Pattern, string PatternKind, bool IgnoreCase);
    public sealed record KnowledgeTagResponse(Guid Id, string Name, bool IsGlobalPublic, bool IsEnabled, bool IsBound);
    public sealed record GroupContextResponse(GroupContextOverrides Configured, GroupContextSettings Effective);
    public sealed record PreviewGroupRulesResponse(IReadOnlyList<GroupRulePreviewResult> Results);
    public sealed record GroupRulePreviewResult(string GroupName, bool IsMatch, bool IsExcluded);
    private enum GroupRuleDirection { Include, Exclude }
}
