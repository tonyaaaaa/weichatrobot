using WechatRobot.Domain.Groups;
using WechatRobot.Domain.Knowledge;
using System.Collections.Immutable;

namespace WechatRobot.UnitTests.Knowledge;

public sealed class KnowledgeVisibilityTests
{
    [Fact]
    public void BuildAllowedTagIds_returns_an_immutable_set_that_cannot_be_changed_in_place()
    {
        var globalPublicTag = new KnowledgeTag(Guid.NewGuid(), "Public", IsEnabled: true, IsGlobalPublic: true);
        var allowedTagIds = KnowledgeVisibility.BuildAllowedTagIds("任何群", Array.Empty<GroupRule>(), globalPublicTag);
        var additionalTagId = Guid.NewGuid();

        var immutableAllowedTagIds = Assert.IsAssignableFrom<IImmutableSet<Guid>>(allowedTagIds);
        var changedTagIds = immutableAllowedTagIds.Add(additionalTagId);

        Assert.DoesNotContain(additionalTagId, allowedTagIds);
        Assert.Contains(additionalTagId, changedTagIds);
    }

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
