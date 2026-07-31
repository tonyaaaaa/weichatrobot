using WechatRobot.Application.Agents;
using WechatRobot.Application.Conversations;
using WechatRobot.Application.Groups;
using WechatRobot.Application.Jobs;
using WechatRobot.Application.Messaging;

namespace WechatRobot.UnitTests.Messaging;

public sealed class InboundMessageIntentOrderingTests
{
    [Fact]
    public async Task Busy_session_is_rejected_before_invoking_intent_agent()
    {
        var messageId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var repository = new BusyRepository(messageId, groupId);
        var intent = new CountingIntentAgent();
        var processor = new InboundMessageProcessor(
            repository,
            new ConversationContextService(),
            null!,
            null!,
            TimeProvider.System,
            null!,
            intentAgent: intent,
            runtimeOptions: new AgentRuntimeOptions
            {
                IntentRuntimeMode = IntentRuntimeMode.AgentFramework
            });
        var job = new LeasedDurableJob(
            Guid.NewGuid(),
            "ProcessInboundMessage",
            $$"""{"messageId":"{{messageId:D}}","robotConfigId":"{{Guid.NewGuid():D}}","groupName":"测试群","wasMentioned":true}""",
            0,
            "owner");

        await Assert.ThrowsAsync<ConversationSessionBusyException>(() =>
            processor.ProcessAsync(job, TestContext.Current.CancellationToken));

        Assert.Equal(0, intent.CallCount);
    }

    private sealed class CountingIntentAgent : IMessageIntentAgent
    {
        public int CallCount { get; private set; }

        public Task<MessageIntentResult> DecideAsync(
            MessageIntentRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new MessageIntentResult(
                IntentDecision.Reply,
                IntentCategory.DirectedToBot,
                "explicitly_addresses_bot",
                1,
                null));
        }
    }

    private sealed class BusyRepository(Guid messageId, Guid groupId)
        : IGroundedConversationRepository
    {
        public Task<InboundPolicyDecision> EvaluateInboundPolicyAsync(Guid id, string groupName, string? groupRemark, bool wasMentioned, CancellationToken token) =>
            Task.FromResult(new InboundPolicyDecision(messageId, InboundPolicyDecisionKind.Proceed, groupId, null, "{}"));

        public Task<ConversationProcessingRequest> LeaseForProcessingAsync(Guid id, string leaseOwner, DateTime nowUtc, TimeSpan leaseDuration, CancellationToken token) =>
            Task.FromException<ConversationProcessingRequest>(new ConversationSessionBusyException("busy"));

        public Task PersistNoReplyTerminalAsync(InboundPolicyDecision decision, CancellationToken token) => throw new NotSupportedException();
        public Task<ConversationProcessingRequest> LoadForProcessingAsync(Guid id, CancellationToken token) => throw new NotSupportedException();
        public Task<bool> RenewLeaseAsync(Guid sessionId, string leaseOwner, DateTime nowUtc, TimeSpan leaseDuration, CancellationToken token) => throw new NotSupportedException();
        public Task ReleaseLeaseAsync(Guid sessionId, string leaseOwner, CancellationToken token) => throw new NotSupportedException();
        public Task PersistAnswerAndEnqueueAsync(ConversationProcessingRequest request, GroundedAnswerResult result, CancellationToken token) => throw new NotSupportedException();
        public Task<int> ClearGroupContextAsync(Guid groupProfileId, DateTime clearedAtUtc, CancellationToken token) => throw new NotSupportedException();
        public Task<GroupConversationContextSourcePage?> GetGroupContextAsync(Guid groupProfileId, int page, int pageSize, CancellationToken token) => throw new NotSupportedException();
        public Task<ClearConversationContextResult> ClearGroupContextAsync(Guid groupProfileId, int expectedConfigurationVersion, DateTime clearedAtUtc, CancellationToken token) => throw new NotSupportedException();
        public Task<PageResult<ConversationPageItem>> GetHistoryAsync(Guid groupProfileId, int page, int pageSize, CancellationToken token) => throw new NotSupportedException();
        public Task<PageResult<RetrievalAuditPageItem>> GetAuditsAsync(Guid groupProfileId, int page, int pageSize, CancellationToken token) => throw new NotSupportedException();
    }
}
