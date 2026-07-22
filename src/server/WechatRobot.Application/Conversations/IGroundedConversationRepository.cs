using WechatRobot.Application.Groups;
using WechatRobot.Application.Models;

namespace WechatRobot.Application.Conversations;

public sealed record ConversationProcessingRequest(Guid MessageId, Guid RobotConfigId, string WorkToolRobotId, Guid GroupProfileId,
    string GroupName, string SenderExternalUserId, string Question, DateTime ReceivedAtUtc, IReadOnlyList<Guid> AllowedTagIds,
    IReadOnlyList<ConversationHistoryMessage> History, string? Summary, GroupContextSettings ContextPolicy,
    ModelProviderConfiguration ChatConfiguration);

public sealed record ConversationPageItem(Guid Id, Guid GroupProfileId, Guid? ConversationSessionId, string Direction, string Role, string SenderExternalUserId,
    string Text, DateTime CreatedAtUtc);
public sealed record RetrievalAuditPageItem(Guid Id, Guid MessageId, Guid GroupProfileId, string Decision, double ConfidenceThreshold,
    double? ConfidenceValue, string? FailureCode, string EvidenceJson, DateTime CreatedAtUtc);
public sealed record PageResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

public interface IGroundedConversationRepository
{
    Task<ConversationProcessingRequest> LoadForProcessingAsync(Guid messageId, CancellationToken token);
    Task PersistAnswerAndEnqueueAsync(ConversationProcessingRequest request, GroundedAnswerResult result, CancellationToken token);
    Task<int> ClearContextAsync(Guid groupProfileId, string? senderExternalUserId, DateTime clearedAtUtc, CancellationToken token);
    Task<PageResult<ConversationPageItem>> GetHistoryAsync(Guid groupProfileId, int page, int pageSize, CancellationToken token);
    Task<PageResult<RetrievalAuditPageItem>> GetAuditsAsync(Guid groupProfileId, int page, int pageSize, CancellationToken token);
}
