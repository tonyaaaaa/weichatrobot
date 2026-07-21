using WechatRobot.Domain.Groups;
using WechatRobot.Domain.Knowledge;

namespace WechatRobot.UnitTests.Knowledge;

public sealed class KnowledgeVisibilityTests
{
    [Fact]
    public void BuildAllowedTagIds_uses_or_semantics_for_matched_group_tags_and_includes_enabled_global_public_tag()
    {
        var globalPublicTag = new KnowledgeTag(Guid.NewGuid(), "Public", IsEnabled: true, IsGlobalPublic: true);
        var firstGroupTag = new KnowledgeTag(Guid.NewGuid(), "Sales", IsEnabled: true);
        var secondGroupTag = new KnowledgeTag(Guid.NewGuid(), "Support", IsEnabled: true);
        var rules = new[]
        {
            new GroupRule(Guid.NewGuid(), "华东", GroupRulePatternKind.Contains, boundTagIds: new[] { firstGroupTag.Id }),
            new GroupRule(Guid.NewGuid(), "VIP", GroupRulePatternKind.Contains, boundTagIds: new[] { secondGroupTag.Id })
        };

        var allowedTagIds = KnowledgeVisibility.BuildAllowedTagIds("华东VIP客户群", rules, globalPublicTag);

        Assert.Equal(
            new HashSet<Guid> { globalPublicTag.Id, firstGroupTag.Id, secondGroupTag.Id },
            allowedTagIds);
    }
}
