using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Knowledge;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.Infrastructure.Knowledge;

public sealed class KnowledgeTagScopeResolver(WechatRobotDbContext database) : IKnowledgeTagScopeResolver
{
    public const string EffectiveVisibleTagsFilter = "tag_ids:any-of-effective-visible-tags";

    public async Task<KnowledgeTagScope> ResolveAsync(IReadOnlyList<Guid> requestedTagIds, CancellationToken cancellationToken)
    {
        var requested = requestedTagIds.Distinct().Order().ToArray();
        var effective = (await database.KnowledgeTags.AsNoTracking()
            .Where(tag => tag.IsEnabled && tag.IsGlobalPublic)
            .Select(tag => tag.Id)
            .ToArrayAsync(cancellationToken)).ToHashSet();
        foreach (var batch in GuidBatchQuery.CreateBatches(requested))
        {
            var predicate = GuidBatchQuery.BuildPredicate<KnowledgeTagEntity>(batch, tag => tag.Id);
            effective.UnionWith(await database.KnowledgeTags.AsNoTracking()
                .Where(tag => tag.IsEnabled).Where(predicate)
                .Select(tag => tag.Id)
                .ToArrayAsync(cancellationToken));
        }
        return new(requested, effective.Order().ToArray(), EffectiveVisibleTagsFilter);
    }
}
