using WechatRobot.Application.Groups;
using WechatRobot.Application.Models;

namespace WechatRobot.Application.Conversations;

public enum AnswerDecisionKind { Answer, Clarification, Handoff, InsufficientEvidence, SystemFailure }

public sealed record AnswerDecision(AnswerDecisionKind Kind, string GroupText);
public sealed record RetrievalEvidence(Guid DocumentId, Guid VersionId, Guid ChunkId, int? PageNumber, double Similarity,
    IReadOnlyList<Guid> TagIds, string DocumentTitle, string Text);
public sealed record RetrievalAuditDraft(IReadOnlyList<RetrievalEvidence> Evidence, double ConfidenceThreshold, double? ConfidenceValue,
    string ContextPolicy, string Decision, string? FailureCode = null);
public sealed record GroundedAnswerResult(AnswerDecision Decision, RetrievalAuditDraft Audit);
public sealed record GroundedAnswerRequest(Guid MessageId, Guid GroupProfileId, string SenderExternalUserId, string Question,
    IReadOnlyList<Guid> AllowedTagIds, ConversationContextResult Context, GroupContextSettings ContextPolicy,
    ModelProviderConfiguration ChatConfiguration);

public interface IRetrievalEvidenceProvider
{
    Task<IReadOnlyList<RetrievalEvidence>> RetrieveAsync(string question, IReadOnlyList<Guid> allowedTagIds, int limit, CancellationToken token);
}

public sealed class RetrievalUnavailableException(string message, Exception? innerException = null) : Exception(message, innerException);

public sealed record GroundedAnswerOptions(
    double ConfidenceThreshold = .7,
    int MaximumEvidence = 8,
    string InsufficientEvidenceText = "信息不足，请联系人工客服。",
    string SystemFailureText = "系统暂时不可用，请稍后再试。",
    string SensitiveHandoffText = "该问题需要转人工客服处理。")
{
    public const string SectionName = "GroundedAnswer";
    public IReadOnlyList<string> SensitiveTerms { get; init; } = ["密码", "验证码", "银行卡", "转账", "汇款", "身份证号"];

    public void Validate()
    {
        if (ConfidenceThreshold is < 0 or > 1 || MaximumEvidence is < 1 or > 50 ||
            string.IsNullOrWhiteSpace(InsufficientEvidenceText) || string.IsNullOrWhiteSpace(SystemFailureText) || string.IsNullOrWhiteSpace(SensitiveHandoffText) ||
            SensitiveTerms.Count == 0 || SensitiveTerms.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("Grounded answer options are invalid.");
    }
}
