using WechatRobot.Application.Conversations;
using WechatRobot.Application.Handoffs;
using WechatRobot.Application.Groups;
using WechatRobot.Application.Models;

namespace WechatRobot.UnitTests.Handoffs;

public sealed class HandoffTriggerTests
{
    [Theory]
    [InlineData("请转人工客服", AnswerDecisionKind.Answer, 0, "explicit_transfer")]
    [InlineData("普通问题", AnswerDecisionKind.Handoff, 0, "policy_handoff")]
    [InlineData("普通问题", AnswerDecisionKind.SystemFailure, 3, "repeated_system_failure")]
    public void Supported_triggers_create_structured_reason(string question, AnswerDecisionKind decision, int failures, string expected)
    {
        var result = new HandoffTriggerEvaluator(new HandoffTriggerOptions(["转人工", "人工客服"], 3))
            .Evaluate(question, decision, failures);

        Assert.Equal(expected, result?.ReasonCode);
    }

    [Fact]
    public void A_single_system_failure_does_not_transfer()
    {
        Assert.Null(new HandoffTriggerEvaluator(new HandoffTriggerOptions(["转人工"], 3))
            .Evaluate("普通问题", AnswerDecisionKind.SystemFailure, 1));
    }

    [Theory]
    [InlineData(HandoffPausePolicy.Group, "stable-sender", HandoffPauseScope.Group, null, false)]
    [InlineData(HandoffPausePolicy.Sender, "stable-sender", HandoffPauseScope.Sender, "stable-sender", false)]
    [InlineData(HandoffPausePolicy.Sender, null, HandoffPauseScope.Group, null, true)]
    public async Task Automatic_handoff_uses_persisted_policy_and_safely_degrades_missing_sender_identity(
        HandoffPausePolicy policy,
        string? stableSenderId,
        HandoffPauseScope expectedScope,
        string? expectedStableSenderId,
        bool expectedDegradation)
    {
        var store = new CapturingStore();
        var orchestrator = new HandoffOrchestrator(new HandoffService(store, TimeProvider.System), store,
            new HandoffTriggerEvaluator(new HandoffTriggerOptions(["转人工"], 3)),
            new HandoffTriggerOptions(["转人工"], 3));
        var request = Request(policy, stableSenderId);
        var result = new GroundedAnswerResult(new(AnswerDecisionKind.Handoff, string.Empty),
            new([], .7, null, "policy", "Handoff", "policy_handoff"));

        Assert.True(await orchestrator.HandleDecisionAsync(request, result, TestContext.Current.CancellationToken));

        Assert.NotNull(store.Command);
        Assert.Equal(expectedScope, store.Command!.PauseScope);
        Assert.Equal(expectedStableSenderId, store.Command.StableSenderId);
        Assert.Equal(expectedDegradation, store.Command.EvidenceJson.Contains("stable_sender_id_unavailable_group_pause", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Handoff_evidence_excludes_document_text_titles_and_source_uris()
    {
        var store = new CapturingStore();
        var orchestrator = new HandoffOrchestrator(new HandoffService(store, TimeProvider.System), store,
            new HandoffTriggerEvaluator(new HandoffTriggerOptions(["转人工"], 3)),
            new HandoffTriggerOptions(["转人工"], 3));
        var marker = "SENSITIVE-HANDOFF-MARKER";
        var result = new GroundedAnswerResult(new(AnswerDecisionKind.Handoff, string.Empty),
            new([new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 2, .8, [Guid.NewGuid()], marker, marker, $"https://example.invalid/{marker}")],
                .7, .8, "policy", "Handoff", "policy_handoff"));

        Assert.True(await orchestrator.HandleDecisionAsync(Request(HandoffPausePolicy.Group, "stable"), result,
            TestContext.Current.CancellationToken));

        Assert.NotNull(store.Command);
        Assert.DoesNotContain(marker, store.Command!.EvidenceJson, StringComparison.Ordinal);
        Assert.Contains("DocumentId", store.Command.EvidenceJson, StringComparison.Ordinal);
    }

    private static ConversationProcessingRequest Request(HandoffPausePolicy policy, string? stableSenderId)
    {
        var messageId = Guid.NewGuid();
        return new(messageId, Guid.NewGuid(), "robot", Guid.NewGuid(), "Support", "Alice", stableSenderId,
            new ConversationScope(stableSenderId is null ? $"stateless:{messageId:N}" : $"sender:{stableSenderId}", stableSenderId is null, null),
            "转人工", DateTime.UtcNow, [], [], null, new GroupContextSettings(false, 6, 30, 3000, true, true),
            new ModelProviderConfiguration("https://fake.invalid", "fake", "fake", TimeSpan.FromSeconds(1), 0))
        {
            HandoffPausePolicy = policy
        };
    }

    private sealed class CapturingStore : IHandoffStore
    {
        public StartHandoffCommand? Command { get; private set; }
        public Task<HandoffRecord> StartAsync(StartHandoffCommand command, DateTime nowUtc, CancellationToken token)
        {
            Command = command;
            return Task.FromResult(new HandoffRecord(Guid.NewGuid(), "WaitingHuman", command.AssigneeUserId, 0));
        }
        public Task<bool> IsPausedAsync(Guid groupProfileId, string? stableSenderId, CancellationToken token) => Task.FromResult(false);
        public Task<int> CountRecentSystemFailuresAsync(Guid groupProfileId, int maximum, CancellationToken token) => Task.FromResult(0);
        public Task RecordUnverifiedWorkToolMessageAsync(Guid handoffId, string externalMessageId, string displayName, string text, DateTime nowUtc, CancellationToken token) => throw new NotSupportedException();
        public Task<KnowledgeCandidateRecord> ResolveAsync(Guid handoffId, Guid authenticatedActorUserId, string finalAnswer, int expectedVersion, DateTime nowUtc, CancellationToken token) => throw new NotSupportedException();
        public Task<HandoffRecord> AssignAsync(Guid handoffId, Guid authenticatedActorUserId, Guid assigneeUserId, int expectedVersion, DateTime nowUtc, CancellationToken token) => throw new NotSupportedException();
        public Task<HandoffRecord> RestoreAiAsync(Guid handoffId, Guid authenticatedActorUserId, int expectedVersion, DateTime nowUtc, CancellationToken token) => throw new NotSupportedException();
        public Task CapturePausedMessageAsync(Guid groupProfileId, string? stableSenderId, Guid conversationMessageId, string displayName, string text, DateTime nowUtc, CancellationToken token) => throw new NotSupportedException();
        public Task<HandoffRecord> StartManualAsync(ManualStartHandoffCommand command, DateTime nowUtc, CancellationToken token) => throw new NotSupportedException();
    }
}
