using WechatRobot.Domain.Groups;

namespace WechatRobot.UnitTests.Groups;

public sealed class GroupRuleMatcherTests
{
    public static TheoryData<GroupRulePatternKind, string, string> MatchingIncludes => new()
    {
        { GroupRulePatternKind.Exact, "技术支持群", "技术支持群" },
        { GroupRulePatternKind.Contains, "支持", "技术支持群" },
        { GroupRulePatternKind.Regex, "^技术.*群$", "技术支持群" }
    };

    [Theory]
    [MemberData(nameof(MatchingIncludes))]
    public void Match_returns_a_match_for_each_supported_include_pattern(
        GroupRulePatternKind patternKind,
        string includePattern,
        string groupName)
    {
        var rule = new GroupRule(Guid.NewGuid(), includePattern, patternKind);

        var result = GroupRuleMatcher.Match(rule, groupName);

        Assert.True(result.IsMatch);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Match_exclude_match_wins_over_an_include_match()
    {
        var rule = new GroupRule(
            Guid.NewGuid(),
            "技术",
            GroupRulePatternKind.Contains,
            "测试",
            GroupRulePatternKind.Contains);

        var result = GroupRuleMatcher.Match(rule, "技术测试群");

        Assert.False(result.IsMatch);
        Assert.True(result.IsExcluded);
    }

    [Fact]
    public void Match_does_not_allow_a_group_without_an_include_match()
    {
        var rule = new GroupRule(Guid.NewGuid(), "技术", GroupRulePatternKind.Contains);

        var result = GroupRuleMatcher.Match(rule, "行政群");

        Assert.False(result.IsMatch);
        Assert.False(result.IsExcluded);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Match_returns_a_validation_failure_for_an_invalid_regex()
    {
        var rule = new GroupRule(Guid.NewGuid(), "(", GroupRulePatternKind.Regex);

        var result = GroupRuleMatcher.Match(rule, "技术支持群");

        Assert.False(result.IsMatch);
        Assert.False(result.IsValid);
        Assert.NotNull(result.ValidationError);
    }

    [Fact]
    public void Match_bounds_regex_execution_to_100_milliseconds()
    {
        var rule = new GroupRule(Guid.NewGuid(), "^(a+)+\\1$", GroupRulePatternKind.Regex);
        var groupName = new string('a', 30_000) + "!";

        var result = GroupRuleMatcher.Match(rule, groupName);

        Assert.False(result.IsMatch);
        Assert.False(result.IsValid);
        Assert.Equal("Regex execution timed out.", result.ValidationError);
    }
}
