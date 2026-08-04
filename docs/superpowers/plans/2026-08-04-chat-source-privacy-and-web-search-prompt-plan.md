# Chat Source Privacy and Web Search Prompt Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ensure private and group WeChat replies never expose web-search source lists, preserve those sources in conversation audit, prevent GLM Web Search from treating prompt metadata such as `participant` as the user's query, answer exact greetings without invoking retrieval or an LLM, and keep the knowledge-document list available after a physical-delete request.

**Architecture:** Enforce reply privacy at the shared `GroundedAnswerService` boundary while leaving the existing audit payload intact. Give the Web Search fallback a question-only final user message, introduce a shared deterministic greeting result in Application, and invoke it from both private and group processors after their existing eligibility gates but before template routing and retrieval. Retain the persisted `WebSearchShowSources` field for backward compatibility, but normalize all API writes and effective reads to `false` and remove its admin UI control. For physical-delete list summaries, replace the MySQL-incompatible runtime GUID collection predicate with the repository's bounded `GuidBatchQuery` pattern and preserve the current retry-state contract.

**Tech Stack:** .NET 10, ASP.NET Core, EF Core/MySQL, xUnit v3 with Microsoft Testing Platform, Vue 3, TypeScript, Vite, Element Plus, Vitest.

## Global Constraints

- Work directly on the current `master` checkout, as explicitly approved.
- Preserve all unrelated user changes and inspect the worktree before editing.
- Use `.local/.env` only through `WECHATROBOT_ENV_FILE`; never print secret values.
- Do not create a database migration or reindex Qdrant; the schema and vector data do not change.
- Do not start, retry, or complete physical-delete jobs as part of the list-query repair; the Worker remains authoritative for cleanup.
- Query cleanup-job IDs through `GuidBatchQuery.CreateBatches` and `GuidBatchQuery.BuildPredicate`; do not use a runtime `Guid[]` `Contains` predicate or per-document N+1 queries.
- Keep `WebSearchShowSources` in persisted/backend/frontend contracts for compatibility, but never allow it to affect outgoing chat text.
- Keep `ChatSource` records in `RetrievalAuditDraft.WebSearchSources`; only the user-visible answer is source-free.
- Greeting matching is exact after trim, terminal punctuation removal, and ASCII case normalization. It must not match business questions that merely contain a greeting.
- Run the narrow test first after every change, then the relevant complete suites and release builds before completion.
- Do not start the Worker for this implementation unless live acceptance is explicitly requested.

---

## Task 1: Lock down shared Web Search reply privacy and query shape

**Files:**

- Modify: `src/server/WechatRobot.Application/Conversations/GroundedAnswerService.cs`
- Modify: `tests/server/WechatRobot.UnitTests/Conversations/GroundedAnswerTests.cs`

- [ ] **Step 1: Add a failing unit test proving sources stay in audit but not in the reply**

Extend the existing successful Web Search test, or add a focused test with `WebSearchShowSources: true`, so the legacy flag cannot re-enable disclosure:

```csharp
Assert.Equal("联网答案", result.Decision.GroupText);
Assert.DoesNotContain("来源：", result.Decision.GroupText, StringComparison.Ordinal);
Assert.DoesNotContain("https://example.com", result.Decision.GroupText, StringComparison.Ordinal);
Assert.Equal("web_search", result.Audit.AnswerSource);
var source = Assert.Single(result.Audit.WebSearchSources);
Assert.Equal(new Uri("https://example.com/result"), source.Url);
```

- [ ] **Step 2: Add a failing unit test for the GLM Web Search final message**

Capture the `ChatCompletionRequest` passed to the fake chat client and assert that, when `WebSearchOptions` is present, the last user message is exactly the original question:

```csharp
var request = Assert.Single(chat.Requests, item => item.WebSearch is not null);
Assert.Equal("你好", request.Messages[^1].Content);
Assert.DoesNotContain("participant:", request.Messages[^1].Content, StringComparison.OrdinalIgnoreCase);
Assert.DoesNotContain("content:", request.Messages[^1].Content, StringComparison.OrdinalIgnoreCase);
```

Also keep an assertion that conversation summary/history, when provided, remains in earlier delimited messages so only the final query shape changes.

- [ ] **Step 3: Run the two focused tests and confirm the expected failures**

Run:

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --filter "FullyQualifiedName~GroundedAnswerTests"
```

Expected before implementation: the reply contains `来源：` when the flag is true, and the Web Search request's last message contains the `participant`/`content` envelope.

- [ ] **Step 4: Implement the smallest shared-boundary change**

In the valid Web Search result branch, always return the model text while still passing `sources` into the `Result` call:

```csharp
return Result(
    AnswerDecisionKind.Answer,
    text,
    evidence,
    confidence,
    contextPolicy,
    failureCode,
    inputSummaryJson,
    "web_search",
    null,
    sources,
    memoryRecall);
```

Delete `AppendSources`; it must have no remaining call sites.

Change only the Web Search form of `BuildFallbackPrompt` so its final user message is the original question without structural labels:

```csharp
messages.Add(new(
    "user",
    webSearch is null
        ? $"<<<UNTRUSTED_QUESTION_BEGIN>>>\n{FormatCurrentQuestion(request)}\n<<<UNTRUSTED_QUESTION_END>>>"
        : request.Question.Trim()));
```

Do not change the controlled-evidence prompt or model-knowledge fallback prompt in this task.

- [ ] **Step 5: Re-run focused unit tests**

Run the same filtered `GroundedAnswerTests` command. Expected: all pass, the displayed answer has no URLs/source heading, and audit sources remain populated.

---

## Task 2: Add a shared deterministic greeting result

**Files:**

- Create: `src/server/WechatRobot.Application/Conversations/ConversationalGreeting.cs`
- Create: `tests/server/WechatRobot.UnitTests/Conversations/ConversationalGreetingTests.cs`

- [ ] **Step 1: Write the failing matcher tests**

Cover the complete approved exact set and normalization:

```csharp
[Theory]
[InlineData("你好")]
[InlineData("您好！")]
[InlineData(" 嗨 ")]
[InlineData("哈喽。")]
[InlineData("HELLO")]
[InlineData("hi?")]
[InlineData("在吗？")]
public void Exact_greeting_matches(string text) =>
    Assert.True(ConversationalGreeting.TryCreate(text, out _));

[Theory]
[InlineData("你好，日本三年签证怎么办")]
[InlineData("hi 日本签证")]
[InlineData("在吗，韩国签证需要什么材料")]
[InlineData("participant")]
public void Business_question_does_not_match(string text) =>
    Assert.False(ConversationalGreeting.TryCreate(text, out _));
```

Assert the successful result contract as well:

```csharp
Assert.Equal("您好！请问有什么签证问题需要咨询？", result.Decision.GroupText);
Assert.Equal("conversational_greeting", result.Audit.AnswerSource);
Assert.Empty(result.Audit.WebSearchSources);
```

- [ ] **Step 2: Run the focused test and confirm it fails to compile**

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --filter "FullyQualifiedName~ConversationalGreetingTests"
```

Expected: `ConversationalGreeting` does not exist.

- [ ] **Step 3: Implement the shared component in Application**

Use a small static component with no infrastructure dependency:

```csharp
namespace WechatRobot.Application.Conversations;

public static class ConversationalGreeting
{
    private const string ReplyText = "您好！请问有什么签证问题需要咨询？";
    private static readonly HashSet<string> Greetings =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "你好", "您好", "嗨", "哈喽", "hello", "hi", "在吗"
        };

    public static bool TryCreate(string input, out GroundedAnswerResult result)
    {
        var normalized = Normalize(input);
        if (!Greetings.Contains(normalized))
        {
            result = null!;
            return false;
        }

        result = new(
            new(AnswerDecisionKind.Answer, ReplyText),
            new RetrievalAuditDraft(
                [], 0, 1, "conversational_greeting", "answer",
                AnswerSource: "conversational_greeting"));
        return true;
    }

    private static string Normalize(string value) =>
        value.Trim().TrimEnd('。', '！', '？', '!', '?').Trim();
}
```

If the actual `AnswerDecision` constructor requires an explicit named argument or the repository's nullable conventions require a different `TryCreate` signature, adjust the syntax without changing this behavior.

- [ ] **Step 4: Re-run the greeting tests**

Expected: all positive, negative, reply-text, and audit assertions pass.

---

## Task 3: Short-circuit greetings in private and group conversations

**Files:**

- Modify: `src/server/WechatRobot.Infrastructure/Agents/PrivateChatProcessor.cs`
- Modify: `src/server/WechatRobot.Application/Messaging/InboundMessageProcessor.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/PrivateChat/PrivateChatProcessorTests.cs`
- Modify: `tests/server/WechatRobot.UnitTests/Conversations/InboundMessageProcessorTests.cs`

- [ ] **Step 1: Add a failing private-chat processor regression test**

Submit an inbound private message containing `你好` with recording doubles for template routing, query rewrite/RAG, and answer generation. Assert:

```csharp
Assert.Empty(router.PrivateRequests);
Assert.Equal(0, answerAgent.CallCount);
Assert.Equal(0, queryRewrite.CallCount);
Assert.Equal("您好！请问有什么签证问题需要咨询？", sent.Text);
Assert.Equal("conversational_greeting", audit.AnswerSource);
Assert.Equal("answer", audit.Decision);
```

The test should not require a default chat model, proving the deterministic response does not fail when model configuration is unavailable.

- [ ] **Step 2: Add a failing group processor regression test**

Use the established `InboundMessageProcessorTests` fixture for an eligible group message `你好`. Keep group policy and Agent Framework intent gates successful, then assert:

```csharp
Assert.Equal(0, templateRouter.CallCount);
Assert.Equal(0, multiTurnRewrite.CallCount);
Assert.Equal(0, answerAgent.CallCount);
Assert.Equal("您好！请问有什么签证问题需要咨询？", repository.PersistedResult!.Decision.GroupText);
Assert.Equal("conversational_greeting", repository.PersistedResult.Audit.AnswerSource);
```

Also retain or add a negative case for `你好，日本签证需要什么材料` proving normal routing continues.

- [ ] **Step 3: Run the processor-focused tests and confirm failure**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~PrivateChatProcessorTests"
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --filter "FullyQualifiedName~InboundMessageProcessorTests"
```

Expected before wiring: downstream router/retrieval/model is invoked, or the private flow returns the unavailable fallback when no model exists.

- [ ] **Step 4: Wire the private short-circuit after command handling**

After unsupported/direct-ingest command branches and before default-model lookup:

```csharp
if (ConversationalGreeting.TryCreate(command.Body, out var greeting))
{
    await ReplyAsync(
        message,
        greeting.Decision.GroupText,
        cancellationToken,
        greeting);
    return;
}
```

Set `PrivateAnswerFallback.WebSearchShowSources` to `false` as compatibility hardening, even though Task 1 makes the shared service authoritative.

- [ ] **Step 5: Wire the group short-circuit after intent approval**

After policy/runtime/intent gates and the processing lease, but before template routing and context/RAG:

```csharp
if (ConversationalGreeting.TryCreate(request.Question, out var greeting))
{
    await EnsureLeaseAsync(request, cancellationToken);
    await conversations.PersistAnswerAndEnqueueAsync(request, greeting, cancellationToken);
    committed = true;
    return;
}
```

Do not move or bypass the existing group mention, inbound policy, paused-runtime, or Agent Framework intent checks.

- [ ] **Step 6: Re-run private and group focused tests**

Expected: greeting tests pass without downstream calls, business questions still follow the normal flow, and audit records use `conversational_greeting`.

---

## Task 4: Normalize legacy source-display configuration and remove the admin control

**Files:**

- Modify: `src/server/WechatRobot.Api/Groups/GroupEndpoints.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Groups/GroupConfigurationMySqlTests.cs`
- Modify: `src/web/wechatrobot-admin/src/components/groups/GroupKnowledgeAnswerPanel.vue`
- Modify: `src/web/wechatrobot-admin/src/components/groups/GroupKnowledgeAnswerPanel.spec.ts`
- Inspect and modify only if required by type/default assertions: `src/web/wechatrobot-admin/src/api/groups.ts`

- [ ] **Step 1: Add failing API normalization coverage**

POST/PUT a group configuration with the legacy request field set to `true`, then fetch it and inspect persistence:

```csharp
Assert.False(response.RootElement
    .GetProperty("answerFallback")
    .GetProperty("webSearchShowSources")
    .GetBoolean());
Assert.False(savedGroup.WebSearchShowSources);
```

This proves stale clients cannot re-enable source disclosure and effective reads never advertise it.

- [ ] **Step 2: Add a failing frontend test proving the control is absent**

Mount `GroupKnowledgeAnswerPanel` with `webSearchEnabled: true` and a legacy payload containing `webSearchShowSources: true`:

```ts
expect(wrapper.text()).not.toContain('在群消息中展示网页来源');
expect(wrapper.find('[data-testid="web-search-show-sources"]').exists()).toBe(false);
```

When the panel emits an updated fallback draft for another field, assert `webSearchShowSources` remains `false`.

- [ ] **Step 3: Run focused backend and frontend tests and confirm failures**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~GroupConfigurationMySqlTests"
Set-Location src/web/wechatrobot-admin
npm test -- --run src/components/groups/GroupKnowledgeAnswerPanel.spec.ts
```

- [ ] **Step 4: Normalize the backend compatibility field**

Keep the request/response DTO member but make all mappings authoritative:

```csharp
private static GroupAnswerFallbackSettings ToAnswerFallback(UpdateGroupRequest request) => new(
    request.WebSearchEnabled,
    request.ModelKnowledgeFallbackEnabled,
    WebSearchShowSources: false,
    // remaining existing fields unchanged
);
```

Apply the same `false` normalization when mapping persisted groups into effective settings and when saving:

```csharp
group.WebSearchShowSources = false;
```

Do not alter the EF entity, migration history, or model snapshot.

- [ ] **Step 5: Remove the frontend checkbox and normalize drafts**

Delete the Element Plus control and its label `在群消息中展示网页来源`. Retain the TypeScript contract member if the API type uses it, but initialize/emit it as `false` so old server payloads cannot revive the option in local component state.

- [ ] **Step 6: Re-run focused API and frontend tests**

Expected: API input `true` round-trips as `false`, persistence is false, the checkbox is absent, and unrelated fallback controls still emit correctly.

---

## Task 5: Repair the physical-delete list summary query

**Files:**

- Modify: `src/server/WechatRobot.Infrastructure/Knowledge/KnowledgeDocumentAdministrationQuery.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Knowledge/KnowledgeDocumentAdministrationMySqlTests.cs`

**Interfaces:**

- Consumes: `GuidBatchQuery.CreateBatches(IEnumerable<Guid>, int)` and `GuidBatchQuery.BuildPredicate<TEntity>(IReadOnlyCollection<Guid>, Expression<Func<TEntity, Guid>>)` from `WechatRobot.Infrastructure.Persistence`.
- Produces: private `LoadCleanupStatusesAsync(IReadOnlyCollection<Guid>, CancellationToken)` returning `Task<Dictionary<Guid, string>>`; public API response contracts remain unchanged.

- [ ] **Step 1: Expand the MySQL regression test to reproduce a mixed pending-delete page**

Replace the single-document setup in `Physical_delete_state_queries_translate_on_mysql` with two pending-delete documents and one normal document. Give the pending documents deterministic cleanup jobs in different states:

```csharp
var pendingDocument = new KnowledgeDocumentEntity
{
    Title = $"Pending Delete {suffix}",
    Status = "disabled",
    IsDeleteRequested = true
};
var failedDocument = new KnowledgeDocumentEntity
{
    Title = $"Failed Delete {suffix}",
    Status = "disabled",
    IsDeleteRequested = true
};
var normalDocument = new KnowledgeDocumentEntity
{
    Title = $"Normal Document {suffix}",
    Status = "uploaded"
};

setup.AddRange(
    pendingDocument,
    failedDocument,
    normalDocument,
    Version(pendingDocument.Id, 1, "pending-delete.txt", "disabled"),
    Version(failedDocument.Id, 1, "failed-delete.txt", "disabled"),
    Version(normalDocument.Id, 1, "normal.txt", "uploaded"),
    CleanupJob(pendingDocument.Id, "pending"),
    CleanupJob(failedDocument.Id, "deadLetter"));
```

Add these local helper methods to the test class so every seeded row satisfies the existing entity contract:

```csharp
private static KnowledgeDocumentVersionEntity Version(
    Guid documentId,
    int version,
    string fileName,
    string status) => new()
{
    KnowledgeDocumentId = documentId,
    Version = version,
    OriginalFileName = fileName,
    SafeFileName = fileName,
    ContentType = "text/plain",
    Sha256 = Guid.NewGuid().ToString("N").PadRight(64, '0'),
    ObjectKey = $"test/physical-delete-list/{Guid.NewGuid():N}/{fileName}",
    Status = status
};

private static DurableJobEntity CleanupJob(Guid documentId, string status) => new()
{
    Id = KnowledgeDocumentCleanupJobIdentity.Create(documentId),
    JobType = "CleanupKnowledgeDocument",
    Status = status,
    PayloadJson = "{}"
};
```

Query by the shared `suffix`, then assert:

```csharp
Assert.Equal(3, page.Items.Count);
Assert.False(page.Items.Single(item => item.Id == pendingDocument.Id).CanRetryPhysicalDelete);
Assert.True(page.Items.Single(item => item.Id == failedDocument.Id).CanRetryPhysicalDelete);
Assert.False(page.Items.Single(item => item.Id == normalDocument.Id).IsDeleteRequested);
```

- [ ] **Step 2: Run the focused MySQL test against the configured integration-test database**

```powershell
$env:WECHATROBOT_ENV_FILE = 'H:\Codex\WechatRobot\.local\.env'
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~KnowledgeDocumentAdministrationMySqlTests.Physical_delete_state_queries_translate_on_mysql"
```

Expected on the affected MySQL configuration before implementation: FAIL from the cleanup-job status query when EF executes the runtime GUID collection predicate. If the configured test server does not reproduce the Provider exception, retain this behavior regression and use the code review assertion in Step 5 to prove the unsafe predicate is removed; do not run test writes against an unconfirmed production database.

- [ ] **Step 3: Add the bounded cleanup-status loader**

In `KnowledgeDocumentAdministrationQuery`, add a focused loader using the same pattern as the class's version/tag loaders:

```csharp
private async Task<Dictionary<Guid, string>> LoadCleanupStatusesAsync(
    IReadOnlyCollection<Guid> cleanupJobIds,
    CancellationToken cancellationToken)
{
    var rows = await LoadBatchedAsync(
        cleanupJobIds,
        batch =>
        {
            var predicate = GuidBatchQuery.BuildPredicate<DurableJobEntity>(
                batch,
                job => job.Id);
            return database.DurableJobs
                .AsNoTracking()
                .Where(predicate)
                .Where(job => job.JobType == "CleanupKnowledgeDocument")
                .Select(job => new CleanupJobStatusRow(job.Id, job.Status))
                .ToArrayAsync(cancellationToken);
        });

    return rows.ToDictionary(row => row.Id, row => row.Status);
}
```

Add the private projection beside the other private records:

```csharp
private sealed record CleanupJobStatusRow(Guid Id, string Status);
```

- [ ] **Step 4: Replace the incompatible collection query**

Keep cleanup-job ID construction unchanged, then replace the direct `Contains`/`ToDictionaryAsync` query with:

```csharp
var cleanupStatuses = await LoadCleanupStatusesAsync(
    cleanupJobIds.Values.ToArray(),
    cancellationToken);
```

Do not catch database exceptions, change `CanRetryPhysicalDelete`, or execute cleanup inline.

- [ ] **Step 5: Run the focused MySQL test and inspect the source invariant**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~KnowledgeDocumentAdministrationMySqlTests.Physical_delete_state_queries_translate_on_mysql"
rg -n "cleanupJobIdValues\.Contains|GuidBatchQuery\.BuildPredicate<DurableJobEntity>" src/server/WechatRobot.Infrastructure/Knowledge/KnowledgeDocumentAdministrationQuery.cs
```

Expected: the test passes; the unsafe `cleanupJobIdValues.Contains` expression is absent; the durable-job query uses `BuildPredicate<DurableJobEntity>`.

- [ ] **Step 6: Commit the isolated query repair**

```powershell
git add src/server/WechatRobot.Infrastructure/Knowledge/KnowledgeDocumentAdministrationQuery.cs `
        tests/server/WechatRobot.IntegrationTests/Knowledge/KnowledgeDocumentAdministrationMySqlTests.cs
git diff --cached --check
git commit -m "fix: load physical cleanup states safely"
```

---

## Task 6: Cross-boundary regression verification

**Files:**

- Review: all files changed in Tasks 1–4
- Update only if behavior documentation drift is found: `docs/superpowers/specs/2026-08-04-chat-source-privacy-and-web-search-prompt-design.md`

- [ ] **Step 1: Run complete backend unit tests**

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj
```

- [ ] **Step 2: Run relevant backend integration tests**

Load `.local` without displaying values, then run:

```powershell
$env:WECHATROBOT_ENV_FILE = 'H:\Codex\WechatRobot\.local\.env'
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~PrivateChatProcessorTests|FullyQualifiedName~GroupConfigurationMySqlTests|FullyQualifiedName~KnowledgeDocumentAdministrationMySqlTests"
```

If the test framework does not accept the compound filter syntax, run each class separately. Report unavailable MySQL or external infrastructure as a blocker rather than substituting stale evidence.

- [ ] **Step 3: Run frontend verification**

From `src/web/wechatrobot-admin`:

```powershell
npm run typecheck
npm test -- --run
npm run build
```

- [ ] **Step 4: Build the deployable backend projects**

```powershell
dotnet build src/server/WechatRobot.Api/WechatRobot.Api.csproj -c Release
dotnet build src/server/WechatRobot.Worker/WechatRobot.Worker.csproj -c Release
```

- [ ] **Step 5: Check diff hygiene and source-leak regressions**

```powershell
rg -n "AppendSources|在群消息中展示网页来源|WebSearchShowSources:\s*true|cleanupJobIdValues\.Contains" src tests
git diff --check
git status --short
git diff --stat
```

Expected: no outgoing-answer source append helper, no UI label, no hardcoded private `true`, and no whitespace errors. Existing persistence/model-snapshot references are allowed.

- [ ] **Step 6: Review every requirement against the approved spec**

Confirm explicitly:

- Private and group outgoing replies cannot show an appended source section.
- Audit still contains sanitized Web Search sources.
- The Web Search request's last user message is the user's original question only.
- Exact greetings avoid template routing, RAG/query rewrite, and model calls.
- Business questions containing a greeting do not short-circuit.
- Group policy/mention/intent rules still run before the greeting response.
- The legacy API field is accepted but normalized to false.
- No database migration, Qdrant change, or knowledge reindex was introduced.
- Pending physical-delete documents remain listable, and cleanup retryability retains its existing meaning.

- [ ] **Step 7: Commit the completed implementation**

Review staged files so no `.local` data or unrelated changes are included, then:

```powershell
git add src/server/WechatRobot.Application/Conversations/GroundedAnswerService.cs `
        src/server/WechatRobot.Application/Conversations/ConversationalGreeting.cs `
        src/server/WechatRobot.Application/Messaging/InboundMessageProcessor.cs `
        src/server/WechatRobot.Infrastructure/Agents/PrivateChatProcessor.cs `
        src/server/WechatRobot.Api/Groups/GroupEndpoints.cs `
        src/web/wechatrobot-admin/src/components/groups/GroupKnowledgeAnswerPanel.vue `
        src/web/wechatrobot-admin/src/components/groups/GroupKnowledgeAnswerPanel.spec.ts `
        tests/server/WechatRobot.UnitTests/Conversations/GroundedAnswerTests.cs `
        tests/server/WechatRobot.UnitTests/Conversations/ConversationalGreetingTests.cs `
        tests/server/WechatRobot.UnitTests/Conversations/InboundMessageProcessorTests.cs `
        tests/server/WechatRobot.IntegrationTests/PrivateChat/PrivateChatProcessorTests.cs `
        tests/server/WechatRobot.IntegrationTests/Groups/GroupConfigurationMySqlTests.cs
git diff --cached --check
git commit -m "fix: keep chat sources private"
```

If `src/web/wechatrobot-admin/src/api/groups.ts` or another directly required compatibility file changes during implementation, add it explicitly after reviewing its diff. Do not use `git add .`.

---

## Task 7: Release handoff (only after all verification passes)

**Files:**

- Generate under the repository's existing ignored/local release output convention; do not commit binaries.

- [ ] **Step 1: Publish API and Worker from the verified commit**

Use the repository's existing release script if one exists and matches the current deployment contract. Otherwise publish explicitly to bounded output directories:

```powershell
dotnet publish src/server/WechatRobot.Api/WechatRobot.Api.csproj -c Release -o artifacts/release/api
dotnet publish src/server/WechatRobot.Worker/WechatRobot.Worker.csproj -c Release -o artifacts/release/worker
```

- [ ] **Step 2: Package the frontend build and backend outputs**

Create a versioned ZIP using the established repository packaging script or release layout. Verify the archive contains API, Worker, and admin assets and excludes `.local`, `.env`, logs, test outputs, and source-only secrets.

- [ ] **Step 3: Report deployment impact**

State that production requires replacing/restarting API and Worker plus deploying the frontend assets. State that MySQL migration and knowledge reindex are not required. Do not deploy or restart production without separate authorization.
