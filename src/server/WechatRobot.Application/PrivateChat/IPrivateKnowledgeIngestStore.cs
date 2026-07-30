namespace WechatRobot.Application.PrivateChat;

public interface IPrivateKnowledgeIngestStore
{
    Task<PrivateKnowledgeIngestBatch> GetOrCreateAsync(
        Guid robotConfigId,
        Guid sourceConversationMessageId,
        int roomType,
        string sourceActorDisplayName,
        DateTime nowUtc,
        CancellationToken cancellationToken);

    Task<PrivateKnowledgeIngestBatch?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<PrivateKnowledgeIngestBatch>> ListAsync(
        string? status,
        int skip,
        int take,
        CancellationToken cancellationToken);
    Task SaveProposalsAsync(
        Guid batchId,
        int expectedVersion,
        IReadOnlyList<ProposedKnowledgeItem> proposals,
        DateTime nowUtc,
        CancellationToken cancellationToken);
    Task MarkFailedAsync(
        Guid batchId,
        string failureCode,
        bool retryable,
        DateTime nowUtc,
        CancellationToken cancellationToken);
    Task<PrivateKnowledgeIngestBatch> RetryAsync(
        Guid batchId,
        int expectedVersion,
        DateTime nowUtc,
        CancellationToken cancellationToken);
}
