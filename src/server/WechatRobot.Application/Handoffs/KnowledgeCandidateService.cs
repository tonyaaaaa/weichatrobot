namespace WechatRobot.Application.Handoffs;

public sealed record ReviewKnowledgeCandidateCommand(Guid CandidateId, Guid ReviewerUserId, string Decision,
    IReadOnlyList<Guid> TagIds, string? RevisedAnswer, string IdempotencyKey, int ExpectedVersion);
public sealed record KnowledgeCandidateReviewResult(Guid CandidateId, string Status, Guid? KnowledgeDocumentVersionId, Guid? IndexJobId, int Version);

public interface IKnowledgeCandidateStore
{
    Task<KnowledgeCandidateReviewResult> ReviewAsync(ReviewKnowledgeCandidateCommand command, DateTime nowUtc, CancellationToken token);
}

public sealed class KnowledgeCandidateService(IKnowledgeCandidateStore store, TimeProvider timeProvider)
{
    public Task<KnowledgeCandidateReviewResult> ReviewAsync(ReviewKnowledgeCandidateCommand command, CancellationToken token)
    {
        if (command.ReviewerUserId == Guid.Empty) throw new UnauthorizedAccessException("An authenticated reviewer is required.");
        if (command.Decision is not ("approve" or "reject" or "revision")) throw new ArgumentException("Unsupported review decision.");
        if (command.Decision == "approve" && command.TagIds.Count == 0) throw new ArgumentException("Approval requires at least one tag.");
        return store.ReviewAsync(command, timeProvider.GetUtcNow().UtcDateTime, token);
    }
}
