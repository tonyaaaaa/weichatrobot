using System.Collections.Immutable;

namespace WechatRobot.Domain.Groups;

public enum GroupRulePatternKind
{
    Exact,
    Contains,
    Regex
}

public sealed record GroupRule
{
    public GroupRule(
        Guid id,
        string includePattern,
        GroupRulePatternKind includePatternKind,
        string? excludePattern = null,
        GroupRulePatternKind excludePatternKind = GroupRulePatternKind.Exact,
        bool ignoreCase = true,
        bool isEnabled = true,
        IEnumerable<Guid>? boundTagIds = null)
    {
        Id = id;
        IncludePattern = includePattern;
        IncludePatternKind = includePatternKind;
        ExcludePattern = excludePattern;
        ExcludePatternKind = excludePatternKind;
        IgnoreCase = ignoreCase;
        IsEnabled = isEnabled;
        BoundTagIds = (boundTagIds ?? Array.Empty<Guid>()).ToImmutableHashSet();
    }

    public Guid Id { get; }

    public string IncludePattern { get; }

    public GroupRulePatternKind IncludePatternKind { get; }

    public string? ExcludePattern { get; }

    public GroupRulePatternKind ExcludePatternKind { get; }

    public bool IgnoreCase { get; }

    public bool IsEnabled { get; }

    public ImmutableHashSet<Guid> BoundTagIds { get; }
}

public sealed record GroupRuleMatchResult(
    bool IsMatch,
    bool IsExcluded,
    bool IsValid,
    string? ValidationError = null);
