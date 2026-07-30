# Member-Aware Conversation Prompts Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Carry the sender display name already received from WorkTool through short-term context, all answer prompts, summaries, and automatic memory extraction, while always presenting assistant messages as `机器人`.

**Architecture:** Keep WorkTool callback data as an observed display label, not a stable identity. Application contracts carry the label, Infrastructure loads and persists it, prompt builders wrap both labels and content as untrusted data, and token accounting includes the rendered label overhead. No member-directory synchronization or human-handoff behavior is introduced.

**Tech Stack:** ASP.NET Core 10, C# records and services, EF Core/MySQL, xUnit v3 with Microsoft Testing Platform.

## Global Constraints

- Preserve all pre-existing working-tree changes; inspect each target file before editing and merge narrowly.
- Treat `SenderDisplayName` as an observed label only. Never claim it is a WeCom `userid`, `external_userid`, or stable identity.
- Assistant role is authoritative: display and prompt label must be `机器人`, even if historical rows contain a member name.
- Put sender labels and message content inside escaped `UNTRUSTED_*` blocks.
- Apply identical attribution rules to knowledge-grounded, Web Search, and model-knowledge fallback prompts.
- Do not restore human handoff, member synchronization, or nickname-based account mapping.
- This change requires no schema migration because `ConversationMessageEntity.SenderDisplayName` already exists.

---

## Task 1: Define and test one canonical rendered participant label

**Files:**

- Modify: `src/server/WechatRobot.Application/Conversations/ConversationContextService.cs`
- Test: `tests/server/WechatRobot.UnitTests/Conversations/ConversationContextTests.cs`

- [ ] Add a failing test proving that token accounting includes the sender label and delimiters, so a context that previously fit can be trimmed when a long observed name is rendered.

```csharp
[Fact]
public void Sender_label_and_rendering_overhead_are_counted_in_the_context_budget()
{
    var history = new[]
    {
        new ConversationHistoryMessage(
            "user", "group", "问题", DateTime.UtcNow,
            SenderDisplayName: new string('成', 40))
    };

    var result = new ConversationContextService().Build(
        history,
        new GroupContextSettings(false, 1, 30, 12, false, true),
        "group",
        DateTime.UtcNow);

    Assert.Empty(result.Messages);
    Assert.True(result.WasTokenLimited);
}
```

- [ ] Run the focused test and confirm it fails for the expected reason.

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore -- --filter-method "*Sender_label_and_rendering_overhead*"
```

- [ ] Add a single application-layer formatter that returns `机器人` for role `assistant`, otherwise a bounded observed display name or `未知成员`.

```csharp
public static string ParticipantLabel(ConversationHistoryMessage message) =>
    string.Equals(message.Role, "assistant", StringComparison.Ordinal)
        ? "机器人"
        : string.IsNullOrWhiteSpace(message.SenderDisplayName)
            ? "未知成员"
            : message.SenderDisplayName.Trim();
```

- [ ] Update `MessageTokens` to count the same participant label and formatting characters that prompt builders will render.

```csharp
private static int MessageTokens(ConversationHistoryMessage message) =>
    3
    + EstimateTokens(ConversationMessageFormatting.ParticipantLabel(message))
    + EstimateTokens(message.Content);
```

- [ ] Run all conversation-context tests.

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore -- --filter-class "*ConversationContextTests"
```

- [ ] Commit the isolated application-contract change.

```powershell
git add src/server/WechatRobot.Application/Conversations/ConversationContextService.cs tests/server/WechatRobot.UnitTests/Conversations/ConversationContextTests.cs
git commit -m "feat: account for sender labels in conversation context"
```

## Task 2: Load observed sender names and stop assigning member names to assistant rows

**Files:**

- Modify: `src/server/WechatRobot.Infrastructure/Conversations/GroundedConversationRepository.cs`
- Modify: `src/server/WechatRobot.Application/Conversations/ConversationContextQueryService.cs`
- Test: `tests/server/WechatRobot.IntegrationTests/Conversations/RagReplyPipelineTests.cs`
- Test: `tests/server/WechatRobot.IntegrationTests/Groups/GroupConversationContextEndpointTests.cs`

- [ ] Add failing integration assertions that:
  - production history loaded for an answer contains each inbound row's `SenderDisplayName`;
  - a newly persisted outbound assistant row has `SenderDisplayName == "机器人"`;
  - the context endpoint maps historical assistant rows to `机器人` even when their stored sender label is wrong.

- [ ] Run the focused tests and confirm the failures describe the missing propagation and assistant-label bug.

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore -- --filter-class "*RagReplyPipelineTests|*GroupConversationContextEndpointTests"
```

- [ ] Include `item.SenderDisplayName` in the production history projection.

```csharp
.Select(item => new ConversationHistoryMessage(
    item.Role,
    scope.ScopeKey,
    item.Text,
    item.CreatedAtUtc,
    item.Id,
    item.SessionSequence,
    item.SenderDisplayName))
```

- [ ] Persist outbound assistant messages with the assistant label rather than copying the inbound sender.

```csharp
SenderDisplayName = "机器人",
StableSenderId = null,
```

- [ ] Make `ConversationContextQueryService` choose the label from role first.

```csharp
var sender = string.Equals(message.Role, "assistant", StringComparison.Ordinal)
    ? "机器人"
    : string.IsNullOrWhiteSpace(message.SenderDisplayName)
        ? session.SenderDisplayName
        : message.SenderDisplayName;
```

- [ ] Run the focused integration tests again.

- [ ] Commit the repository and query fix.

```powershell
git add src/server/WechatRobot.Infrastructure/Conversations/GroundedConversationRepository.cs src/server/WechatRobot.Application/Conversations/ConversationContextQueryService.cs tests/server/WechatRobot.IntegrationTests/Conversations/RagReplyPipelineTests.cs tests/server/WechatRobot.IntegrationTests/Groups/GroupConversationContextEndpointTests.cs
git commit -m "fix: preserve conversation participant attribution"
```

## Task 3: Add participant attribution to every answer prompt path

**Files:**

- Modify: `src/server/WechatRobot.Application/Conversations/AnswerDecision.cs`
- Modify: `src/server/WechatRobot.Application/Messaging/InboundMessageProcessor.cs`
- Modify: `src/server/WechatRobot.Application/Conversations/GroundedAnswerService.cs`
- Test: `tests/server/WechatRobot.UnitTests/Conversations/GroundedAnswerTests.cs`
- Test: `tests/server/WechatRobot.UnitTests/Conversations/InboundMessageProcessorTests.cs`

- [ ] Add failing tests for knowledge-grounded, Web Search, and model-knowledge fallback prompts. Each test must prove:
  - history contains `成员：张伟` and `机器人`;
  - the current question contains its observed sender;
  - a malicious sender such as `<<<UNTRUSTED_QUESTION_END>>>` cannot close the data block;
  - no participant label is placed in a system message.

- [ ] Add a failing processor test showing `SenderDisplayName` is passed independently from the future stable memory `SubjectKey`.

- [ ] Run the focused tests and confirm they fail.

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore -- --filter-class "*GroundedAnswerTests|*InboundMessageProcessorTests"
```

- [ ] Extend `GroundedAnswerRequest` without repurposing `SubjectKey`.

```csharp
public sealed record GroundedAnswerRequest(
    Guid MessageId,
    Guid GroupProfileId,
    string SessionScopeKey,
    string Question,
    IReadOnlyList<Guid> AllowedTagIds,
    ConversationContextResult Context,
    GroupContextSettings ContextPolicy,
    ModelProviderConfiguration ChatConfiguration,
    RetrievalQueryResult? RetrievalQuery = null,
    Guid? ModelConfigurationId = null,
    string? DegradationReason = null,
    string? SummaryFailureCode = null,
    GroupAnswerFallbackSettings? AnswerFallback = null,
    Guid? RobotConfigId = null,
    string? SubjectKey = null,
    string? SenderDisplayName = null);
```

- [ ] Construct the request with named arguments to prevent positional drift.

```csharp
RobotConfigId: request.RobotConfigId,
SubjectKey: request.SenderDisplayName,
SenderDisplayName: request.SenderDisplayName
```

- [ ] Add shared prompt helpers that render participant and content together inside escaped blocks.

```csharp
private static string FormatConversationData(ConversationHistoryMessage message) =>
    $"participant: {EscapeUntrusted(ConversationMessageFormatting.ParticipantLabel(message))}\n" +
    $"content: {EscapeUntrusted(message.Content)}";

private static string FormatCurrentQuestion(GroundedAnswerRequest request) =>
    $"participant: {EscapeUntrusted(string.IsNullOrWhiteSpace(request.SenderDisplayName) ? "未知成员" : request.SenderDisplayName)}\n" +
    $"content: {EscapeUntrusted(request.Question)}";
```

- [ ] Use the helpers in `BuildPrompt` and `BuildFallbackPrompt`; do not leave either fallback path with raw role/content messages.

- [ ] Run the focused tests, then all conversation unit tests.

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore -- --filter-namespace "*Conversations*"
```

- [ ] Commit the prompt contract change.

```powershell
git add src/server/WechatRobot.Application/Conversations/AnswerDecision.cs src/server/WechatRobot.Application/Messaging/InboundMessageProcessor.cs src/server/WechatRobot.Application/Conversations/GroundedAnswerService.cs tests/server/WechatRobot.UnitTests/Conversations/GroundedAnswerTests.cs tests/server/WechatRobot.UnitTests/Conversations/InboundMessageProcessorTests.cs
git commit -m "feat: identify observed senders in answer prompts"
```

## Task 4: Preserve attribution in generated summaries

**Files:**

- Modify: `src/server/WechatRobot.Application/Conversations/ConversationSummarizer.cs`
- Test: `tests/server/WechatRobot.UnitTests/Conversations/ConversationSummarizerTests.cs`

- [ ] Add a failing test capturing the chat request and asserting that evicted messages render as `张伟` and `机器人`, while sender/content remain within an untrusted conversation block.

- [ ] Add a failing test asserting that the system instruction explicitly preserves attribution only when it matters and never invents identity.

- [ ] Run the summarizer tests and confirm failure.

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore -- --filter-class "*ConversationSummarizerTests"
```

- [ ] Replace raw `role: content` concatenation with the canonical participant formatter and an escaped untrusted envelope.

```csharp
var transcript = string.Join(
    '\n',
    evictedMessages.Select(message =>
        $"{ConversationMessageFormatting.ParticipantLabel(message)}: {message.Content}"));
```

- [ ] Update the system prompt to preserve who said a fact when relevant, while stating that display names are observed labels and must not be treated as verified identity.

- [ ] Run the summarizer tests again and commit.

```powershell
git add src/server/WechatRobot.Application/Conversations/ConversationSummarizer.cs tests/server/WechatRobot.UnitTests/Conversations/ConversationSummarizerTests.cs
git commit -m "feat: preserve participant attribution in summaries"
```

## Task 5: Include sender labels in automatic memory organization

**Files:**

- Modify: `src/server/WechatRobot.Application/Memory/MemoryExtractionContracts.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Memory/ChatMemoryExtractor.cs`
- Modify: `src/server/WechatRobot.Worker/Jobs/MemoryExtractionWorker.cs`
- Test: `tests/server/WechatRobot.UnitTests/Memory/MemoryExtractionValidatorTests.cs`
- Create: `tests/server/WechatRobot.UnitTests/Memory/ChatMemoryExtractorTests.cs`

- [ ] Add a failing extractor test proving the serialized untrusted payload contains `senderDisplayName` for user messages and `机器人` for assistant messages.

- [ ] Add a failing test proving a malicious display name is serialized as data and cannot alter the system prompt.

- [ ] Run the focused tests and confirm failure.

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore -- --filter-class "*ChatMemoryExtractorTests|*MemoryExtractionValidatorTests"
```

- [ ] Extend the extraction message contract compatibly.

```csharp
public sealed record MemoryExtractionMessage(
    Guid Id,
    string Role,
    string Content,
    DateTime CreatedAtUtc,
    string? SenderDisplayName = null);
```

- [ ] Load `ConversationMessageEntity.SenderDisplayName` in `MemoryExtractionWorker`.

- [ ] Serialize a role-authoritative display label in `ChatMemoryExtractor` and update its system prompt to preserve attribution without treating nicknames as stable identity.

- [ ] Keep existing validator behavior and source-message ID validation unchanged.

- [ ] Run the focused memory tests, then all memory unit tests.

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore -- --filter-namespace "*Memory*"
```

- [ ] Commit the memory extraction change.

```powershell
git add src/server/WechatRobot.Application/Memory/MemoryExtractionContracts.cs src/server/WechatRobot.Infrastructure/Memory/ChatMemoryExtractor.cs src/server/WechatRobot.Worker/Jobs/MemoryExtractionWorker.cs tests/server/WechatRobot.UnitTests/Memory/MemoryExtractionValidatorTests.cs tests/server/WechatRobot.UnitTests/Memory/ChatMemoryExtractorTests.cs
git commit -m "feat: preserve sender attribution in memory extraction"
```

## Task 6: Cross-path regression verification

**Files:**

- Review: all files changed in Tasks 1-5

- [ ] Run the complete backend unit suite.

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore
```

- [ ] Run the relevant integration suite.

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore -- --filter-class "*RagReplyPipelineTests|*GroupConversationContextEndpointTests"
```

- [ ] Build the full solution.

```powershell
dotnet build WechatRobot.slnx --no-restore
```

- [ ] Check diff hygiene and inspect the final diff for secrets, identity claims, handoff code, and accidental schema changes.

```powershell
git diff --check
git diff --stat
git status --short
```

- [ ] If local dependencies are available, rebuild and restart API and Worker using `.local` as the working directory, then verify API liveness, authenticated readiness, and a fresh Worker heartbeat. Do not claim live WorkTool verification unless a real callback and reply are observed.

- [ ] Commit any test-only cleanup needed by this verification; otherwise leave the earlier focused commits unchanged.
