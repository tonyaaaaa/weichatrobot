using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Conversations;
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
        groups.MapPost("{id:guid}/disable", DisableAsync);
        groups.MapPost("{id:guid}/enable", EnableAsync);
        groups.MapPost("{id:guid}/archive", ArchiveAsync);
        groups.MapPost("{id:guid}/restore", RestoreAsync);
        groups.MapGet("{id:guid}/conversation-context", GetConversationContextAsync);
        groups.MapPost("{id:guid}/conversation-context/clear", ClearConversationContextAsync);
        endpoints.MapPost("/api/group-rules/preview", PreviewAsync).RequireAuthorization(SystemRoles.Admin);
        return endpoints;
    }

    private static async Task<IResult> GetConversationContextAsync(
        Guid id,
        int page,
        int pageSize,
        ConversationContextQueryService service,
        CancellationToken token)
    {
        var result = await service.GetAsync(id, page, pageSize, token);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> ClearConversationContextAsync(
        Guid id,
        ClearConversationContextRequest request,
        ConversationContextQueryService service,
        WechatRobotDbContext database,
        ClaimsPrincipal user,
        CancellationToken token)
    {
        if (request.ExpectedConfigurationVersion < 0)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["expectedConfigurationVersion"] = ["Configuration version must be zero or greater."]
            });

        var result = await service.ClearAsync(id, request.ExpectedConfigurationVersion, token);
        if (result.Status == ClearConversationContextStatus.NotFound)
            return Results.NotFound();
        if (result.Status == ClearConversationContextStatus.Conflict)
            return Results.Conflict(new
            {
                error = "group-configuration-conflict",
                currentVersion = result.CurrentConfigurationVersion
            });

        database.AdministrationAudits.Add(new AdministrationAuditEntity
        {
            Actor = user.Identity?.Name ?? "unknown",
            Action = "group.context.clear",
            TargetType = "GroupProfile",
            TargetId = id.ToString("D"),
            SanitizedDetailJson = JsonSerializer.Serialize(new
            {
                result.ClearedSessions,
                configurationVersion = result.CurrentConfigurationVersion
            })
        });
        await database.SaveChangesAsync(token);
        return Results.Ok(new
        {
            result.ClearedSessions,
            configurationVersion = result.CurrentConfigurationVersion
        });
    }

    private static Task<IResult> DisableAsync(
        Guid id, ChangeGroupStateRequest request, GroupLifecycleService service,
        WechatRobotDbContext database, ClaimsPrincipal user, CancellationToken token) =>
        ChangeStateAsync(id, request, "disable", service, database, user, token);

    private static Task<IResult> EnableAsync(
        Guid id, ChangeGroupStateRequest request, GroupLifecycleService service,
        WechatRobotDbContext database, ClaimsPrincipal user, CancellationToken token) =>
        ChangeStateAsync(id, request, "enable", service, database, user, token);

    private static Task<IResult> ArchiveAsync(
        Guid id, ChangeGroupStateRequest request, GroupLifecycleService service,
        WechatRobotDbContext database, ClaimsPrincipal user, CancellationToken token) =>
        ChangeStateAsync(id, request, "archive", service, database, user, token);

    private static Task<IResult> RestoreAsync(
        Guid id, ChangeGroupStateRequest request, GroupLifecycleService service,
        WechatRobotDbContext database, ClaimsPrincipal user, CancellationToken token) =>
        ChangeStateAsync(id, request, "restore", service, database, user, token);

    private static async Task<IResult> ChangeStateAsync(
        Guid id,
        ChangeGroupStateRequest request,
        string action,
        GroupLifecycleService service,
        WechatRobotDbContext database,
        ClaimsPrincipal user,
        CancellationToken token)
    {
        if (request.ExpectedStateVersion < 0)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["expectedStateVersion"] = ["State version must be zero or greater."]
            });

        var result = action switch
        {
            "disable" => await service.DisableAsync(id, request.ExpectedStateVersion, token),
            "enable" => await service.EnableAsync(id, request.ExpectedStateVersion, token),
            "archive" => await service.ArchiveAsync(id, request.ExpectedStateVersion, token),
            "restore" => await service.RestoreAsync(id, request.ExpectedStateVersion, token),
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        if (result.Status == GroupLifecycleResult.NotFound)
            return Results.NotFound();
        if (result.Status == GroupLifecycleResult.Conflict)
            return Results.Conflict(new
            {
                error = result.ErrorCode,
                currentState = result.State,
                blockers = result.Blockers
            });

        database.AdministrationAudits.Add(new AdministrationAuditEntity
        {
            Actor = user.Identity?.Name ?? "unknown",
            Action = $"group.{action}",
            TargetType = "GroupProfile",
            TargetId = id.ToString("D"),
            SanitizedDetailJson = JsonSerializer.Serialize(new
            {
                result.State!.State,
                result.State.StateVersion
            })
        });
        await database.SaveChangesAsync(token);
        return Results.Ok(result.State);
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
        IGroundedConversationRepository conversations,
        CancellationToken cancellationToken)
    {
        if (request.ExpectedConfigurationVersion is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["expectedConfigurationVersion"] = ["The current configuration version is required for every update."]
            });
        }
        var selectedTagIds = request.BoundTagIds.Distinct().ToArray();
        if (request.IncludeRules.Count > MaximumRulesPerKind || request.ExcludeRules.Count > MaximumRulesPerKind || selectedTagIds.Length > 100)
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
        GroupAnswerFallbackSettings? answerFallback = null;
        if (request.AnswerFallback is not null)
        {
            var fallbackValidation = service.ValidateAnswerFallback(
                ToAnswerFallback(request.AnswerFallback));
            if (!fallbackValidation.IsValid)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["answerFallback"] = [fallbackValidation.Error!]
                });
            answerFallback = fallbackValidation.Settings;
        }

        var group = await database.GroupProfiles.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (group is null) return Results.NotFound();
        if (request.ExpectedConfigurationVersion.Value != group.ConfigurationVersion)
            return Results.Conflict(new { error = "group-configuration-conflict", currentVersion = group.ConfigurationVersion });

        if (selectedTagIds.Length > 0)
        {
            var selectedTags = GuidBatchQuery.BuildPredicate<KnowledgeTagEntity>(selectedTagIds, tag => tag.Id);
            if (await database.KnowledgeTags.Where(tag => tag.IsEnabled).Where(selectedTags).CountAsync(cancellationToken) != selectedTagIds.Length)
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
        if (answerFallback is not null) ApplyAnswerFallback(group, answerFallback);
        group.ConfigurationVersion++;
        group.UpdatedAtUtc = DateTime.UtcNow;
        try { await database.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Conflict(new { error = "group-configuration-conflict" });
        }
        var clearedSessions = request.ClearContext
            ? await conversations.ClearGroupContextAsync(id, DateTime.UtcNow, cancellationToken)
            : 0;
        return Results.Ok(await ToResponseAsync(group, database, service, clearedSessions, cancellationToken));
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

    private static async Task<GroupConfigurationResponse> ToResponseAsync(GroupProfileEntity group, WechatRobotDbContext database, GroupConfigurationService service, int clearedSessions, CancellationToken cancellationToken)
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
            ToAnswerFallback(group),
            clearedSessions,
            group.ConfigurationVersion);
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

    private static GroupAnswerFallbackSettings ToAnswerFallback(
        AnswerFallbackRequest request) => new(
        request.WebSearchEnabled,
        request.ModelKnowledgeFallbackEnabled,
        request.WebSearchShowSources,
        request.WebSearchResultCount,
        request.WebSearchRecency,
        request.WebSearchDomainFilter,
        request.WebSearchContentSize,
        request.FinalNoEvidencePolicy);

    private static GroupAnswerFallbackSettings ToAnswerFallback(
        GroupProfileEntity group) => new(
        group.WebSearchEnabled,
        group.ModelKnowledgeFallbackEnabled,
        group.WebSearchShowSources,
        group.WebSearchResultCount,
        group.WebSearchRecency,
        group.WebSearchDomainFilter,
        group.WebSearchContentSize,
        group.FinalNoEvidencePolicy);

    private static void ApplyAnswerFallback(
        GroupProfileEntity group,
        GroupAnswerFallbackSettings settings)
    {
        group.WebSearchEnabled = settings.WebSearchEnabled;
        group.ModelKnowledgeFallbackEnabled = settings.ModelKnowledgeFallbackEnabled;
        group.WebSearchShowSources = settings.WebSearchShowSources;
        group.WebSearchResultCount = settings.WebSearchResultCount;
        group.WebSearchRecency = settings.WebSearchRecency;
        group.WebSearchDomainFilter = settings.WebSearchDomainFilter;
        group.WebSearchContentSize = settings.WebSearchContentSize;
        group.FinalNoEvidencePolicy = settings.FinalNoEvidencePolicy;
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
        IReadOnlyList<Guid>? BoundTagIds, ContextOverridesRequest? Context, bool ClearContext,
        int? ExpectedConfigurationVersion = null, AnswerFallbackRequest? AnswerFallback = null)
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
    public sealed record ChangeGroupStateRequest(int ExpectedStateVersion);
    public sealed record ClearConversationContextRequest(int ExpectedConfigurationVersion);
    public sealed record ContextOverridesRequest(bool? SenderIsolated, int? HistoryTurns, int? IdleTimeoutMinutes, int? TokenCap, bool? SummaryEnabled, bool? IncludeBotHistory);
    public sealed record AnswerFallbackRequest(
        bool WebSearchEnabled = false,
        bool ModelKnowledgeFallbackEnabled = false,
        bool WebSearchShowSources = false,
        int WebSearchResultCount = 5,
        string WebSearchRecency = "NoLimit",
        string? WebSearchDomainFilter = null,
        string WebSearchContentSize = "Medium",
        string FinalNoEvidencePolicy = "InsufficientEvidence");
    public sealed record GroupConfigurationResponse(Guid Id, string Name, GroupRulesResponse Rules, IReadOnlyList<Guid> BoundTagIds, IReadOnlyList<Guid> AllowedTagIds,
        IReadOnlyList<KnowledgeTagResponse> AvailableTags, string TagVisibility, GroupContextResponse Context,
        GroupAnswerFallbackSettings AnswerFallback, int ClearedContextSessions,
        int ConfigurationVersion);
    public sealed record GroupRulesResponse(IReadOnlyList<RuleResponse> Include, IReadOnlyList<RuleResponse> Exclude);
    public sealed record RuleResponse(Guid Id, string Pattern, string PatternKind, bool IgnoreCase);
    public sealed record KnowledgeTagResponse(Guid Id, string Name, bool IsGlobalPublic, bool IsEnabled, bool IsBound);
    public sealed record GroupContextResponse(GroupContextOverrides Configured, GroupContextSettings Effective);
    public sealed record PreviewGroupRulesResponse(IReadOnlyList<GroupRulePreviewResult> Results);
    public sealed record GroupRulePreviewResult(string GroupName, bool IsMatch, bool IsExcluded);
    private enum GroupRuleDirection { Include, Exclude }
}
