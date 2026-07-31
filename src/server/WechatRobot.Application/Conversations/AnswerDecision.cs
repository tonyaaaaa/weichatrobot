using WechatRobot.Application.Groups;
using WechatRobot.Application.Knowledge;
using WechatRobot.Application.Models;
using WechatRobot.Application.Memory;

namespace WechatRobot.Application.Conversations;

public enum AnswerDecisionKind { Answer, Clarification, InsufficientEvidence, SystemFailure }

public sealed record AnswerDecision(AnswerDecisionKind Kind, string GroupText);
public sealed record RetrievalEvidence(Guid DocumentId, Guid VersionId, Guid ChunkId, int? PageNumber, double Similarity,
    IReadOnlyList<Guid> TagIds, string DocumentTitle, string Text, string? SourceUri = null, string? SourceFileName = null);
public sealed record RetrievalAuditDraft(IReadOnlyList<RetrievalEvidence> Evidence, double ConfidenceThreshold, double? ConfidenceValue,
    string ContextPolicy, string Decision, string? FailureCode = null, string InputSummaryJson = "{}",
    string AnswerSource = "none", string? WebSearchFailureCode = null,
    IReadOnlyList<ChatSource>? WebSearchSources = null,
    MemoryRecallResult? MemoryRecall = null,
    Guid? FixedReplyTemplateId = null,
    int? FixedReplyTemplateVersion = null);
public sealed record GroundedAnswerResult(AnswerDecision Decision, RetrievalAuditDraft Audit, string? UpdatedSummary = null, bool ResetContextBeforeCurrent = false);
public sealed record GroundedAnswerRequest(Guid MessageId, Guid GroupProfileId, string SessionScopeKey, string Question,
    IReadOnlyList<Guid> AllowedTagIds, ConversationContextResult Context, GroupContextSettings ContextPolicy,
    ModelProviderConfiguration ChatConfiguration, RetrievalQueryResult? RetrievalQuery = null, Guid? ModelConfigurationId = null,
    string? DegradationReason = null, string? SummaryFailureCode = null,
    GroupAnswerFallbackSettings? AnswerFallback = null,
    Guid? RobotConfigId = null,
    string? SubjectKey = null,
    string? SenderDisplayName = null,
    QueryRewriteAudit? QueryRewriteAudit = null);

public interface IRetrievalEvidenceProvider
{
    Task<KnowledgeTagScope> ResolveScopeAsync(IReadOnlyList<Guid> requestedTagIds, CancellationToken token);
    Task<IReadOnlyList<RetrievalEvidence>> RetrieveAsync(string question, KnowledgeTagScope scope, int limit, CancellationToken token);
}

public sealed class RetrievalUnavailableException(string message, Exception? innerException = null) : Exception(message, innerException);
public enum NoEvidencePolicy { InsufficientEvidence, Clarification }

public sealed record GroundedAnswerOptions(
    double ConfidenceThreshold = .7,
    int MaximumEvidence = 8,
    string InsufficientEvidenceText = "暂时没有找到可靠答案，请联系工作人员。",
    string SystemFailureText = "系统暂时不可用，请稍后再试。",
    string SensitiveQuestionText = "该问题无法由机器人处理，请联系工作人员。")
{
    public const string SectionName = "GroundedAnswer";
    public IReadOnlyList<string> SensitiveTerms { get; init; } = ["密码", "验证码", "银行卡", "转账", "汇款", "身份证号"];
    public NoEvidencePolicy NoEvidencePolicy { get; init; } = NoEvidencePolicy.InsufficientEvidence;
    public string ClarificationText { get; init; } = "请补充问题细节，我会重新核对。";
    public string UnsafeOutputText { get; init; } = "请补充问题细节，我会重新核对。";

    public void Validate()
    {
        if (ConfidenceThreshold is < 0 or > 1 || MaximumEvidence is < 1 or > 50 ||
            string.IsNullOrWhiteSpace(InsufficientEvidenceText) || string.IsNullOrWhiteSpace(SystemFailureText) || string.IsNullOrWhiteSpace(SensitiveQuestionText) ||
            SensitiveTerms.Count == 0 || SensitiveTerms.Any(string.IsNullOrWhiteSpace) || string.IsNullOrWhiteSpace(ClarificationText) ||
            string.IsNullOrWhiteSpace(UnsafeOutputText) || !Enum.IsDefined(NoEvidencePolicy))
            throw new InvalidOperationException("Grounded answer options are invalid.");
    }
}
