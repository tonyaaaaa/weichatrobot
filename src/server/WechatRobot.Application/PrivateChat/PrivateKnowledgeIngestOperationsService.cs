namespace WechatRobot.Application.PrivateChat;

public sealed class PrivateKnowledgeIngestOperationsService(
    IPrivateKnowledgeIngestStore store,
    TimeProvider timeProvider)
{
    public Task<IReadOnlyList<PrivateKnowledgeIngestBatch>> ListAsync(
        string? status,
        int skip,
        int take,
        CancellationToken cancellationToken) =>
        store.ListAsync(status, Math.Max(0, skip), Math.Clamp(take, 1, 200), cancellationToken);

    public Task<PrivateKnowledgeIngestBatch?> GetAsync(
        Guid id,
        CancellationToken cancellationToken) =>
        store.GetAsync(id, cancellationToken);

    public Task<PrivateKnowledgeIngestBatch> RetryAsync(
        Guid id,
        int expectedVersion,
        CancellationToken cancellationToken) =>
        store.RetryAsync(
            id,
            expectedVersion,
            timeProvider.GetUtcNow().UtcDateTime,
            cancellationToken);
}
