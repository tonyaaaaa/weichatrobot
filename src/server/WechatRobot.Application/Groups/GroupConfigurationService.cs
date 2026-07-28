using WechatRobot.Domain.Groups;
using WechatRobot.Domain.Knowledge;
using System.Globalization;

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

    public GroupAnswerFallbackValidation ValidateAnswerFallback(
        GroupAnswerFallbackSettings settings)
    {
        if (settings.WebSearchResultCount is < 1 or > 20)
            return GroupAnswerFallbackValidation.Invalid(
                "Web Search result count must be between 1 and 20.");
        if (settings.WebSearchRecency is not ("NoLimit" or "OneDay" or "OneWeek" or "OneMonth" or "OneYear"))
            return GroupAnswerFallbackValidation.Invalid("Web Search recency is invalid.");
        if (settings.WebSearchContentSize is not ("Medium" or "High"))
            return GroupAnswerFallbackValidation.Invalid("Web Search content size is invalid.");
        if (settings.FinalNoEvidencePolicy is not ("InsufficientEvidence" or "Clarification"))
            return GroupAnswerFallbackValidation.Invalid("Final no-evidence policy is invalid.");

        var domains = new List<string>();
        foreach (var raw in (settings.WebSearchDomainFilter ?? string.Empty)
            .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.Length > 253
                || raw.Contains("://", StringComparison.Ordinal)
                || raw.IndexOfAny(['/', '\\', '@', '*', '?', '#', ':']) >= 0
                || Uri.CheckHostName(raw) == UriHostNameType.Unknown)
                return GroupAnswerFallbackValidation.Invalid(
                    "Web Search domain filter must contain host names only.");
            try
            {
                domains.Add(new IdnMapping().GetAscii(raw).ToLowerInvariant());
            }
            catch (ArgumentException)
            {
                return GroupAnswerFallbackValidation.Invalid(
                    "Web Search domain filter contains an invalid host name.");
            }
        }
        var normalized = string.Join(',', domains.Distinct(StringComparer.Ordinal).Take(20));
        if (normalized.Length > 512)
            return GroupAnswerFallbackValidation.Invalid(
                "Web Search domain filter is too long.");
        return GroupAnswerFallbackValidation.Valid(settings with
        {
            WebSearchDomainFilter = string.IsNullOrEmpty(normalized) ? null : normalized
        });
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
public sealed record GroupAnswerFallbackSettings(
    bool WebSearchEnabled,
    bool ModelKnowledgeFallbackEnabled,
    bool WebSearchShowSources,
    int WebSearchResultCount,
    string WebSearchRecency,
    string? WebSearchDomainFilter,
    string WebSearchContentSize,
    string FinalNoEvidencePolicy);
public sealed record GroupAnswerFallbackValidation(
    bool IsValid,
    GroupAnswerFallbackSettings? Settings,
    string? Error)
{
    public static GroupAnswerFallbackValidation Valid(GroupAnswerFallbackSettings settings) =>
        new(true, settings, null);
    public static GroupAnswerFallbackValidation Invalid(string error) =>
        new(false, null, error);
}
public sealed record GroupConfigurationValidation(bool IsValid, string? Error)
{
    public static GroupConfigurationValidation Valid { get; } = new(true, null);
    public static GroupConfigurationValidation Invalid(string error) => new(false, error);
}
