namespace WechatRobot.Application.Knowledge;

public sealed record KnowledgeTagScope(
    IReadOnlyList<Guid> RequestedTagIds,
    IReadOnlyList<Guid> EffectiveVisibleTagIds,
    string FilterDescriptor);

public interface IKnowledgeTagScopeResolver
{
    Task<KnowledgeTagScope> ResolveAsync(IReadOnlyList<Guid> requestedTagIds, CancellationToken cancellationToken);
}
