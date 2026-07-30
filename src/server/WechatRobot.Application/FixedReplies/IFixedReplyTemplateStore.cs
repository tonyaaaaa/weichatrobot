namespace WechatRobot.Application.FixedReplies;

public interface IFixedReplyTemplateStore
{
    Task<IReadOnlyList<FixedReplyTemplateView>> ListAsync(
        FixedReplyTemplateQuery query,
        CancellationToken cancellationToken);

    Task<FixedReplyTemplateView?> GetAsync(
        Guid id,
        CancellationToken cancellationToken);

    Task<FixedReplyTemplateView> CreateAsync(
        ValidatedFixedReplyTemplate template,
        Guid actorUserId,
        DateTime nowUtc,
        CancellationToken cancellationToken);

    Task<FixedReplyTemplateView> UpdateAsync(
        Guid id,
        int expectedVersion,
        ValidatedFixedReplyTemplate template,
        Guid actorUserId,
        DateTime nowUtc,
        CancellationToken cancellationToken);

    Task<FixedReplyTemplateView> SetEnabledAsync(
        Guid id,
        int expectedVersion,
        bool enabled,
        Guid actorUserId,
        DateTime nowUtc,
        CancellationToken cancellationToken);

    Task DeleteAsync(
        Guid id,
        int expectedVersion,
        Guid actorUserId,
        DateTime nowUtc,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<EffectiveFixedReply>> ListEffectiveAsync(
        Guid groupProfileId,
        int maximumCandidates,
        int examplesPerTemplate,
        CancellationToken cancellationToken);

    Task<ResolvedFixedReply?> ResolveAsync(
        Guid templateId,
        int expectedVersion,
        Guid groupProfileId,
        CancellationToken cancellationToken);
}
