using System.Text.RegularExpressions;

namespace WechatRobot.Domain.Groups;

public static class GroupRuleMatcher
{
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    public static GroupRuleMatchResult Match(GroupRule rule, string groupName)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(groupName);

        if (!rule.IsEnabled)
        {
            return new GroupRuleMatchResult(false, false, true);
        }

        var include = MatchPattern(rule.IncludePattern, rule.IncludePatternKind, groupName, rule.IgnoreCase);
        if (!include.IsValid)
        {
            return new GroupRuleMatchResult(false, false, false, include.ValidationError);
        }

        if (!include.IsMatch)
        {
            return new GroupRuleMatchResult(false, false, true);
        }

        if (string.IsNullOrWhiteSpace(rule.ExcludePattern))
        {
            return new GroupRuleMatchResult(true, false, true);
        }

        var exclude = MatchPattern(rule.ExcludePattern, rule.ExcludePatternKind, groupName, rule.IgnoreCase);
        if (!exclude.IsValid)
        {
            return new GroupRuleMatchResult(false, false, false, exclude.ValidationError);
        }

        return exclude.IsMatch
            ? new GroupRuleMatchResult(false, true, true)
            : new GroupRuleMatchResult(true, false, true);
    }

    private static PatternMatchResult MatchPattern(
        string pattern,
        GroupRulePatternKind patternKind,
        string groupName,
        bool ignoreCase)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return PatternMatchResult.Invalid("Patterns cannot be empty.");
        }

        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return patternKind switch
        {
            GroupRulePatternKind.Exact => PatternMatchResult.Valid(string.Equals(groupName, pattern, comparison)),
            GroupRulePatternKind.Contains => PatternMatchResult.Valid(groupName.Contains(pattern, comparison)),
            GroupRulePatternKind.Regex => MatchRegex(pattern, groupName, ignoreCase),
            _ => PatternMatchResult.Invalid("Unsupported pattern kind.")
        };
    }

    private static PatternMatchResult MatchRegex(string pattern, string groupName, bool ignoreCase)
    {
        var options = RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);

        try
        {
            return PatternMatchResult.Valid(new Regex(pattern, options | RegexOptions.NonBacktracking, RegexTimeout).IsMatch(groupName));
        }
        catch (NotSupportedException)
        {
            return MatchRegexWithBacktracking(pattern, groupName, options);
        }
        catch (ArgumentException exception)
        {
            return PatternMatchResult.Invalid(exception.Message);
        }
        catch (RegexMatchTimeoutException)
        {
            return PatternMatchResult.Invalid("Regex execution timed out.");
        }
    }

    private static PatternMatchResult MatchRegexWithBacktracking(string pattern, string groupName, RegexOptions options)
    {
        try
        {
            return PatternMatchResult.Valid(new Regex(pattern, options, RegexTimeout).IsMatch(groupName));
        }
        catch (ArgumentException exception)
        {
            return PatternMatchResult.Invalid(exception.Message);
        }
        catch (RegexMatchTimeoutException)
        {
            return PatternMatchResult.Invalid("Regex execution timed out.");
        }
    }

    private sealed record PatternMatchResult(bool IsMatch, bool IsValid, string? ValidationError)
    {
        public static PatternMatchResult Valid(bool isMatch) => new(isMatch, true, null);

        public static PatternMatchResult Invalid(string validationError) => new(false, false, validationError);
    }
}
