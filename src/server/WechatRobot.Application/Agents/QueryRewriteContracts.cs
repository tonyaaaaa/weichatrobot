using WechatRobot.Application.Conversations;
using WechatRobot.Application.Models;

namespace WechatRobot.Application.Agents;

public enum QueryRewriteDecision
{
    Search,
    Clarification,
    Failure
}

public enum QueryRewriteReasonCode
{
    StandaloneQuestion,
    ContextualFollowUp,
    AmbiguousReference,
    ConflictingContext,
    InvalidOutput,
    ProviderTimeout,
    ProviderFailure
}

public sealed record QueryRewriteRequest(
    Guid MessageId,
    Guid ConversationSessionId,
    ConversationChannelType ChannelType,
    Guid? GroupProfileId,
    Guid RobotConfigId,
    string SessionScopeKey,
    string SenderDisplayName,
    string CurrentQuestion,
    ConversationContextResult Context,
    ModelProviderConfiguration ChatConfiguration,
    Guid ModelConfigurationId);

public sealed record QueryRewriteResult(
    QueryRewriteDecision Decision,
    string? StandaloneQuery,
    string? ClarificationQuestion,
    QueryRewriteReasonCode ReasonCode,
    int DurationMilliseconds = 0,
    string? FailureCode = null);

public interface IQueryRewriteAgent
{
    Task<QueryRewriteResult> RewriteAsync(
        QueryRewriteRequest request,
        CancellationToken cancellationToken);
}
