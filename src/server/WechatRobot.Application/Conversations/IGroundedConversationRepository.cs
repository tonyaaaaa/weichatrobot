using WechatRobot.Application.Groups;
using WechatRobot.Application.Models;

namespace WechatRobot.Application.Conversations;

public sealed record ConversationProcessingRequest(Guid MessageId, Guid RobotConfigId, string WorkToolRobotId, Guid GroupProfileId,
    string GroupName, string SenderDisplayName, string? StableSenderId, ConversationScope Scope, string Question, DateTime ReceivedAtUtc, IReadOnlyList<Guid> AllowedTagIds,
    IReadOnlyList<ConversationHistoryMessage> History, string? Summary, GroupContextSettings ContextPolicy,
    ModelProviderConfiguration ChatConfiguration, Guid ModelConfigurationId = default, Guid ConversationSessionId = default,
    string? SessionLeaseOwner = null, int SessionVersion = 0);

public sealed record ConversationPageItem(Guid Id, Guid GroupProfileId, Guid? ConversationSessionId, string Direction, string Role, string SenderDisplayName, string? StableSenderId,
    string Text, DateTime CreatedAtUtc);
public sealed record RetrievalAuditPageItem(Guid Id, Guid MessageId, Guid GroupProfileId, string Decision, double ConfidenceThreshold,
    double? ConfidenceValue, string? FailureCode, string EvidenceJson, DateTime CreatedAtUtc);
public sealed record PageResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

public interface IGroundedConversationRepository
{
    Task<ConversationProcessingRequest> LoadForProcessingAsync(Guid messageId, CancellationToken token);
    Task<ConversationProcessingRequest> LeaseForProcessingAsync(Guid messageId, string leaseOwner, DateTime nowUtc, TimeSpan leaseDuration, CancellationToken token);
    Task<bool> RenewLeaseAsync(Guid sessionId, string leaseOwner, DateTime nowUtc, TimeSpan leaseDuration, CancellationToken token);
    Task ReleaseLeaseAsync(Guid sessionId, string leaseOwner, CancellationToken token);
    Task PersistAnswerAndEnqueueAsync(ConversationProcessingRequest request, GroundedAnswerResult result, CancellationToken token);
    Task<int> ClearGroupContextAsync(Guid groupProfileId, DateTime clearedAtUtc, CancellationToken token);
    Task<PageResult<ConversationPageItem>> GetHistoryAsync(Guid groupProfileId, int page, int pageSize, CancellationToken token);
    Task<PageResult<RetrievalAuditPageItem>> GetAuditsAsync(Guid groupProfileId, int page, int pageSize, CancellationToken token);
}

public sealed class ConversationSessionBusyException(string message) : Exception(message);
public sealed class ConversationSessionOwnershipLostException(string message) : Exception(message);
