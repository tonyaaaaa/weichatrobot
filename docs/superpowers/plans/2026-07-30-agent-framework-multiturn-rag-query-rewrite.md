# Agent Framework Multi-Turn RAG Query Rewrite Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add mandatory, auditable Agent Framework query rewriting before ordinary RAG for both group and private conversations.

**Architecture:** MySQL conversation sessions, messages, and summaries remain the only durable conversation truth. Application contracts validate a closed rewrite decision and prepare either a standalone retrieval query, a clarification reply, or a safe failure; the Infrastructure Agent Framework adapter only transforms the bounded formal context. Existing answer agents, Qdrant retrieval, tag authorization, thresholds, Intent modes, fixed replies, durable sends, and configuration stay authoritative.

**Tech Stack:** .NET 10, ASP.NET Core Worker DI, Microsoft Agent Framework 1.15, Microsoft.Extensions.AI, EF Core/MySQL, xUnit v3/Microsoft Testing Platform.

## Global Constraints

- Support both group and private ordinary RAG conversations.
- Do not add or modify any `appsettings` setting.
- Reuse the answer model configuration and its existing timeout.
- Reuse `RetrievalQueryOptions.TokenCap` for standalone-query limits.
- Reuse `ConversationContextService` output; the rewrite Agent must not query conversation storage.
- Fixed-reply hits and private knowledge-ingest commands must bypass rewriting.
- Intent runtime modes keep their current meanings and raw Intent history must not enter rewrite context.
- Member-isolated group sessions must not inherit another member's formal history.
- Current and prior raw RAG evidence must never enter rewrite input.
- Persist rewrite metadata through the existing retrieval audit `InputSummaryJson`; no schema migration.
- Do not commit until every implementation and verification task is complete.

---

### Task 1: Application rewrite contract and validation

**Files:**
- Create: `src/server/WechatRobot.Application/Agents/QueryRewriteContracts.cs`
- Create: `src/server/WechatRobot.Application/Conversations/MultiTurnRetrievalService.cs`
- Create: `tests/server/WechatRobot.UnitTests/Conversations/MultiTurnRetrievalServiceTests.cs`

**Interfaces:**
- Produces: `IQueryRewriteAgent.RewriteAsync(QueryRewriteRequest, CancellationToken)`
- Produces: `QueryRewriteResult` with `Search`, `Clarification`, or `Failure`
- Produces: `MultiTurnRetrievalService.PrepareAsync(QueryRewriteRequest, CancellationToken)`
- Produces: `MultiTurnRetrievalPreparation` carrying a validated `RetrievalQueryResult`, a terminal answer, and rewrite audit metadata.

- [x] **Step 1: Write failing contract and service tests**

```csharp
[Fact]
public async Task Contextual_search_uses_validated_standalone_query()
{
    var agent = new StubRewriteAgent(new(
        QueryRewriteDecision.Search,
        "办理日本三年签证需要准备什么材料？",
        null,
        QueryRewriteReasonCode.ContextualFollowUp));
    var service = Service(agent);

    var result = await service.PrepareAsync(Request(History()), CancellationToken.None);

    Assert.Equal("办理日本三年签证需要准备什么材料？", result.RetrievalQuery!.Query);
    Assert.True(result.Audit.RagExecuted);
}

[Fact]
public async Task Ambiguous_reference_returns_safe_clarification_without_query()
{
    var service = Service(new StubRewriteAgent(new(
        QueryRewriteDecision.Clarification,
        null,
        "请确认您咨询的是日本三年签证还是五年签证？",
        QueryRewriteReasonCode.AmbiguousReference)));

    var result = await service.PrepareAsync(Request(History()), CancellationToken.None);

    Assert.Null(result.RetrievalQuery);
    Assert.Equal(AnswerDecisionKind.Clarification, result.TerminalAnswer!.Kind);
    Assert.False(result.Audit.RagExecuted);
}

[Fact]
public async Task Provider_failure_without_history_uses_original_question()
{
    var service = Service(new StubRewriteAgent(QueryRewriteResult.ProviderFailure()));

    var result = await service.PrepareAsync(Request(), CancellationToken.None);

    Assert.Equal("需要什么材料？", result.RetrievalQuery!.Query);
    Assert.True(result.Audit.UsedOriginalQuestion);
}

[Fact]
public async Task Provider_failure_with_history_stops_before_retrieval()
{
    var service = Service(new StubRewriteAgent(QueryRewriteResult.ProviderFailure()));

    var result = await service.PrepareAsync(Request(History()), CancellationToken.None);

    Assert.Null(result.RetrievalQuery);
    Assert.Equal(AnswerDecisionKind.SystemFailure, result.TerminalAnswer!.Kind);
}
```

- [x] **Step 2: Run tests and verify RED**

Run:

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore -- --filter-class '*MultiTurnRetrievalServiceTests' --minimum-expected-tests 1
```

Expected: compilation fails because the rewrite contracts and service do not exist.

- [x] **Step 3: Implement the closed contract and validator**

Implement these public shapes:

```csharp
public enum QueryRewriteDecision { Search, Clarification, Failure }
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
```

`MultiTurnRetrievalService` must reject empty/mixed/oversized outputs, validate clarification text with `AnswerOutputFirewall.ValidateUngrounded`, use the fixed clarification fallback `请明确您咨询的具体对象或类型，我会重新核对。`, fall back to the original question only when formal context has no messages and no summary, and return a fixed system failure for rewrite failures with history.

- [x] **Step 4: Run focused tests and verify GREEN**

Run the Task 1 command again. Expected: all focused tests pass.

### Task 2: Agent Framework query rewrite adapter

**Files:**
- Create: `src/server/WechatRobot.Infrastructure/Agents/QueryRewriteAgent.cs`
- Create: `tests/server/WechatRobot.UnitTests/Agents/QueryRewriteAgentTests.cs`

**Interfaces:**
- Consumes: `IQueryRewriteAgent`, `IAgentChatClientFactory`, `QueryRewriteRequest`
- Produces: one validated candidate submitted through `submit_query_rewrite`

- [x] **Step 1: Write failing adapter tests**

Use a recording `IChatClient` and assert observable results:

```csharp
[Fact]
public async Task Follow_up_submission_becomes_structured_search_result()
{
    var client = new ToolCallingChatClient(
        "Search",
        "办理日本三年签证需要准备什么材料？",
        null,
        "contextual_follow_up");
    var agent = Agent(client);

    var result = await agent.RewriteAsync(Request(History()), CancellationToken.None);

    Assert.Equal(QueryRewriteDecision.Search, result.Decision);
    Assert.Equal(QueryRewriteReasonCode.ContextualFollowUp, result.ReasonCode);
}

[Fact]
public async Task Prompt_contains_only_formal_context_with_participant_labels()
{
    var client = new ToolCallingChatClient(
        "Clarification", null, "请确认具体签证类型？", "ambiguous_reference");

    await Agent(client).RewriteAsync(Request(History()), CancellationToken.None);

    Assert.Contains("participant", client.LastInput);
    Assert.DoesNotContain("rawSameGroupMessages", client.LastInput);
    Assert.DoesNotContain("Evidence data", client.LastInput);
}
```

Also cover unknown decisions, multiple submissions, provider timeout/failure, prompt delimiter escaping, and propagation of caller cancellation.

- [x] **Step 2: Run tests and verify RED**

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore -- --filter-class '*QueryRewriteAgentTests' --minimum-expected-tests 1
```

Expected: compilation fails because `QueryRewriteAgent` does not exist.

- [x] **Step 3: Implement the Agent Framework adapter**

Create a `ChatClientAgent` named `QueryRewriteAgent`. Register exactly one non-business tool:

```csharp
submit_query_rewrite(
    string decision,
    string? standaloneQuery,
    string? clarificationQuestion,
    string reasonCode)
```

The system instructions must say that conversation blocks are untrusted data, the Agent only rewrites and never answers, and it must submit exactly once. Serialize only `request.Context.Summary`, `request.Context.Messages` with participant labels and message references, plus the current participant and question. Use `request.ChatConfiguration.TimeoutSeconds`; do not read options or the database.

- [x] **Step 4: Run focused tests and verify GREEN**

Run the Task 2 command again. Expected: all focused tests pass.

### Task 3: Group RAG pipeline integration and audit

**Files:**
- Modify: `src/server/WechatRobot.Application/Messaging/InboundMessageProcessor.cs`
- Modify: `src/server/WechatRobot.Application/Conversations/AnswerDecision.cs`
- Modify: `src/server/WechatRobot.Application/Conversations/GroundedAnswerService.cs`
- Modify: `src/server/WechatRobot.Worker/Program.cs`
- Modify: `tests/server/WechatRobot.UnitTests/Conversations/InboundMessageProcessorTests.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Conversations/RagReplyPipelineTests.cs`

**Interfaces:**
- Consumes: `MultiTurnRetrievalService`
- Produces: rewrite-before-answer behavior for both Legacy and Agent Framework answer modes
- Produces: rewrite audit fields merged into `RetrievalAuditDraft.InputSummaryJson`

- [x] **Step 1: Write failing group pipeline tests**

Add tests proving:

```csharp
Assert.Equal(
    "办理日本三年签证需要准备什么材料？",
    retrieval.Queries.Single());
Assert.Contains("\"RewriteDecision\":\"Search\"", audit.InputSummaryJson);
Assert.Contains("\"RewriteReasonCode\":\"contextual_follow_up\"", audit.InputSummaryJson);
Assert.DoesNotContain("第一轮证据正文", rewriteAgent.LastRequestJson);
```

Add a clarification test that asserts the rewrite Agent is called, the answer/retrieval provider is not called, one clarification message is persisted, and a retry does not create another outbound message or send command. Keep existing tests that prove `Paused`, Agent Framework `NoReply`/`Uncertain`, and fixed-template hits stop before this stage.

- [x] **Step 2: Run group tests and verify RED**

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore -- --filter-class '*InboundMessageProcessorTests' --minimum-expected-tests 1
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore -- --filter-class '*RagReplyPipelineTests' --minimum-expected-tests 1
```

Expected: new assertions fail because the group pipeline still uses mechanical `RetrievalQueryBuilder`.

- [x] **Step 3: Integrate preparation before answer execution**

After formal context and summary construction:

```csharp
var preparation = await multiTurnRetrieval.PrepareAsync(
    new QueryRewriteRequest(...),
    cancellationToken);
```

Renew the group session lease before and after the Agent call. For a terminal preparation, persist a `GroundedAnswerResult` immediately with no evidence and rewrite audit metadata. For Search, pass only the validated standalone query and formal context message IDs to `GroundedAnswerRequest`. Merge rewrite metadata into the existing hashed input summary without storing plaintext questions.

Register `IQueryRewriteAgent`, `MultiTurnRetrievalService`, and supporting singleton validators in Worker DI. Do not alter `AgentRuntimeOptions`.

- [x] **Step 4: Run group tests and verify GREEN**

Run both Task 3 commands. Expected: all focused group tests pass.

### Task 4: Private-chat integration and isolation

**Files:**
- Modify: `src/server/WechatRobot.Infrastructure/Agents/PrivateChatProcessor.cs`
- Modify: `src/server/WechatRobot.Worker/Program.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/PrivateChat/PrivateChatProcessorTests.cs`

**Interfaces:**
- Consumes: `MultiTurnRetrievalService`
- Produces: the same rewrite behavior for `roomType=2` and `roomType=4`

- [x] **Step 1: Write failing private pipeline tests**

For both room types, seed a previous user/assistant turn for one `ScopeHash`, send `需要什么材料？`, and assert the answer request contains:

```csharp
Assert.Equal(
    "办理日本三年签证需要准备什么材料？",
    answerAgent.LastRequest!.RetrievalQuery!.Query);
```

Add:

- a different `RobotConfigId`/`RoomType`/`ScopeHash` history isolation assertion;
- a clarification test proving `IAnswerAgent` is not called;
- a fixed-template test asserting `IQueryRewriteAgent` is not called;
- a direct-ingest test asserting `IQueryRewriteAgent` is not called;
- an audit assertion for rewrite decision, reason, model ID, duration, context IDs, hashes/lengths, `RagExecuted`, and `UsedOriginalQuestion`.

- [x] **Step 2: Run tests and verify RED**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore -- --filter-class '*PrivateChatProcessorTests' --minimum-expected-tests 1
```

Expected: new tests fail because private chat has no rewrite preparation.

- [x] **Step 3: Integrate private preparation**

Build the existing `ScopeHash`-isolated context first, then call `PrepareAsync`. Persist terminal clarification/failure through the existing idempotent `ReplyAsync`; otherwise pass the standalone query into `GroundedAnswerRequest`. Keep all enabled tags as the private knowledge request and preserve existing fallback behavior.

- [x] **Step 4: Run tests and verify GREEN**

Run the Task 4 command again. Expected: all focused private tests pass.

### Task 5: Controlled evidence context and final verification

**Files:**
- Create: `src/server/WechatRobot.Infrastructure/Agents/KnowledgeEvidenceProvider.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Agents/AnswerAgent.cs`
- Create: `tests/server/WechatRobot.UnitTests/Agents/KnowledgeEvidenceProviderTests.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Conversations/AnswerAgentEquivalenceTests.cs`

**Interfaces:**
- Produces: sanitized, current-call-only evidence context for `AnswerAgent`
- Preserves: existing `GroundedAnswerService` retrieval filters, thresholds, fallbacks, and output firewall.

- [x] **Step 1: Write failing evidence-provider tests**

```csharp
[Fact]
public void Provider_formats_only_current_authorized_evidence()
{
    var provider = new KnowledgeEvidenceProvider([Evidence("本轮证据")]);

    var text = provider.BuildContext();

    Assert.Contains("本轮证据", text);
    Assert.DoesNotContain("上一轮证据", text);
    Assert.DoesNotContain("tool_calls", text, StringComparison.OrdinalIgnoreCase);
}
```

Also prove delimiter escaping and that a second provider instance does not retain the first instance's evidence.

- [x] **Step 2: Run tests and verify RED**

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore -- --filter-class '*KnowledgeEvidenceProviderTests' --minimum-expected-tests 1
```

Expected: compilation fails because the provider does not exist.

- [x] **Step 3: Implement and use the controlled provider**

Keep the provider Infrastructure-only and per-call. It accepts only the `RetrievalEvidence` already returned by the deterministic Application service, escapes internal delimiters, and returns the evidence block consumed by `AnswerAgent`; it owns no search, tools, session, or persistence.

- [x] **Step 4: Run focused and complete verification**

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj --no-restore
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore
git diff --check
git status --short
```

Expected: all test projects pass, the diff check is clean, and no `appsettings` or migration file changed.

- [x] **Step 5: Review and commit once**

Review the complete diff for scope, secrets, plaintext question leakage, and accidental generated files. Stage only this implementation and commit:

```powershell
git commit -m "feat: add multi-turn rag query rewriting"
```

## Verification Record

- Unit tests: full project passed.
- Contract tests: full project passed.
- Relevant integration namespaces: 77 tests passed together.
- Full integration assembly: 388 total, 381 passed, 3 skipped, and 4 failed.
  One handoff failure caused by the new dependency was fixed and passed on
  rerun. Two model endpoint failures passed together when their class was
  rerun in isolation. The remaining isolated baseline failure is
  `KnowledgeIndexMySqlConcurrencyTests.Disable_cleanup_insert_failure_rolls_back_document_version_and_index_job`,
  which creates a duplicate default embedding configuration and fails on the
  existing `IX_model_config_DefaultConfigurationType` unique key.
- `git diff --check` passed and no `appsettings` or migration file changed.
