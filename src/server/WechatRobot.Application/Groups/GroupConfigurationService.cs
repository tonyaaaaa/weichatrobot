using WechatRobot.Domain.Groups;
using WechatRobot.Domain.Knowledge;

namespace WechatRobot.Application.Groups;

public sealed class GroupConfigurationService
{
    public const int DefaultHistoryTurns = 6;
    public const int DefaultIdleTimeoutMinutes = 30;
    public const int DefaultTokenCap = 3000;
    public const bool DefaultSenderIsolated = false;
    public const bool DefaultSummaryEnabled = true;
    public const bool DefaultIncludeBotHistory = true;

    public GroupContextSettings GetEffectiveContext(GroupContextOverrides configured) => new(
        configured.SenderIsolated ?? DefaultSenderIsolated,
        configured.HistoryTurns ?? DefaultHistoryTurns,
        configured.IdleTimeoutMinutes ?? DefaultIdleTimeoutMinutes,
        configured.TokenCap ?? DefaultTokenCap,
        configured.SummaryEnabled ?? DefaultSummaryEnabled,
        configured.IncludeBotHistory ?? DefaultIncludeBotHistory);

    public GroupConfigurationValidation Validate(GroupContextOverrides context, IEnumerable<GroupPatternRule> includeRules, IEnumerable<GroupPatternRule> excludeRules)
    {
        if (context.HistoryTurns is < 0 or > 100 || context.IdleTimeoutMinutes is < 1 or > 1440 || context.TokenCap is < 256 or > 100_000)
        {
            return GroupConfigurationValidation.Invalid("Context override values are out of range.");
        }

        foreach (var rule in includeRules.Concat(excludeRules))
        {
            if (string.IsNullOrWhiteSpace(rule.Pattern) || rule.Pattern.Length > 1024)
            {
                return GroupConfigurationValidation.Invalid("Rule patterns must contain 1 to 1024 characters.");
            }

            var result = GroupRuleMatcher.Match(new GroupRule(Guid.Empty, rule.Pattern, rule.PatternKind, ignoreCase: rule.IgnoreCase), "validation");
            if (!result.IsValid)
            {
                return GroupConfigurationValidation.Invalid(result.ValidationError ?? "Invalid regex rule.");
            }
        }

        return GroupConfigurationValidation.Valid;
    }

    public GroupRuleMatchResult Preview(IEnumerable<GroupPatternRule> includeRules, IEnumerable<GroupPatternRule> excludeRules, string groupName)
    {
        var include = includeRules.Select(rule => GroupRuleMatcher.Match(new GroupRule(Guid.NewGuid(), rule.Pattern, rule.PatternKind, ignoreCase: rule.IgnoreCase), groupName)).ToArray();
        var invalidInclude = include.FirstOrDefault(result => !result.IsValid);
        if (invalidInclude is not null) return invalidInclude;
        if (!include.Any(result => result.IsMatch)) return new GroupRuleMatchResult(false, false, true);

        var exclude = excludeRules.Select(rule => GroupRuleMatcher.Match(new GroupRule(Guid.NewGuid(), rule.Pattern, rule.PatternKind, ignoreCase: rule.IgnoreCase), groupName)).ToArray();
        var invalidExclude = exclude.FirstOrDefault(result => !result.IsValid);
        if (invalidExclude is not null) return invalidExclude;
        return exclude.Any(result => result.IsMatch)
            ? new GroupRuleMatchResult(false, true, true)
            : new GroupRuleMatchResult(true, false, true);
    }

    public IReadOnlySet<Guid> ResolveVisibleTagIds(IEnumerable<Guid> boundTagIds, IEnumerable<KnowledgeTag> tags) =>
        tags.Where(tag => tag.IsEnabled && (tag.IsGlobalPublic || boundTagIds.Contains(tag.Id))).Select(tag => tag.Id).ToHashSet();
}

public sealed record GroupPatternRule(string Pattern, GroupRulePatternKind PatternKind, bool IgnoreCase = true);
public sealed record GroupContextOverrides(bool? SenderIsolated, int? HistoryTurns, int? IdleTimeoutMinutes, int? TokenCap, bool? SummaryEnabled, bool? IncludeBotHistory);
public sealed record GroupContextSettings(bool SenderIsolated, int HistoryTurns, int IdleTimeoutMinutes, int TokenCap, bool SummaryEnabled, bool IncludeBotHistory);
public sealed record GroupConfigurationValidation(bool IsValid, string? Error)
{
    public static GroupConfigurationValidation Valid { get; } = new(true, null);
    public static GroupConfigurationValidation Invalid(string error) => new(false, error);
}
