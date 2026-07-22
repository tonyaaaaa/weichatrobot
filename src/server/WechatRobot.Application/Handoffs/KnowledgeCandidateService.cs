namespace WechatRobot.Application.Handoffs;

public sealed record ReviewKnowledgeCandidateCommand(Guid CandidateId, Guid ReviewerUserId, string Decision,
    IReadOnlyList<Guid>? TagIds, string? RevisedAnswer, string IdempotencyKey, int ExpectedVersion);
public sealed record KnowledgeCandidateReviewResult(Guid CandidateId, string Status, Guid? KnowledgeDocumentVersionId, Guid? IndexJobId, int Version,
    Guid? PublishJobId = null);

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
        var tags = command.TagIds ?? throw new ArgumentException("TagIds is required.");
        if (command.Decision == "approve" && tags.Count == 0) throw new ArgumentException("Approval requires at least one tag.");
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey) || command.IdempotencyKey.Length > 48)
            throw new ArgumentException("Review idempotency key is required and must not exceed 48 characters.");
        command = command with { TagIds = tags, IdempotencyKey = $"candidate:{command.CandidateId:D}:{command.Decision}:{command.IdempotencyKey.Trim()}" };
        return store.ReviewAsync(command, timeProvider.GetUtcNow().UtcDateTime, token);
    }
}
