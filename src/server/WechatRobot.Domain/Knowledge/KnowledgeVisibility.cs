using WechatRobot.Domain.Groups;

namespace WechatRobot.Domain.Knowledge;

public static class KnowledgeVisibility
{
    public static IReadOnlySet<Guid> BuildAllowedTagIds(
        string groupName,
        IEnumerable<GroupRule> groupRules,
        KnowledgeTag globalPublicTag)
    {
        ArgumentNullException.ThrowIfNull(groupName);
        ArgumentNullException.ThrowIfNull(groupRules);
        ArgumentNullException.ThrowIfNull(globalPublicTag);

        var allowedTagIds = new HashSet<Guid>();
        if (globalPublicTag.IsEnabled && globalPublicTag.IsGlobalPublic)
        {
            allowedTagIds.Add(globalPublicTag.Id);
        }

        foreach (var rule in groupRules)
        {
            if (!GroupRuleMatcher.Match(rule, groupName).IsMatch)
            {
                continue;
            }

            allowedTagIds.UnionWith(rule.BoundTagIds);
        }

        return allowedTagIds;
    }
}
